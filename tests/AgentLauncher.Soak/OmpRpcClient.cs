using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SoakRunner;

internal sealed class ProtocolException(string message) : Exception(message);

internal sealed class RpcCommandException(string message) : Exception(message);

internal sealed class VersionProbe
{
    public int ExitCode { get; init; }
    public string Raw { get; init; } = "";
    public string? Parsed { get; init; }
}

internal sealed class BashReply
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public int? ExitCode { get; init; }
    public bool Cancelled { get; init; }
    public List<string> DataKeys { get; init; } = [];
}

internal sealed class ShutdownResult
{
    public int ExitCode { get; init; }
    public bool Forced { get; init; }
}

internal sealed class OmpRpcClient : IDisposable
{
    private static readonly string[] RpcArguments =
    [
        "--mode", "rpc",
        "--model", "launcher-soak/tool-only",
        "--no-session",
        "--no-title",
        "--no-extensions",
        "--no-skills",
        "--no-rules",
        "--no-context-files",
        "--no-lsp",
        "--no-pty",
        "--no-ui"
    ];

    private const int MaxLineBytes = 1024 * 1024;
    private readonly Process process;
    private readonly Stream input;
    private readonly Stream output;
    private readonly Thread readerThread;
    private readonly Thread stderrThread;
    private readonly FilePump stderrPump;
    private readonly TaskCompletionSource<ReadyInfo> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> fatal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BashReply>> pending = new(StringComparer.Ordinal);
    private int sequence;
    private volatile int shutdownStarted;
    private bool disposed;
    private ShutdownResult? shutdownResult;
    private volatile bool supportsExcludeFromContext;
    private volatile int lateResponses;

    public ReadyInfo Ready { get; }
    public int ProcessId { get; }
    public long ProcessStartTimeUtc { get; }
    public string StderrPath { get; }
    public bool StderrTruncated => stderrPump.Truncated;
    public long StderrCharacters => stderrPump.WrittenCharacters;
    public long LateResponseCount => lateResponses;

