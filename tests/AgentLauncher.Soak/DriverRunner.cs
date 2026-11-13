using System.Diagnostics;
using System.Security.Cryptography;

namespace SoakRunner;

internal static class DriverRunner
{
    private static readonly string[] WorkloadKinds = ["read", "write", "hash", "search", "git-status"];
    private const int FallbackGraceSeconds = 300;

    public static int Run(RunnerOptions options)
    {
        var workspace = Directory.GetCurrentDirectory();
        EvidenceChecks.ValidateDriverRoot(options, workspace);
        var progress = new DriverProgress
        {
            Phase = "starting",
            Pid = Environment.ProcessId,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ExpectedVersion = options.OmpVersion,
            Workspace = workspace,
            AgentDirectory = Path.GetFullPath(options.AgentDirectory),
            HomeDirectory = Path.GetFullPath(options.HomeDirectory),
            ProtocolVerification = "Per-iteration workload marker files verify execution because the provided RPC documentation does not enumerate BashResult data fields. Only success=false, explicit failure exit codes, protocol failure, or missing/mismatched markers fail the run."
        };
        OmpRpcClient? rpc = null;
        Exception? failure = null;
        var exitCode = 4;
        try
        {
            if (File.Exists(options.StopFile)) throw new ExecutionFailureException("stop file 已存在，拒绝把陈旧停止信号当作本轮完成。");
            Directory.CreateDirectory(options.AgentDirectory);
            Directory.CreateDirectory(options.HomeDirectory);
            Directory.CreateDirectory(Path.Combine(options.HomeDirectory, "Temp"));
            Directory.CreateDirectory(Path.GetDirectoryName(options.ResultFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(options.StopFile)!);
            Directory.CreateDirectory(workspace);
            if (!File.Exists(Path.Combine(workspace, "workload.js")) || !Directory.Exists(Path.Combine(workspace, ".git")))
                throw new ExecutionFailureException("driver 工作目录不是预期 fixture：缺少 workload.js 或 .git。");
            var nodeDirectory = Path.GetDirectoryName(Path.GetFullPath(options.NodePath)) ?? "";
            var extra = new List<string>();
            if (nodeDirectory.Length > 0) extra.Add(nodeDirectory);
            if (Path.IsPathFullyQualified(options.GitPath)) extra.AddRange(ProcessTools.ExtraPathDirectoriesForTool(options.GitPath));
            var environment = ProcessTools.BuildChildEnvironment(
                testRoot: options.StateRoot,
                homeDirectory: options.HomeDirectory,
                agentDirectory: options.AgentDirectory,
                extraPathDirectories: extra);
            progress.EnvironmentKeys = environment.Variables.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();
            progress.CredentialLikeParentVariables = environment.CredentialLikeParentVariables;
            progress.Phase = "probing-version";
            Publish(progress, options.ResultFile);
            var version = OmpRpcClient.ProbeVersion(options.OmpPath, environment.Variables, workspace,
                TimeSpan.FromSeconds(Math.Min(options.CommandTimeoutSeconds, 30)));
            progress.ActualVersion = version.Parsed;
            if (!string.Equals(version.Parsed, options.OmpVersion, StringComparison.Ordinal))
                throw new VersionMismatchException($"omp 版本不匹配：期望 {options.OmpVersion}，实际 {version.Parsed ?? "未识别"}（{version.Raw}）。");
            progress.Phase = "starting-rpc";
            Publish(progress, options.ResultFile);
            var stderrPath = Path.Combine(options.StateRoot, "omp-stderr.log");
            progress.OmpStderrLog = stderrPath;
            rpc = new OmpRpcClient(options.OmpPath, workspace, environment.Variables, stderrPath,
                TimeSpan.FromSeconds(60));
            progress.OmpPid = rpc.ProcessId;
            progress.OmpProcessTimeUtc = rpc.ProcessStartTimeUtc;
            progress.Ready = new ReadyInfo
            {
                ProtocolVersion = rpc.Ready.ProtocolVersion,
                SupportedProtocolVersions = rpc.Ready.SupportedProtocolVersions,
                MaxFrameBytes = rpc.Ready.MaxFrameBytes,
                MaxReassembledFrameBytes = rpc.Ready.MaxReassembledFrameBytes,
                SupportsBashExcludeFromContext = rpc.Ready.SupportsBashExcludeFromContext
            };
            progress.ReadyAtUtc = DateTimeOffset.UtcNow;
            progress.Phase = "running";
            progress.Status = "running";
            Publish(progress, options.ResultFile);

            var watch = Stopwatch.StartNew();
            var fallback = TimeSpan.FromSeconds(options.DurationSeconds + FallbackGraceSeconds);
            while (true)
            {
                rpc.EnsureRunning();
                if (File.Exists(options.StopFile))
                {
                    progress.StopReason = "stop-file";
                    break;
                }
                if (watch.Elapsed > fallback)
                {
                    progress.StopReason = "duration-grace";
                    break;
                }
                RunCycle(options, workspace, rpc, progress);
                progress.Cycles++;
                progress.FirstCycleAtUtc ??= DateTimeOffset.UtcNow;
                progress.LastCycleAtUtc = DateTimeOffset.UtcNow;
                progress.LastActivityAtUtc = progress.LastCycleAtUtc;
                Publish(progress, options.ResultFile);
                if (options.CycleIntervalMilliseconds > 0) Thread.Sleep(options.CycleIntervalMilliseconds);
            }
            progress.Phase = "stopping";
            progress.Status = "stopping";
            Publish(progress, options.ResultFile);
            var shutdown = rpc.Shutdown(TimeSpan.FromSeconds(options.OmpShutdownSeconds));
            rpc.EnsureProtocolHealthy();
            progress.OmpExitCode = shutdown.ExitCode;
            progress.OmpForcedTermination = shutdown.Forced;
            progress.OmpStderrCharacters = rpc.StderrCharacters;
            progress.OmpStderrTruncated = rpc.StderrTruncated;
            if (shutdown.Forced) throw new ExecutionFailureException("omp 在关闭期限内没有退出，已终止自己的子进程；不把强制终止算作成功。");
            if (shutdown.ExitCode != 0) throw new ExecutionFailureException($"omp 关闭退出码为 {shutdown.ExitCode}；不把非零退出算作成功。");
            progress.Phase = "finished";
            progress.Status = "succeeded";
            progress.FinishedAtUtc = DateTimeOffset.UtcNow;
            Publish(progress, options.ResultFile);
            return 0;
        }
        catch (VersionMismatchException e)
        {
            failure = e;
            exitCode = 6;
            progress.FailureKind = "protocol-version";
        }
        catch (ProtocolException e)
        {
            failure = e;
            exitCode = 6;
            progress.FailureKind = e.Message.Contains("ready", StringComparison.OrdinalIgnoreCase) ? "protocol-ready" : "protocol";
        }
        catch (TimeoutException e)
        {
            failure = e;
            exitCode = 4;
            progress.FailureKind = "workload-timeout";
        }
        catch (RpcCommandException e)
        {
            failure = e;
            exitCode = 4;
            progress.FailureKind = "workload";
        }
        catch (ExecutionFailureException e)
        {
            failure = e;
            exitCode = 4;
            progress.FailureKind = "process";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failure = e;
            exitCode = 4;
            progress.FailureKind = "internal";
        }
        finally
        {
            if (rpc is not null)
            {
                try
                {
                    var shutdown = rpc.Shutdown(TimeSpan.FromSeconds(options.OmpShutdownSeconds));
                    if (progress.OmpExitCode is null)
                    {
                        progress.OmpExitCode = shutdown.ExitCode;
                        progress.OmpForcedTermination = shutdown.Forced;
                    }
                    progress.OmpStderrCharacters = rpc.StderrCharacters;
                    progress.OmpStderrTruncated = rpc.StderrTruncated;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    progress.LastError = JsonIo.Bounded(progress.LastError ?? e.Message, 1000);
                }
                rpc.Dispose();
            }
        }
        progress.Phase = "finished";
        progress.Status = "failed";
        progress.FinishedAtUtc = DateTimeOffset.UtcNow;
        progress.LastError = JsonIo.Bounded(failure?.Message, 2000);
        progress.ReplayAttempted = false;
        if (progress.LastCommandKind is { } failedKind)
        {
            progress.Failures.TryGetValue(failedKind, out var failures);
            progress.Failures[failedKind] = failures + 1;
        }
        try { Publish(progress, options.ResultFile); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine("无法写入 driver 失败证据: " + e.Message);
        }
        return exitCode;
    }

    private static void RunCycle(RunnerOptions options, string workspace, OmpRpcClient rpc, DriverProgress progress)
    {
        foreach (var kind in WorkloadKinds)
        {
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var markerRelative = ".soak-work/m-" + kind;
            var node = Path.GetFullPath(options.NodePath).Replace('\\', '/');
            var command = Quote(node) + " " + Quote("workload.js") + " " + kind + " " + Quote(markerRelative) + " " + Quote(nonce);
            progress.Attempts.TryGetValue(kind, out var attempts);
            progress.Attempts[kind] = attempts + 1;
            progress.LastCommandKind = kind;
            var watch = Stopwatch.StartNew();
            var reply = rpc.Bash(command, TimeSpan.FromSeconds(options.CommandTimeoutSeconds));
            watch.Stop();
            progress.LastCommandLatencyMs = watch.ElapsedMilliseconds;
            if (reply.DataKeys.Count > 0)
            {
                foreach (var key in reply.DataKeys)
                    if (!progress.BashResponseDataKeysObserved.Contains(key, StringComparer.Ordinal))
                        progress.BashResponseDataKeysObserved.Add(key);
            }
            if (reply.ExitCode is int seenExit)
            {
                progress.BashExitCodeObserved = true;
                if (seenExit != 0) throw new RpcCommandException($"{kind} 返回退出码 {seenExit}。");
            }
            if (reply.Cancelled) throw new RpcCommandException($"{kind} 被 RPC 取消。");
            if (!reply.Success) throw new RpcCommandException($"{kind} RPC 失败：{reply.Error ?? "无错误文本"}");
            var marker = Path.Combine(workspace, ".soak-work", "m-" + kind);
            if (!File.Exists(marker)) throw new RpcCommandException($"{kind} 缺少迭代标记文件。");
            var observed = File.ReadAllText(marker).Trim();
            if (!string.Equals(observed, nonce, StringComparison.Ordinal))
                throw new RpcCommandException($"{kind} 标记内容不匹配。");
            try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            progress.Successes.TryGetValue(kind, out var successes);
            progress.Successes[kind] = successes + 1;
            progress.ActivityCount++;
            progress.LastActivityAtUtc = DateTimeOffset.UtcNow;
            Publish(progress, options.ResultFile);
        }
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void Publish(DriverProgress progress, string path) => JsonIo.Write(path, progress);
}
