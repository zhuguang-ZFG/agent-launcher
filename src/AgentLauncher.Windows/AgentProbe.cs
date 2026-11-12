using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentLauncher.Core;
using Microsoft.Win32.SafeHandles;

namespace AgentLauncher.Windows;

public sealed record ProbeResult(int? ExitCode, bool TimedOut, bool Truncated, string Output, string Error);

/// <summary>Runs a bounded version probe inside its own Job with redirected pipes; never leaves the probe tree behind.</summary>
public static class AgentProbe
{
    public const int DefaultMaxBytes = 64 * 1024;

    public static async Task<AgentCapability> ProbeCapabilityAsync(string agentId, AgentProfile profile, string cwd, TimeSpan timeout, CancellationToken ct = default)
    {
        var spec = Commands.Resolve(agentId, new AgentProfile { Executable = profile.Executable, Arguments = [] });
        var fingerprint = Fingerprint(spec);
        var result = await RunAsync(spec with { Arguments = [.. spec.Arguments, "--version"] }, cwd, timeout, ct: ct);
        if (fingerprint != Fingerprint(spec)) throw new InvalidDataException("版本核对期间程序已变化，请重试核对。");
        var version = result is { ExitCode: 0 } ? AgentVersion.Parse(result.Output + "\n" + result.Error) : null;
        var note = result.TimedOut ? "版本探测超时；预设不可用，仍可手动填写参数。"
            : result.Truncated ? "版本输出超出上限（可能不是带 --version 的 CLI）；预设不可用。"
            : result.ExitCode is null ? "版本探测未得到退出码；预设不可用。"
            : result.ExitCode != 0 ? $"版本探测退出码 {result.ExitCode}；预设不可用，仍可手动填写参数。"
            : version is null ? "版本输出无法识别；预设不可用，仍可手动填写参数。"
            : "版本与预设已按此安装核对。";
        return new AgentCapability
        {
            AgentId = agentId,
            ProgramPath = spec.Executable,
            Fingerprint = fingerprint,
            Version = version,
            ProbedAt = DateTimeOffset.UtcNow,
            Note = note
        };
    }