    public OmpRpcClient(string ompPath, string workspace, IReadOnlyDictionary<string, string> environment,
        string stderrPath, TimeSpan readyTimeout)
    {
        if (!File.Exists(ompPath)) throw new ProtocolException("omp 可执行文件不存在: " + ompPath);
        if (!Directory.Exists(workspace)) throw new ProtocolException("omp 工作目录不存在: " + workspace);
        StderrPath = Path.GetFullPath(stderrPath);
        PrepareModelFreeConfiguration(environment);
        var startInfo = new ProcessStartInfo
        {
            FileName = ompPath,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in RpcArguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment.Clear();
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new ProtocolException("omp 进程启动失败。");
        ProcessId = process.Id;
        try { ProcessStartTimeUtc = process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { ProcessStartTimeUtc = 0; }
        input = process.StandardInput.BaseStream;
        output = process.StandardOutput.BaseStream;
        stderrPump = new FilePump(StderrPath, 256 * 1024);
        stderrThread = new Thread(() => stderrPump.Pump(process.StandardError)) { IsBackground = true, Name = "omp-stderr" };
        readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "omp-rpc-stdout" };
        stderrThread.Start();
        readerThread.Start();
        try
        {
            Ready = WaitForReady(readyTimeout);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private ReadyInfo WaitForReady(TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        var delay = Task.Delay(Timeout.InfiniteTimeSpan, timeoutSource.Token);
        var completed = Task.WhenAny(new Task[] { ready.Task, fatal.Task, delay }).GetAwaiter().GetResult();
        timeoutSource.Cancel();
        if (fatal.Task.IsCompleted) throw StoredException(fatal);
        if (completed == ready.Task) return ready.Task.GetAwaiter().GetResult();
        throw new ProtocolException("omp 未在限定时间内发送 ready 帧。");
    }

    private static void PrepareModelFreeConfiguration(IReadOnlyDictionary<string, string> environment)
    {
        var agentDirectory = environment["PI_CODING_AGENT_DIR"];
        Directory.CreateDirectory(agentDirectory);
        var models = Path.Combine(agentDirectory, "models.yml");
        var config = Path.Combine(agentDirectory, "config.yml");
        if (File.Exists(models) || File.Exists(config)) throw new ProtocolException("Fresh isolated model configuration required.");
        File.WriteAllText(models, """
providers:
  launcher-soak:
    baseUrl: http://127.0.0.1:1/v1
    auth: none
    api: openai-completions
    models:
      - id: tool-only
        name: Model-free tool transport sentinel
        contextWindow: 32768
        maxTokens: 1024
""", new UTF8Encoding(false));
        File.WriteAllText(config, """
disabledProviders:
  - ollama
  - lm-studio
  - llama.cpp
""", new UTF8Encoding(false));
    }

    public BashReply Bash(string command, TimeSpan timeout)
    {
        ThrowIfFatal();
        if (shutdownStarted != 0) throw new InvalidOperationException("omp 会话正在关闭。");
        var id = "soak-" + (++sequence).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<BashReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new ProtocolException("重复的 RPC 请求 ID。");
        try
        {
            var commandObject = new JsonObject
            {
                ["id"] = id,
                ["type"] = "bash",
                ["command"] = command
            };
            if (supportsExcludeFromContext) commandObject["excludeFromContext"] = true;
            var bytes = Encoding.UTF8.GetBytes(commandObject.ToJsonString());
            if (bytes.Length + 1 > Math.Min(MaxLineBytes, Ready.MaxFrameBytes)) throw new ProtocolException("bash 请求超出单帧边界。");
            input.Write(bytes, 0, bytes.Length);
            input.WriteByte((byte)'\n');
            input.Flush();
            using var timeoutSource = new CancellationTokenSource(timeout);
            var delay = Task.Delay(Timeout.InfiniteTimeSpan, timeoutSource.Token);
            var completed = Task.WhenAny(new Task[] { completion.Task, fatal.Task, delay }).GetAwaiter().GetResult();
            timeoutSource.Cancel();
            if (completed == completion.Task) return completion.Task.GetAwaiter().GetResult();
            if (fatal.Task.IsCompleted) throw new ProtocolException("RPC 读取失败: " + StoredException(fatal).Message);
            throw new TimeoutException($"bash 命令在 {timeout.TotalSeconds:0} 秒内没有响应。");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private void ReaderLoop()
    {
        try
        {
            ReadBoundedLines(output, MaxLineBytes, HandleLine, Fail);
            if (!ready.Task.IsCompleted) Fail(new ProtocolException("omp stdout 在 ready 帧之前结束。"));
            else if (shutdownStarted == 0) Fail(new ProtocolException("omp stdout 在会话关闭之前结束。"));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e is ProtocolException ? e : new ProtocolException("读取 omp stdout 失败: " + e.Message));
        }
    }

    internal static void ReadBoundedLines(Stream stream, int maximumBytes, Action<string> onLine, Action<Exception> onError)
    {
        var utf8 = new UTF8Encoding(false, true);
        using var line = new MemoryStream(4096);
        var buffer = new byte[8192];
        var overflow = false;
        var reportedOverflow = false;
        while (true)
        {
            int read;
            try { read = stream.Read(buffer, 0, buffer.Length); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { return; }
            if (read <= 0) break;
            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (value == (byte)'\n')
                {
                    if (overflow)
                    {
                        if (!reportedOverflow)
                        {
                            reportedOverflow = true;
                            onError(new ProtocolException("omp stdout 单帧超出边界。"));
                        }
                    }
                    else
                    {
                        var bytes = line.ToArray();
                        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') Array.Resize(ref bytes, bytes.Length - 1);
                        if (bytes.Length > 0)
                        {
                            string text;
                            try { text = utf8.GetString(bytes); }
                            catch (DecoderFallbackException e) { onError(new ProtocolException("omp stdout 不是有效 UTF-8: " + e.Message)); text = ""; }
                            if (text.Length > 0)
                            {
                                try { onLine(text); }
                                catch (Exception e) when (e is not OutOfMemoryException) { onError(e); }
                            }
                        }
                    }
                    line.SetLength(0);
                    overflow = false;
                }
                else if (line.Length < maximumBytes)
                {
                    line.WriteByte(value);
                }
                else
                {
                    overflow = true;
                    if (!reportedOverflow)
                    {
                        reportedOverflow = true;
                        onError(new ProtocolException("omp stdout 单帧超出边界。"));
                    }
                }
            }
        }
        if (overflow && !reportedOverflow) onError(new ProtocolException("omp stdout 单帧超出边界。"));
        else if (!overflow && line.Length > 0) onError(new ProtocolException("omp stdout 在完整换行帧发布前结束。"));
    }

    private void HandleLine(string line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException e) { Fail(new ProtocolException("omp stdout JSON 无法解析: " + e.Message)); return; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                return;
            var type = typeElement.GetString();
            switch (type)
            {
                case "ready":
                    HandleReady(root);
                    break;
                case "response":
                    HandleResponse(root);
                    break;
                case "rpc_chunk":
                    Fail(new ProtocolException("未协商协议 v2，却收到 rpc_chunk 帧。"));
                    break;
                case "extension_ui_request":
                    Fail(new ProtocolException("未启用 UI，却收到 extension_ui_request；拒绝代替用户应答。"));
                    break;
                case "prompt_result":
                    Fail(new ProtocolException("未发送 prompt，却收到 prompt_result。"));
                    break;
                default:
                    break;
            }
        }
    }

    private void HandleReady(JsonElement root)
    {
        if (ready.Task.IsCompleted || fatal.Task.IsCompleted) return;
        if (!root.TryGetProperty("protocolVersion", out var versionElement) || !versionElement.TryGetInt32(out var protocolVersion))
        {
            Fail(new ProtocolException("ready 帧缺少 protocolVersion。"));
            return;
        }
        var supported = new List<int>();
        if (root.TryGetProperty("supportedProtocolVersions", out var supportedElement) && supportedElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in supportedElement.EnumerateArray())
                if (element.TryGetInt32(out var supportedVersion)) supported.Add(supportedVersion);
        }
        if (protocolVersion != 1)
        {
            Fail(new ProtocolException("omp 未声明协议 v1 支持。"));
            return;
        }
        if (!root.TryGetProperty("maxFrameBytes", out var frameElement) || !frameElement.TryGetInt32(out var maxFrameBytes) || maxFrameBytes < 4096)
        {
            Fail(new ProtocolException("ready 帧缺少有效的 maxFrameBytes。"));
            return;
        }
        var reassembled = 0;
        if (root.TryGetProperty("maxReassembledFrameBytes", out var reassembledElement)) reassembledElement.TryGetInt32(out reassembled);
        var exclude = root.TryGetProperty("supportsBashExcludeFromContext", out var excludeElement) &&
                      (excludeElement.ValueKind is JsonValueKind.True or JsonValueKind.False) && excludeElement.GetBoolean();
        supportsExcludeFromContext = exclude;
        ready.TrySetResult(new ReadyInfo
        {
            ProtocolVersion = protocolVersion,
            SupportedProtocolVersions = [.. supported],
            MaxFrameBytes = maxFrameBytes,
            MaxReassembledFrameBytes = reassembled,
            SupportsBashExcludeFromContext = exclude
        });
    }

    private void HandleResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            Fail(new ProtocolException("response 帧缺少 id。"));
            return;
        }
        var id = idElement.GetString()!;
        if (!root.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String)
        {
            Fail(new ProtocolException("response 帧缺少 command。"));
            return;
        }
        var command = commandElement.GetString();
        if (!string.Equals(command, "bash", StringComparison.Ordinal))
        {
            Fail(new ProtocolException("收到的 response 命令不是 bash: " + command));
            return;
        }
        if (!root.TryGetProperty("success", out var successElement) || successElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            Fail(new ProtocolException("bash response 缺少 success。"));
            return;
        }
        if (!pending.TryGetValue(id, out var completion))
        {
            lateResponses++;
            return;
        }
        var success = successElement.GetBoolean();
        var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
            ? JsonIo.Bounded(errorElement.GetString(), 1000)
            : null;
        var keys = new List<string>();
        int? exitCode = null;
        var cancelled = false;
        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in dataElement.EnumerateObject())
            {
                if (keys.Count < 16) keys.Add(property.Name);
                if ((property.Name is "exitCode" or "exit_code") && property.Value.TryGetInt32(out var code)) exitCode = code;
                if (property.Name == "cancelled" && (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)) cancelled = property.Value.GetBoolean();
            }
        }
        completion.TrySetResult(new BashReply
        {
            Success = success,
            Error = error,
            ExitCode = exitCode,
            Cancelled = cancelled,
            DataKeys = keys
        });
    }

    private void ThrowIfFatal()
    {
        if (fatal.Task.IsCompleted) throw new ProtocolException("RPC 会话已失败: " + StoredException(fatal).Message);
    }

    public void EnsureRunning()
    {
        ThrowIfFatal();
        if (process.HasExited) throw new ExecutionFailureException("omp 在停止信号前退出。");
    }

    public void EnsureProtocolHealthy() => ThrowIfFatal();

    private static Exception StoredException(TaskCompletionSource<Exception> source)
    {
        var exception = source.Task.GetAwaiter().GetResult();
        return exception;
    }

    private void Fail(Exception exception)
    {
        fatal.TrySetResult(exception);
        ready.TrySetException(exception);
        foreach (var pair in pending) pair.Value.TrySetException(exception);
    }

    public ShutdownResult Shutdown(TimeSpan timeout)
    {
        if (shutdownStarted != 0)
            return shutdownResult ?? new ShutdownResult { ExitCode = -1, Forced = true };
        shutdownStarted = 1;
        try { input.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
        var exited = process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));
        var forced = false;
        if (!exited)
        {
            forced = true;
            try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.WaitForExit(5000);
        }
        if (!readerThread.Join(5000) || !stderrThread.Join(5000))
            throw new ExecutionFailureException("omp 输出未能在关闭期限内排干。");
        if (!process.HasExited) throw new ExecutionFailureException("omp 退出未得到确认。");
        if (stderrPump.Error is { } logError) throw new ExecutionFailureException("omp 日志写入失败: " + logError);
        var exit = -1;
        try { if (process.HasExited) exit = process.ExitCode; } catch (InvalidOperationException) { }
        shutdownResult = new ShutdownResult { ExitCode = exit, Forced = forced };
        return shutdownResult;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { Shutdown(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        try { input.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        process.Dispose();
    }

    public static VersionProbe ProbeVersion(string ompPath, IReadOnlyDictionary<string, string> environment, string cwd,
        TimeSpan timeout)
    {
        var startInfo = ProcessTools.CreateStartInfo(ompPath, ["--version"], cwd);
        startInfo.Environment.Clear();
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        var result = ProcessTools.RunBounded(startInfo, timeout, 64 * 1024);
        var combined = result.StdOut + "\n" + result.StdErr;
        if (result.TimedOut) throw new ProtocolException("omp --version 超时。");
        if (result.ExitCode != 0) throw new ProtocolException($"omp --version 退出码 {result.ExitCode}: {JsonIo.Bounded(result.StdErr, 300)}");
        if (result.Truncated) throw new ProtocolException("omp --version 输出超出边界。");
        var raw = JsonIo.Bounded(combined, 300);
        var match = Regex.Match(combined, @"(?i)\bomp(?:/|\s+v?)?([0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.\-]+)?)", RegexOptions.CultureInvariant);
        if (!match.Success) match = Regex.Match(combined, @"(?<![0-9.])([0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.\-]+)?)(?![0-9.])", RegexOptions.CultureInvariant);
        return new VersionProbe
        {
            ExitCode = result.ExitCode,
            Raw = raw,
            Parsed = match.Success ? match.Groups[1].Value : null
        };
    }
}
