using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>A per-pane, current-user-only endpoint. Requests contain no prompts or tool arguments.</summary>
public sealed class OmpMemoryGuard : IDisposable
{
    public const string ExtensionFile = "omp-memory-guard.mjs";
    public const string PipeEnvironment = "AGENT_LAUNCHER_MEMORY_PIPE";
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    private long handshakeTicks;
    private Admission? lastDecision;
    public Admission? LastDecision => Volatile.Read(ref lastDecision);
    public string PipeName { get; } = "AgentLauncher-memory-" + Guid.NewGuid().ToString("N");

    /// <summary>True once the extension's load hello reached this endpoint; the Runner discloses fail-open tools when false.</summary>
    public bool HandshakeConfirmed => Volatile.Read(ref handshakeTicks) != 0;

    public static CommandSpec Attach(CommandSpec command)
    {
        var path = Path.Combine(AppContext.BaseDirectory, ExtensionFile);
        if (!File.Exists(path)) throw new IOException("启动器缺少 Omp 内存保护组件，请使用完整发布包。");
        var offset = Commands.MemoryGuardInjectionOffset(command);
        var flag = command.Arguments.Skip(offset).TakeWhile(arg => arg != "--").Any(arg => arg == "--trusted-extension" || arg.StartsWith("--trusted-extension=", StringComparison.Ordinal))
            ? "--trusted-extension" : "--extension";
        return command with { Arguments = [..command.Arguments.Take(offset), flag, path, ..command.Arguments.Skip(offset)] };
    }
    /// <summary>Inverse of <see cref="Attach"/>: version probes run the bare CLI, while plan identity keeps the injected arguments.</summary>
    public static CommandSpec Detach(CommandSpec command)
    {
        var offset = Commands.MemoryGuardInjectionOffset(command);
        if (command.Arguments.Length > offset + 1 && command.Arguments[offset] is "--extension" or "--trusted-extension"
            && string.Equals(Path.GetFileName(command.Arguments[offset + 1]), ExtensionFile, StringComparison.OrdinalIgnoreCase))
            return command with { Arguments = [.. command.Arguments.Take(offset), .. command.Arguments.Skip(offset + 2)] };
        return command;
    }
    public static bool IsAttached(CommandSpec command)
    {
        var offset = Commands.MemoryGuardInjectionOffset(command);
        if (command.Arguments.Length <= offset + 1 || command.Arguments[offset] is not ("--extension" or "--trusted-extension")
            || !string.Equals(Path.GetFileName(command.Arguments[offset + 1]), ExtensionFile, StringComparison.OrdinalIgnoreCase)) return false;
        // Frozen queues may refer to an identical component in another complete package.
        // Changed or missing components need a new reviewed plan, not silent unguarded execution.
        var source = new FileInfo(command.Arguments[offset + 1]);
        if (!source.Exists || source.Length > 128 * 1024 || !File.ReadAllBytes(source.FullName).SequenceEqual(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, ExtensionFile))))
            throw new IOException("排队记录的 Omp 内存保护组件已变化，请重新入队以绑定当前版本。");
        return true;
    }

    public OmpMemoryGuard(Job job, Job? pool, ulong physicalFloor, ulong commitFloor)
    {
        // Create the first listening instance before the protected process can run.
        var first = CreatePipe();
        server = Serve(first, job, pool, physicalFloor, commitFloor);
    }

    private NamedPipeServerStream CreatePipe() => new(PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 1024, 1024);

    private async Task Serve(NamedPipeServerStream first, Job job, Job? pool, ulong physicalFloor, ulong commitFloor)
    {
        var pipe = first;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop.Token);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(1));
                    var request = new byte[1];
                    var length = await pipe.ReadAsync(request, deadline.Token);
                    if (length == 1 && request[0] == 2)
                    {
                        // Load hello: the extension is active. Acknowledge without querying memory or gating tools.
                        Interlocked.Exchange(ref handshakeTicks, DateTimeOffset.UtcNow.Ticks);
                        var hello = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { allowed = true, reason = "内存保护已加载" }) + "\n");
                        await pipe.WriteAsync(hello, deadline.Token);
                        await pipe.FlushAsync(deadline.Token);
                    }
                    else if (length == 1 && request[0] == 1)
                    {
                        Admission decision;
                        try
                        {
                            var memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
                            Native.Check(Native.GlobalMemoryStatusEx(ref memory));
                            var sample = new MemorySnapshot(memory.TotalPhysical, memory.AvailablePhysical, memory.TotalPage, memory.AvailablePage, 0, DateTimeOffset.UtcNow);
#if INTEGRATION_TESTS
                            // Match the monitor's isolated system sampling boundary. Native Job
                            // counters remain real; production packages cannot select this sample.
                            if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") == "1")
                                sample = new(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
#endif
                            decision = ToolMemoryPolicy.Check(sample, job.QueryMemory()?.LimitBytes ?? 0, job.QueryCurrentCommit(),
                                pool is null ? null : pool.QueryMemory()?.LimitBytes ?? 0, pool?.QueryCurrentCommit(), physicalFloor, commitFloor);
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            decision = new(false, "内存保护暂不可用，暂停新增工具；已有工作继续运行。");
                        }
                        Volatile.Write(ref lastDecision, decision);
                        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { allowed = decision.Allowed, reason = decision.Reason }) + "\n");
                        await pipe.WriteAsync(bytes, deadline.Token);
                        await pipe.FlushAsync(deadline.Token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
                finally { pipe.Dispose(); }
                if (stop.IsCancellationRequested) break;
                pipe = CreatePipe();
            }
        }
        finally { pipe.Dispose(); }
    }

    /// <summary>Bounded wait for the extension's load hello. A timeout returns false; the Runner then discloses fail-open tools.</summary>
    public bool WaitForHandshake(TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!HandshakeConfirmed)
            {
                if (watch.Elapsed >= timeout || stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100))) return false;
            }
            return true;
        }
        catch (ObjectDisposedException) { return HandshakeConfirmed; }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { server.GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or OperationCanceledException) { }
        stop.Dispose();
    }
}