    /// <summary>Node packages share a runtime; bind their entry and nearest package manifest as well. User arguments are not program identity.</summary>
    public static string Fingerprint(CommandSpec command)
    {
        if (command.Adapter == "exe") return Fingerprint(command.Executable);
        if (command.Adapter != "node-shim" || command.Arguments.Length == 0 || !Path.IsPathFullyQualified(command.Arguments[0]))
            throw new InvalidDataException("程序适配器或 Node 入口身份无法核对，请重新选择程序。");
        var script = Path.GetFullPath(command.Arguments[0]);
        var entryHash = BoundedContentHash(script, 64L * 1024 * 1024);
        string? manifest = null, manifestHash = null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(script)!);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "package.json");
            try { manifestHash = BoundedContentHash(candidate, 1024 * 1024); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            manifest = candidate.ToUpperInvariant();
            break;
        }
        var identity = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Adapter = "node-shim-v2", Runtime = Path.GetFullPath(command.Executable).ToUpperInvariant(),
            RuntimeHash = Fingerprint(command.Executable), Entry = script.ToUpperInvariant(), EntryHash = entryHash,
            Manifest = manifest, ManifestHash = manifestHash
        });
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    private static string BoundedContentHash(string path, long maxBytes)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maxBytes) throw new InvalidDataException("程序身份文件超出核对上限，请人工核对后重新选择程序。");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Content hash for runs; oversized binaries fall back to size and timestamp so the probe stays bounded.</summary>
    public static string Fingerprint(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 64L * 1024 * 1024)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{info.Length}|{info.LastWriteTimeUtc.Ticks}|{path.ToUpperInvariant()}")));
    }

    public static async Task<ProbeResult> RunAsync(CommandSpec command, string cwd, TimeSpan timeout, int maxBytes = DefaultMaxBytes, CancellationToken ct = default) =>
        await Task.Run(() => Run(command, cwd, timeout, maxBytes, ct), ct);

    private static ProbeResult Run(CommandSpec command, string cwd, TimeSpan timeout, int maxBytes, CancellationToken ct)
    {
        if (maxBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        ct.ThrowIfCancellationRequested();
        using var job = new Job(@"Local\AL-probe-" + Guid.NewGuid().ToString("N"), memory: 512 * ResourcePolicy.MiB);
        var attributes = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = 1 };
        IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero, errRead = IntPtr.Zero, errWrite = IntPtr.Zero, inRead = IntPtr.Zero, inWrite = IntPtr.Zero;
        IntPtr attrs = IntPtr.Zero, jobs = IntPtr.Zero, handles = IntPtr.Zero, process = IntPtr.Zero, thread = IntPtr.Zero;
        var initialized = false;
        FileStream? outStream = null, errStream = null;
        void Close(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            Native.CloseHandle(handle); handle = IntPtr.Zero;
        }
        try
        {
            Native.Check(Native.CreatePipe(out outRead, out outWrite, ref attributes, 0));
            Native.Check(Native.CreatePipe(out errRead, out errWrite, ref attributes, 0));
            Native.Check(Native.CreatePipe(out inRead, out inWrite, ref attributes, 0));
            // The child inherits only the ends it uses; inheritance is cleared from the parent-owned ends before creation.
            Native.Check(Native.SetHandleInformation(outRead, 1, 0));
            Native.Check(Native.SetHandleInformation(errRead, 1, 0));
            Native.Check(Native.SetHandleInformation(inWrite, 1, 0));
            UIntPtr size = UIntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrs = Marshal.AllocHGlobal(checked((int)size)); jobs = Marshal.AllocHGlobal(IntPtr.Size); handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Native.Check(Native.InitializeProcThreadAttributeList(attrs, 1, 0, ref size)); initialized = true;
            Marshal.WriteIntPtr(jobs, job.Handle.DangerousGetHandle());
            Native.Check(Native.UpdateProcThreadAttribute(attrs, 0, (UIntPtr)0x0002000D, jobs, (UIntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            var startup = new Native.StartupInfoEx
            {
                Info = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100, StdInput = inRead, StdOutput = outWrite, StdError = errWrite },
                Attributes = attrs
            };
            var line = new StringBuilder(string.Join(" ", new[] { command.Executable }.Concat(command.Arguments).Select(ProtectedProcess.Quote)));
            Native.Check(Native.CreateProcess(command.Executable, line, IntPtr.Zero, IntPtr.Zero, true, 0x00080000 | 0x4 | 0x08000000, IntPtr.Zero, cwd, ref startup, out var info));
            process = info.Process; thread = info.Thread;
            Close(ref outWrite); Close(ref errWrite); Close(ref inRead); Close(ref inWrite);
            outStream = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 4096, false); outRead = IntPtr.Zero;
            errStream = new FileStream(new SafeFileHandle(errRead, true), FileAccess.Read, 4096, false); errRead = IntPtr.Zero;
            if (Native.ResumeThread(thread) == uint.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var outTask = Task.Run(() => Drain(outStream, maxBytes));
            var errTask = Task.Run(() => Drain(errStream, maxBytes));
            var wait = Native.WaitForSingleObject(process, (uint)Math.Clamp(timeout.TotalMilliseconds, 1, 120000));
            var timedOut = wait == 258;
            if (wait == uint.MaxValue) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ct.ThrowIfCancellationRequested();
            // Terminating the Job after the main process also drops anything a probe command left running.
            Native.Check(Native.TerminateJobObject(job.Handle, 1));
            Native.WaitForSingleObject(process, 5000);
            int? exit = Native.GetExitCodeProcess(process, out var code) ? unchecked((int)code) : null;
            if (!Task.WaitAll([outTask, errTask], 3000))
            {
                outStream.Dispose(); errStream.Dispose();
                Task.WaitAll([outTask, errTask], 1000);
            }
            var output = outTask.IsCompletedSuccessfully ? outTask.Result.Text : "";
            var error = errTask.IsCompletedSuccessfully ? errTask.Result.Text : "";
            return new ProbeResult(exit, timedOut, (outTask.IsCompletedSuccessfully && outTask.Result.Truncated) || (errTask.IsCompletedSuccessfully && errTask.Result.Truncated), output, error);
        }
        finally
        {
            // Any failure path (including a probe that never resumed) must not leave the probe tree behind.
            Native.TerminateJobObject(job.Handle, 1);
            outStream?.Dispose(); errStream?.Dispose();
            Close(ref outRead); Close(ref outWrite); Close(ref errRead); Close(ref errWrite); Close(ref inRead); Close(ref inWrite);
            Close(ref thread); Close(ref process);
            if (initialized) Native.DeleteProcThreadAttributeList(attrs);
            if (attrs != IntPtr.Zero) Marshal.FreeHGlobal(attrs);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }

    private static (string Text, bool Truncated) Drain(Stream stream, int maxBytes)
    {
        var buffer = new byte[8192]; var memory = new MemoryStream(); var truncated = false;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var take = Math.Min(read, maxBytes - (int)memory.Length);
                if (take > 0) memory.Write(buffer, 0, take);
                if (read > take) { truncated = true; break; }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        return (Encoding.UTF8.GetString(memory.ToArray()), truncated);
    }
}
