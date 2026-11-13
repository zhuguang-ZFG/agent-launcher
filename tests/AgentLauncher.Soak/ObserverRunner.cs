using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace SoakRunner;

internal static class ObserverRunner
{
    public static int Run(RunnerOptions options, CancellationToken cancellation = default) => new Session(options, cancellation).Execute();

    private sealed class Session
    {
        private readonly RunnerOptions options;
        private readonly SoakResult result = new();
        private readonly Sampler sampler = new();
        private readonly ResourceSettings settings = new();
        private readonly List<Guid> attemptIds = [];
        private readonly List<LoggedProcess> workers = [];
        private readonly List<Runtime> ownedRuntimes = [];
        private readonly CancellationToken cancellation;
        private LoggedProcess? monitor;
        private string gitPath = "";
        private string resultPath = "";
        private bool prepared;

        public Session(RunnerOptions value, CancellationToken cancellation)
        {
            options = value;
            this.cancellation = cancellation;
        }

        public int Execute()
        {
            result.StartedAtUtc = DateTimeOffset.UtcNow;
            result.Root = options.Root;
            result.OutputDirectory = options.OutputDirectory;
            result.Mode = options.AdmissionOnly ? "admission-only" : options.CalibrationSeconds > 0 ? "calibration" : "stability";
            var exitCode = 0;
            try
            {
                Prepare();
                result.Environment = BuildEnvironment();
                var genericPreflight = EvaluateAdmission(DefaultBudgets());
                result.Preflight = genericPreflight;
                WriteResult();
                result.ProtocolNotes.Add("RPC flags: --mode rpc --no-session --no-title --no-extensions --no-skills --no-rules --no-context-files --no-lsp --no-pty --no-ui.");
                result.ProtocolNotes.Add("Only bash RPC commands are sent by the runner. An auth:none model sentinel targets loopback port 1; no prompt, login, inference, extension, or UI command is sent. OMP startup may refresh public model catalogs.");
                result.ProtocolNotes.Add("The provided RPC documentation does not enumerate BashResult data fields; each workload iteration requires a marker file with a fresh nonce, in addition to reply success.");
                result.ProtocolNotes.Add("A single reserve request contains exactly two independent items. No busy retry, waiver, or automatic approval is used.");
                if (!string.Equals(genericPreflight.Decision, "Allow", StringComparison.Ordinal))
                    throw new AdmissionDeniedException(genericPreflight.Reason);
                if (options.AdmissionOnly)
                {
                    EnsureMonitor(genericPreflight);
                    genericPreflight.MonitoredRootCpuRate = ReadMonitorRootCpuRate();
                    genericPreflight.RootCpuMatches = genericPreflight.MonitoredRootCpuRate == 3500;
                    if (!genericPreflight.RootCpuMatches) throw new ExecutionFailureException("监控器根 Job CPU 封顶不是 3500。");
                    result.Status = "admission-allowed";
                    FinalizeStability();
                }
                else
                {
                    ResolveAndProbeTools();
                    for (var index = 1; index <= options.Rounds; index++)
                    {
                        var round = RunRound(index);
                        if (!round.Passed) throw new ExecutionFailureException(round.Failure ?? $"第 {index} 轮未通过。");
                        FinalizeStability();
                        WriteResult();
                    }
                    result.Status = options.CalibrationSeconds > 0 ? "calibration-completed" : "succeeded";
                    FinalizeStability();
                }
            }
            catch (AdmissionDeniedException e)
            {
                result.Status = "admission-blocked";
                result.Blocker = e.Message;
                result.Failure = e.Message;
                FinalizeStability();
                exitCode = 3;
            }
            catch (VersionMismatchException e)
            {
                result.Status = "failed";
                result.Failure = e.Message;
                FinalizeStability();
                exitCode = 6;
            }
            catch (ProtocolException e)
            {
                result.Status = "failed";
                result.Failure = e.Message;
                FinalizeStability();
                exitCode = 6;
            }
            catch (ExecutionFailureException e)
            {
                result.Status = "failed";
                result.Failure = e.Message;
                FinalizeStability();
                exitCode = 4;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                result.Status = "failed";
                result.Failure = e.Message;
                FinalizeStability();
                exitCode = 4;
            }
            finally
            {
                try { Cleanup(); }
                catch (Exception e) when (e is not OutOfMemoryException) { result.Cleanup.Errors.Add(e.Message); }
                if (result.Environment is not null)
                {
                    try
                    {
                        foreach (var pair in result.Environment.RuntimeSha256)
                            if (JsonIo.Sha256File(Path.Combine(options.RuntimeDirectory, pair.Key)) != pair.Value)
                                throw new ExecutionFailureException("运行时文件在验收期间改变: " + pair.Key);
                        foreach (var pair in new[] { (options.OmpPath, result.Environment.OmpSha256), (options.NodePath, result.Environment.NodeSha256), (gitPath, result.Environment.GitSha256) })
                            if (pair.Item2.Length > 0 && JsonIo.Sha256File(pair.Item1) != pair.Item2)
                                throw new ExecutionFailureException("工具文件在验收期间改变: " + Path.GetFileName(pair.Item1));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { result.Cleanup.Errors.Add(e.Message); }
                }
                if (result.Cleanup.Errors.Count > 0 || result.Cleanup.MonitorLeftRunning)
                {
                    result.Status = "failed";
                    result.Failure ??= "验收收尾未能全部核实，查看 cleanup.errors。";
                    exitCode = 4;
                }
                FinalizeStability();
                result.FinishedAtUtc = DateTimeOffset.UtcNow;
                if (prepared) WriteResult();
                else if (result.Failure is not null) Console.Error.WriteLine(result.Failure);
            }
            return exitCode;
        }

        private void Prepare()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE")))
                throw new UsageException("长稳验收禁止继承模拟资源采样。");
            var actualRoot = Path.GetFullPath(Store.Root);
            if (!string.Equals(actualRoot, Path.GetFullPath(options.Root), StringComparison.OrdinalIgnoreCase))
                throw new ExecutionFailureException("Store.Root 与 --root 不一致，拒绝触碰其他状态目录。");
            EvidenceChecks.ValidateRuntime(options.RuntimeDirectory);
            if (!string.Equals(Path.GetFullPath(options.RunnerExecutable), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                throw new ExecutionFailureException("driver 可执行文件必须与当前验收器入口一致。");
            EvidenceChecks.RequirePlainPath(options.Root);
            EvidenceChecks.RequirePlainPath(options.OutputDirectory);
            if (!JsonIo.IsUnder(options.Root, options.OutputDirectory) || Directory.Exists(options.Root) || File.Exists(options.Root))
                throw new UsageException("测试根目录必须为不存在的新目录，输出必须是其子目录。");
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(options.Root);
            using (var owner = new FileStream(Path.Combine(options.Root, "soak-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                System.Text.Json.JsonSerializer.Serialize(owner, new { Pid = Environment.ProcessId, AtUtc = DateTimeOffset.UtcNow });
                owner.Flush(true);
            }
            Directory.CreateDirectory(options.OutputDirectory);
            resultPath = Path.Combine(options.OutputDirectory, "soak-result.json");
            prepared = true;
        }

        private EnvironmentEvidence BuildEnvironment()
        {
            var evidence = new EnvironmentEvidence
            {
                Root = options.Root,
                OutputDirectory = options.OutputDirectory,
                RunnerExecutable = options.RunnerExecutable,
                RunnerSha256 = JsonIo.Sha256File(options.RunnerExecutable),
                RuntimeDirectory = options.RuntimeDirectory,
                OmpPath = options.OmpPath,
                OmpSha256 = File.Exists(options.OmpPath) ? JsonIo.Sha256File(options.OmpPath) : "",
                NodePath = options.NodePath,
                NodeSha256 = File.Exists(options.NodePath) ? JsonIo.Sha256File(options.NodePath) : "",
                OsDescription = RuntimeInformation.OSDescription,
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessId = Environment.ProcessId,
                StartedAtUtc = result.StartedAtUtc,
                TestRootEnvironment = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "",
                TestRootMatches = string.Equals(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT"), options.Root, StringComparison.OrdinalIgnoreCase)
            };
            foreach (var name in new[] { "AgentLauncher.Core.dll", "AgentLauncher.Windows.dll", "AgentLauncher.Worker.exe", "AgentLauncher.Worker.dll", "SoakRunner.dll" })
            {
                var path = Path.Combine(options.RuntimeDirectory, name);
                if (File.Exists(path)) evidence.RuntimeSha256[name] = JsonIo.Sha256File(path);
            }
            var probe = ProcessTools.BuildChildEnvironment(options.Root,
                Path.Combine(options.OutputDirectory, "probes", "environment-home"),
                Path.Combine(options.OutputDirectory, "probes", "environment-agent"));
            evidence.CredentialLikeParentVariables = probe.CredentialLikeParentVariables;
            return evidence;
        }

        private void ResolveAndProbeTools()
        {
            gitPath = Path.IsPathFullyQualified(options.GitPath)
                ? Path.GetFullPath(options.GitPath)
                : ProcessTools.ResolveCommand(options.GitPath) ?? throw new ExecutionFailureException("无法解析 git.exe。");
            if (!File.Exists(gitPath)) throw new ExecutionFailureException("git.exe 不存在。");
            result.Environment!.GitPath = gitPath;
            result.Environment.GitSha256 = JsonIo.Sha256File(gitPath);
            var environment = ToolEnvironment("git-probe");
            result.Environment.GitVersionRaw = RunProbe(gitPath, ["--version"], environment, "git --version");
            result.Environment.NodeVersionRaw = RunProbe(options.NodePath, ["--version"], environment, "node --version");
            var ompEnvironment = ToolEnvironment("omp-probe");
            var version = OmpRpcClient.ProbeVersion(options.OmpPath, ompEnvironment.Variables, options.OutputDirectory,
                TimeSpan.FromSeconds(30));
            result.Environment.OmpVersionRaw = version.Raw;
            result.Environment.OmpVersionParsed = version.Parsed;
            if (!string.Equals(version.Parsed, options.OmpVersion, StringComparison.Ordinal))
                throw new VersionMismatchException($"omp 版本不匹配：期望 {options.OmpVersion}，实际 {version.Parsed ?? "未识别"}（{version.Raw}）。");
        }

        private ChildEnvironment ToolEnvironment(string name)
        {
            var extra = new List<string>();
            var nodeDirectory = Path.GetDirectoryName(Path.GetFullPath(options.NodePath));
            if (!string.IsNullOrWhiteSpace(nodeDirectory)) extra.Add(nodeDirectory);
            if (Path.IsPathFullyQualified(gitPath)) extra.AddRange(ProcessTools.ExtraPathDirectoriesForTool(gitPath));
            return ProcessTools.BuildChildEnvironment(options.Root,
                Path.Combine(options.OutputDirectory, "probes", name + "-home"),
                Path.Combine(options.OutputDirectory, "probes", name + "-agent"),
                extra);
        }

        private static string RunProbe(string executable, IEnumerable<string> arguments, ChildEnvironment environment, string label)
        {
            var start = ProcessTools.CreateStartInfo(executable, arguments, Path.GetDirectoryName(executable)!);
            start.Environment.Clear();
            foreach (var pair in environment.Variables) start.Environment[pair.Key] = pair.Value;
            var output = ProcessTools.RunBounded(start, TimeSpan.FromSeconds(30), 64 * 1024);
            if (output.TimedOut) throw new ExecutionFailureException(label + " 超时。");
            if (output.ExitCode != 0) throw new ExecutionFailureException($"{label} 退出码 {output.ExitCode}: {JsonIo.Bounded(output.StdErr, 300)}");
            return JsonIo.Bounded(output.StdOut + " " + output.StdErr, 300);
        }

        private AdmissionEvidence EvaluateAdmission(IReadOnlyList<BudgetItem> requested)
        {
            sampler.Sample();
            Thread.Sleep(300);
            var sample = sampler.Sample();
            var policy = ResourcePolicy.SnapshotFor(settings, sample.Total, sample.CommitLimit);
            var decision = ResourcePolicy.Evaluate(sample, policy, Array.Empty<BudgetItem>(), requested, includeSoftBudget: true);
            return new AdmissionEvidence
            {
                SampledAtUtc = DateTimeOffset.UtcNow,
                Sample = new MemoryEvidence { Total = sample.Total, Available = sample.Available, CommitLimit = sample.CommitLimit, CommitAvailable = sample.CommitAvailable, CpuPercent = sample.CpuPercent, At = sample.At },
                Policy = new PolicyEvidence
                {
                    Mode = policy.Mode,
                    JobMemoryLimit = policy.JobMemoryLimit,
                    PlanningBytes = policy.PlanningBytes,
                    PhysicalFloor = policy.PhysicalFloor,
                    CommitFloor = policy.CommitFloor,
                    CpuRate = policy.CpuRate,
                    PoolMode = policy.PoolMode,
                    PoolMemoryLimit = policy.PoolMemoryLimit,
                    PolicyHash = ResourcePolicy.PolicyHash(policy)
                },
                Requested = requested.Select(BudgetEvidenceOf).ToList(),
                Decision = decision.Kind.ToString(),
                Reason = decision.Reason,
                PlanningRequested = decision.PlanningRequested,
                PlanningReserved = decision.PlanningReserved,
                CapSumRequested = decision.CapSumRequested,
                CapSumReserved = decision.CapSumReserved,
                SoftCap = decision.SoftCap,
                PhysicalFloor = decision.PhysicalFloor,
                CommitFloor = decision.CommitFloor,
                SampleValid = decision.SampleValid,
                ExpectedRootCpuRate = 3500,
                Blocker = decision.Kind == AdmissionKind.Allow ? null : decision.Reason
            };
        }

        private static List<BudgetItem> DefaultBudgets() =>
        [
            new(Guid.NewGuid(), "agent", ResourcePolicy.DefaultPlanningBytes, ResourcePolicy.DefaultJobMemoryLimit),
            new(Guid.NewGuid(), "agent", ResourcePolicy.DefaultPlanningBytes, ResourcePolicy.DefaultJobMemoryLimit)
        ];

        private static BudgetEvidence BudgetEvidenceOf(BudgetItem item) => new()
        {
            Id = item.Id,
            Kind = item.Kind,
            PlanningBytes = item.PlanningBytes,
            JobMemoryLimit = item.JobMemoryLimit
        };

        private void EnsureMonitor(AdmissionEvidence admission)
        {
            if (monitor is not null) return;
            var worker = Path.Combine(options.RuntimeDirectory, "AgentLauncher.Worker.exe");
            var environment = ProcessTools.BuildChildEnvironment(options.Root,
                Path.Combine(options.OutputDirectory, "monitor-home"),
                Path.Combine(options.OutputDirectory, "monitor-agent"));
            monitor = LoggedProcess.Start(ProcessTools.CreateStartInfo(worker, ["monitor"], options.Root),
                environment.Variables,
                Path.Combine(options.OutputDirectory, "monitor.stdout.log"),
                Path.Combine(options.OutputDirectory, "monitor.stderr.log"));
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(options.StartupSeconds))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var status = Ipc.SendAsync(new Request("status", Guid.NewGuid()), cts.Token).GetAwaiter().GetResult();
                    if (status.Ok)
                    {
                        if (status.RootCpuRate != 3500)
                            throw new ExecutionFailureException($"监控器根 Job CPU 封顶为 {status.RootCpuRate?.ToString(CultureInfo.InvariantCulture) ?? "不可读"}，不是 3500。");
                        admission.MonitoredRootCpuRate = status.RootCpuRate;
                        admission.RootCpuMatches = true;
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(status.AdmissionBlockedReason))
                        throw new ExecutionFailureException("监控器拒绝准入：" + status.AdmissionBlockedReason);
                }
                catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
                if (monitor.Process.HasExited)
                    throw new ExecutionFailureException($"监控器提前退出，退出码 {monitor.ExitCode}。");
                Thread.Sleep(250);
            }
            throw new ExecutionFailureException("监控器未在启动期限内就绪。");
        }

        private int? ReadMonitorRootCpuRate()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var status = Ipc.SendAsync(new Request("status", Guid.NewGuid()), cts.Token).GetAwaiter().GetResult();
                return status.RootCpuRate;
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
            {
                return null;
            }
        }

        private RoundEvidence RunRound(int index)
        {
            var round = new RoundEvidence
            {
                Index = index,
                Kind = options.CalibrationSeconds > 0 ? "calibration" : "stability",
                RequiredSeconds = options.RoundSeconds,
                StartedAtUtc = DateTimeOffset.UtcNow
            };
            result.Rounds.Add(round);
            var roundDirectory = Path.Combine(options.OutputDirectory, $"round-{index:D2}");
            Directory.CreateDirectory(roundDirectory);
            var plans = new List<PlannedItem>();
            try
            {
                for (var position = 1; position <= 2; position++)
                {
                    var stateDirectory = Path.Combine(roundDirectory, $"d{position}-state");
                    Directory.CreateDirectory(stateDirectory);
                    var fixture = FixtureBuilder.Create(options.Root, roundDirectory,
                        Path.Combine(roundDirectory, $"d{position}"), gitPath);
                    plans.Add(BuildItem(position, fixture, stateDirectory));
                }
                var items = plans.Select(plan => plan.Item).ToList();
                var requested = items.Select(item => ResourcePolicy.BudgetOf(item)).ToList();
                var admission = EvaluateAdmission(requested);
                round.Admission = admission;
                result.Preflight = admission;
                if (!string.Equals(admission.Decision, "Allow", StringComparison.Ordinal))
                    throw new AdmissionDeniedException(admission.Reason);
                var policy = ResourcePolicy.SnapshotFor(settings, admission.Sample.Total, admission.Sample.CommitLimit);
                EnsureMonitor(admission);
                var reservation = Reserve(items, policy, admission);
                round.Reservation = reservation;
                if (!reservation.Ok)
                    throw new AdmissionDeniedException(string.IsNullOrWhiteSpace(reservation.AdmissionBlockedReason) ? reservation.Message : reservation.AdmissionBlockedReason);
                attemptIds.AddRange(items.Select(item => item.Id));
                var runtimes = new List<Runtime>();
                Exception? failure = null;
                try
                {
                    foreach (var plan in plans)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var runtime = StartWorker(plan);
                        runtimes.Add(runtime);
                        ownedRuntimes.Add(runtime);
                        workers.Add(runtime.Worker);
                    }
                    WaitForReady(runtimes);
                    var bothReady = runtimes.Max(runtime => runtime.Progress!.ReadyAtUtc!.Value);
                    round.BothReadyAtUtc = bothReady;
                    var overlapWatch = Stopwatch.StartNew();
                    round.Samples.Add(Sample(runtimes));
                    while (overlapWatch.Elapsed < TimeSpan.FromSeconds(options.RoundSeconds))
                    {
                        CheckDuringRun(runtimes);
                        Thread.Sleep(TimeSpan.FromSeconds(options.SampleSeconds));
                        round.Samples.Add(Sample(runtimes));
                        if (round.Samples.Count % 12 == 0)
                        {
                            round.Drivers = runtimes.Select(runtime => runtime.Evidence()).ToList();
                            WriteResult();
                        }
                    }
                    CheckDuringRun(runtimes);
                    round.ObservedOverlapSeconds = overlapWatch.Elapsed.TotalSeconds;
                    StopFiles(runtimes);
                    round.StopFileWrittenAtUtc = DateTimeOffset.UtcNow;
                    WaitForShutdown(runtimes);
                    FillRound(round, runtimes);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    failure = e;
                    round.Failure = e.Message;
                    var started = runtimes.Select(runtime => runtime.Id).ToHashSet();
                    Revoke(plans.Select(plan => plan.Item.Id).Where(id => !started.Contains(id)));
                    try { StopFiles(runtimes); } catch (Exception stopError) when (stopError is IOException or UnauthorizedAccessException) { }
                    try { WaitForShutdown(runtimes); } catch (Exception shutdownError) when (shutdownError is not OutOfMemoryException) { }
                    FillRound(round, runtimes);
                }
                if (failure is AdmissionDeniedException denied) throw denied;
                if (failure is VersionMismatchException version) throw version;
                if (failure is ProtocolException protocol) throw protocol;
                if (failure is not null) throw new ExecutionFailureException(failure.Message);
                ThrowDriverProtocolFailure(runtimes);
                if (!round.Passed) throw new ExecutionFailureException(round.Failure ?? "轮次未通过。");
                return round;
            }
            catch (AdmissionDeniedException)
            {
                if (round.Failure is null) round.Failure = "准入拒绝，未启动 driver。";
                throw;
            }
            catch (VersionMismatchException)
            {
                if (round.Failure is null) round.Failure = "协议或版本不匹配。";
                throw;
            }
            catch (ProtocolException)
            {
                if (round.Failure is null) round.Failure = "RPC 协议失败。";
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (round.Failure is null) round.Failure = e.Message;
                throw new ExecutionFailureException(e.Message);
            }
        }

        private PlannedItem BuildItem(int position, FixtureWorkspace fixture, string stateDirectory)
        {
            var stopFile = Path.Combine(stateDirectory, "stop");
            var resultFile = Path.Combine(stateDirectory, "driver-result.json");
            var arguments = new List<string> { "driver", "--omp", options.OmpPath, "--omp-version", options.OmpVersion, "--node", options.NodePath };
            if (!string.IsNullOrWhiteSpace(gitPath))
            {
                arguments.Add("--git");
                arguments.Add(gitPath);
            }
            arguments.AddRange([
                "--state-root", stateDirectory,
                "--agent-dir", Path.Combine(stateDirectory, "agent"),
                "--home", Path.Combine(stateDirectory, "home"),
                "--duration-seconds", options.RoundSeconds.ToString(CultureInfo.InvariantCulture),
                "--stop-file", stopFile,
                "--result", resultFile,
                "--command-timeout-seconds", options.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                "--cycle-interval-ms", options.CycleIntervalMilliseconds.ToString(CultureInfo.InvariantCulture),
                "--omp-shutdown-seconds", options.OmpShutdownSeconds.ToString(CultureInfo.InvariantCulture)
            ]);
            Directory.CreateDirectory(Path.Combine(stateDirectory, "agent"));
            Directory.CreateDirectory(Path.Combine(stateDirectory, "home"));
            var item = new LaunchItem(
                Id: Guid.NewGuid(), BatchId: Guid.NewGuid(), Position: position,
                Name: $"soak-driver-{position}", Cwd: fixture.Directory,
                Workspace: PhysicalDirectory.Workspace(fixture.Directory), Shared: false,
                Command: new CommandSpec(options.RunnerExecutable, arguments.ToArray(), "soak"),
                Budget: ResourcePolicy.DefaultJobMemoryLimit, Kind: "agent",
                PlanningBytes: ResourcePolicy.DefaultPlanningBytes);
            return new PlannedItem(item, stateDirectory, stopFile, resultFile);
        }

        private Runtime StartWorker(PlannedItem plan)
        {
            var worker = Path.Combine(options.RuntimeDirectory, "AgentLauncher.Worker.exe");
            var environment = ProcessTools.BuildChildEnvironment(options.Root,
                Path.Combine(plan.StateDirectory, "worker-home"),
                Path.Combine(plan.StateDirectory, "worker-agent"));
            LoggedProcess process;
            try { process = LoggedProcess.Start(ProcessTools.CreateStartInfo(worker, ["run", plan.Item.Id.ToString()], options.Root),
                environment.Variables,
                Path.Combine(plan.StateDirectory, "worker.stdout.log"),
                Path.Combine(plan.StateDirectory, "worker.stderr.log")); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                result.Cleanup.Errors.Add("Worker 启动未能完整交接到验收器，保留监控器供核对: " + plan.Item.Id);
                throw;
            }
            return new Runtime
            {
                Id = plan.Item.Id,
                Position = plan.Item.Position,
                StateDirectory = plan.StateDirectory,
                StopFile = plan.StopFile,
                ResultFile = plan.ResultFile,
                Worker = process
            };
        }

        private void WaitForReady(List<Runtime> runtimes)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(options.StartupSeconds))
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var runtime in runtimes)
                {
                    runtime.Refresh();
                    if (runtime.Progress?.Status == "failed") ThrowDriverFailure(runtime.Progress);
                    if (runtime.Worker.Process.HasExited && runtime.Progress?.ReadyAtUtc is null)
                        throw new ExecutionFailureException($"Worker {runtime.Position} 在 driver 就绪前退出，退出码 {runtime.Worker.ExitCode}。");
                }
                if (runtimes.All(runtime => runtime.Progress?.ReadyAtUtc is not null && string.Equals(runtime.Progress.Phase, "running", StringComparison.Ordinal)))
                    return;
                Thread.Sleep(500);
            }
            throw new ExecutionFailureException($"两个 driver 未在 {options.StartupSeconds} 秒内全部发送 OMP ready 并进入运行。");
        }

        private void CheckDuringRun(List<Runtime> runtimes)
        {
            cancellation.ThrowIfCancellationRequested();
            if (monitor is null || monitor.HasExited || ReadMonitorRootCpuRate() != 3500)
                throw new ExecutionFailureException("监控器或根 CPU 上限无法持续核实。");
            foreach (var runtime in runtimes)
            {
                runtime.Refresh();
                var progress = runtime.Progress ?? throw new ExecutionFailureException($"Worker {runtime.Position} 的进度记录不可读。");
                if (progress.Status == "failed") ThrowDriverFailure(progress);
                if (progress.Status == "succeeded" || progress.StopReason is not null)
                    throw new ExecutionFailureException($"Worker {runtime.Position} 在 observers 停止信号前结束。");
                if (progress.ReadyAtUtc is null) throw new ExecutionFailureException($"Worker {runtime.Position} 丢失 ready 时间。");
                if (runtime.Worker.Process.HasExited)
                    throw new ExecutionFailureException($"Worker {runtime.Position} 在轮次结束前退出，退出码 {runtime.Worker.ExitCode}。");
                if (runtime.MemberCount() <= 0)
                    throw new ExecutionFailureException($"Worker {runtime.Position} 的 Job 成员无法核实。");
                runtime.Activity.Observe(progress.ActivityCount, runtime.Watch.Elapsed,
                    TimeSpan.FromSeconds(options.CommandTimeoutSeconds + options.SampleSeconds + 5) + TimeSpan.FromMilliseconds(options.CycleIntervalMilliseconds));
            }
        }

        private static void ThrowDriverFailure(DriverProgress progress)
        {
            if (progress.FailureKind == "protocol-version") throw new VersionMismatchException(progress.LastError ?? "协议版本不匹配。");
            if (progress.FailureKind?.StartsWith("protocol", StringComparison.Ordinal) == true)
                throw new ProtocolException(progress.LastError ?? "RPC 协议失败。");
            throw new ExecutionFailureException(progress.LastError ?? "driver 失败。");
        }

        private static void ThrowDriverProtocolFailure(List<Runtime> runtimes)
        {
            var progress = runtimes.Select(runtime => runtime.Progress).FirstOrDefault(value => value?.FailureKind?.StartsWith("protocol", StringComparison.Ordinal) == true);
            if (progress is not null) ThrowDriverFailure(progress);
        }

        private ResourceSample Sample(List<Runtime> runtimes)
        {
            var sample = sampler.Sample();
            return new ResourceSample
            {
                AtUtc = DateTimeOffset.UtcNow,
                Total = sample.Total,
                Available = sample.Available,
                CommitLimit = sample.CommitLimit,
                CommitAvailable = sample.CommitAvailable,
                CpuPercent = sample.CpuPercent,
                Driver1Activity = runtimes[0].Progress?.ActivityCount ?? 0,
                Driver2Activity = runtimes[1].Progress?.ActivityCount ?? 0,
                Driver1Members = runtimes[0].MemberCount(),
                Driver2Members = runtimes[1].MemberCount(),
                Driver1Status = runtimes[0].Progress?.Status ?? "",
                Driver2Status = runtimes[1].Progress?.Status ?? ""
            };
        }

        private static void StopFiles(List<Runtime> runtimes)
        {
            foreach (var runtime in runtimes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(runtime.StopFile)!);
                File.WriteAllText(runtime.StopFile, "stop " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n", new UTF8Encoding(false));
            }
        }

        private void WaitForShutdown(List<Runtime> runtimes)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(options.ShutdownSeconds))
            {
                foreach (var runtime in runtimes) runtime.Refresh();
                if (runtimes.All(runtime => runtime.Worker.Process.HasExited)) break;
                Thread.Sleep(500);
            }
            foreach (var runtime in runtimes)
            {
                if (!runtime.Worker.Process.HasExited)
                {
                    runtime.WorkerTimedOut = true;
                    runtime.TerminateOwnJob();
                    runtime.Worker.Kill(entireTree: false);
                    runtime.Worker.WaitForExit(TimeSpan.FromSeconds(5));
                }
                runtime.Worker.JoinPumps(TimeSpan.FromSeconds(2));
                runtime.Refresh();
            }
        }

        private static void FillRound(RoundEvidence round, List<Runtime> runtimes)
        {
            round.FinishedAtUtc = DateTimeOffset.UtcNow;
            round.Drivers.Clear();
            round.AllWorkersExited = runtimes.Count == 2 && runtimes.All(runtime => runtime.Worker.Process.HasExited);
            round.AllJobsDrained = runtimes.All(runtime => runtime.JobDrained());
            round.AllDriversSucceeded = runtimes.All(runtime => runtime.Succeeded());
            round.StopFileObservedByBoth = runtimes.All(runtime => runtime.Progress?.StopReason == "stop-file");
            if (round.BothReadyAtUtc is not null && round.StopFileWrittenAtUtc is not null)
            {
                round.RequiredOverlapSeconds = round.RequiredSeconds;
                round.OverlapSatisfied = round.ObservedOverlapSeconds >= round.RequiredSeconds;
            }
            foreach (var runtime in runtimes) round.Drivers.Add(runtime.Evidence());
            if (round.Samples.Count > 0)
            {
                round.PeakCpuPercent = round.Samples.Max(sample => sample.CpuPercent);
                round.MinAvailableBytes = round.Samples.Min(sample => sample.Available);
                round.PeakDriverActivity = round.Samples.Max(sample => Math.Max(sample.Driver1Activity, sample.Driver2Activity));
            }
            var reasons = new List<string>();
            if (!round.OverlapSatisfied) reasons.Add("完整重叠区间不足");
            if (!round.StopFileObservedByBoth) reasons.Add("至少一个 driver 未以 stop-file 停止");
            if (!round.AllWorkersExited) reasons.Add("至少一个 Worker 未退出");
            if (!round.AllJobsDrained) reasons.Add("至少一个自有 Job 仍有成员");
            if (!round.AllDriversSucceeded) reasons.Add("至少一个 driver 未以零退出成功");
            if (reasons.Count > 0 && round.Failure is null) round.Failure = string.Join("；", reasons);
            round.Passed = reasons.Count == 0 && round.Failure is null;
        }

        private ReservationEvidence Reserve(List<LaunchItem> items, ResourcePolicySnapshot policy, AdmissionEvidence admission)
        {
            ReservationEvidence evidence;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var response = Ipc.SendAsync(new Request("reserve", Guid.NewGuid(), items.ToArray(),
                    PolicyHash: ResourcePolicy.PolicyHash(policy),
                    PlanDigest: ResourcePolicy.PlanDigest(items),
                    Policy: policy), cts.Token).GetAwaiter().GetResult();
                evidence = new ReservationEvidence
                {
                    Ok = response.Ok,
                    Message = response.Message,
                    Epoch = response.Epoch,
                    RootCpuRate = response.RootCpuRate ?? ReadMonitorRootCpuRate(),
                    AdmissionBlockedReason = response.AdmissionBlockedReason,
                    AtUtc = DateTimeOffset.UtcNow,
                    Entries = (response.Entries ?? Array.Empty<ControlEntry>()).Select(EntryEvidenceOf).ToList()
                };
                if (!response.Ok)
                {
                    if (response.Entries is { Length: > 0 }) Revoke(items.Select(item => item.Id));
                    return evidence;
                }
                admission.MonitoredRootCpuRate = evidence.RootCpuRate;
                admission.RootCpuMatches = evidence.RootCpuRate == 3500;
                if (!admission.RootCpuMatches)
                    throw new ExecutionFailureException($"reserve 后实测根 Job CPU 封顶不是 3500：{evidence.RootCpuRate?.ToString(CultureInfo.InvariantCulture) ?? "不可读"}。");
                if (response.Entries is null || response.Entries.Length != items.Count)
                    throw new ExecutionFailureException("reserve 返回的启动项数量不正确。");
                foreach (var item in items)
                {
                    var entry = response.Entries.SingleOrDefault(candidate => candidate.Plan.Id == item.Id)
                        ?? throw new ExecutionFailureException("reserve 缺少启动项 " + item.Id.ToString());
                    if (entry.State != "Pending") throw new ExecutionFailureException("reserve 返回的启动项不是 Pending。");
                    if (entry.Plan.Budget != ResourcePolicy.DefaultJobMemoryLimit || entry.Plan.PlanningBytes != ResourcePolicy.DefaultPlanningBytes)
                        throw new ExecutionFailureException("reserve 返回的预算或规划估算不符。");
                    if (entry.JobMemoryLimit != ResourcePolicy.DefaultJobMemoryLimit || entry.PlanningBytes != ResourcePolicy.DefaultPlanningBytes)
                        throw new ExecutionFailureException("reserve 记录的 Job 上限或规划估算不符。");
                    if (entry.Mode != "practical" || entry.Pool != ResourcePolicy.PoolIndependent)
                        throw new ExecutionFailureException("reserve 记录的资源模式或池模式不符。");
                    if (entry.CpuRate != 3500) throw new ExecutionFailureException("reserve 记录的 CPU 封顶不是 3500。");
                }
                return evidence;
            }
            catch (OperationCanceledException e)
            {
                Revoke(items.Select(item => item.Id));
                throw new ExecutionFailureException("reserve 在期限内未得到确认，已尝试撤销并停止：" + e.Message);
            }
            catch (Exception e) when (e is IOException or TimeoutException)
            {
                Revoke(items.Select(item => item.Id));
                throw new ExecutionFailureException("reserve 未确认，已尝试撤销并停止：" + e.Message);
            }
            catch (ExecutionFailureException)
            {
                Revoke(items.Select(item => item.Id));
                throw;
            }
        }

        private static EntryEvidence EntryEvidenceOf(ControlEntry entry) => new()
        {
            Id = entry.Plan.Id,
            Position = entry.Plan.Position,
            Name = entry.Plan.Name,
            State = entry.State,
            Digest = entry.Digest,
            Cwd = entry.Plan.Cwd,
            WorkspacePath = entry.Plan.Workspace.Path,
            WorkspaceKey = entry.Plan.Workspace.Key,
            Executable = entry.Plan.Command.Executable,
            Budget = entry.Plan.Budget,
            PlanningBytes = entry.Plan.PlanningBytes,
            JobMemoryLimit = entry.JobMemoryLimit,
            CpuRate = entry.CpuRate,
            Mode = entry.Mode,
            Pool = entry.Pool
        };

        private static void Revoke(IEnumerable<Guid> ids)
        {
            foreach (var id in ids.Distinct())
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    Ipc.SendAsync(new Request("revoke", Guid.NewGuid(), Attempt: id), cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
            }
        }

        private void FinalizeStability() => EvidenceChecks.Finalize(result, options.CalibrationSeconds > 0, options.AdmissionOnly);

        private void Cleanup()
        {
            result.Cleanup.FilesPreserved = true;
            Revoke(attemptIds);
            foreach (var runtime in ownedRuntimes)
            {
                try
                {
                    runtime.Refresh();
                    if (runtime.Job is null) throw new ExecutionFailureException("自有 Job 句柄缺失，无法证明退出: " + runtime.Id);
                    if (runtime.MemberCount() > 0)
                    {
                        Native.Check(Native.TerminateJobObject(runtime.Job.Handle, 1));
                        result.Cleanup.OwnJobsTerminated = true;
                        result.Cleanup.Actions.Add("请求终止本次验收 Job " + runtime.Id);
                    }
                    var watch = Stopwatch.StartNew();
                    while (!runtime.JobDrained() && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);
                    if (!runtime.JobDrained()) throw new ExecutionFailureException("自有 Job 排干未得到确认: " + runtime.Id);
                }
                catch (Exception e) when (e is not OutOfMemoryException) { result.Cleanup.Errors.Add(e.Message); }
            }
            foreach (var worker in workers)
            {
                try
                {
                    if (!worker.HasExited)
                    {
                        worker.Kill(entireTree: false);
                        result.Cleanup.OwnProcessesTerminated = true;
                    }
                    if (!worker.WaitForExit(TimeSpan.FromSeconds(5)))
                        result.Cleanup.Errors.Add("自有 Worker 退出未得到确认: " + worker.Process.Id);
                    if (!worker.JoinPumps(TimeSpan.FromSeconds(2))) result.Cleanup.Errors.Add("Worker 日志尚未排干。");
                    if (worker.LogError is { } logError) result.Cleanup.Errors.Add(logError);
                }
                catch (Exception e) when (e is not OutOfMemoryException) { result.Cleanup.Errors.Add(e.Message); }
            }
            if (monitor is not null)
            {
                try
                {
                    // Exact Process handle from this fresh root; a pid file is not ownership proof.
                    if (result.Cleanup.Errors.Count == 0 && !monitor.HasExited)
                    {
                        monitor.Kill(entireTree: false);
                        result.Cleanup.Actions.Add("请求停止本次监控器 PID " + monitor.Process.Id);
                        if (!monitor.WaitForExit(TimeSpan.FromSeconds(5))) result.Cleanup.Errors.Add("自有监控器退出未得到确认。");
                    }
                    result.Cleanup.MonitorStopped = monitor.HasExited;
                    result.Cleanup.MonitorLeftRunning = !result.Cleanup.MonitorStopped;
                    if (monitor.HasExited && !monitor.JoinPumps(TimeSpan.FromSeconds(2))) result.Cleanup.Errors.Add("监控器日志尚未排干。");
                    if (monitor.LogError is { } logError) result.Cleanup.Errors.Add(logError);
                }
                catch (Exception e) when (e is not OutOfMemoryException) { result.Cleanup.Errors.Add(e.Message); result.Cleanup.MonitorLeftRunning = true; }
            }
            foreach (var runtime in ownedRuntimes) runtime.Dispose();
            monitor?.Dispose();
            result.Cleanup.Verified = result.Cleanup.Errors.Count == 0 && !result.Cleanup.MonitorLeftRunning;
        }

        private void WriteResult()
        {
            result.Environment ??= BuildEnvironment();
            JsonIo.Write(resultPath, result);
        }

        private sealed record PlannedItem(LaunchItem Item, string StateDirectory, string StopFile, string ResultFile);

        private sealed class Runtime : IDisposable
        {
            public Guid Id { get; init; }
            public int Position { get; init; }
            public string StateDirectory { get; init; } = "";
            public string StopFile { get; init; } = "";
            public string ResultFile { get; init; } = "";
            public LoggedProcess Worker { get; init; } = null!;
            public DriverProgress? Progress { get; private set; }
            public ReceiptEvidence? Receipt { get; private set; }
            public Job? Job { get; private set; }
            public JobEvidence? JobRecord { get; private set; }
            public bool WorkerTimedOut { get; set; }
            public Stopwatch Watch { get; } = Stopwatch.StartNew();
            public ActivityWatch Activity { get; } = new();

            public void Refresh()
            {
                Progress = JsonIo.Read<DriverProgress>(ResultFile);
                try
                {
                    var receipt = Store.Read<Receipt>(Store.ReceiptPath(Id));
                    if (receipt is not null)
                    {
                        Receipt = new ReceiptEvidence
                        {
                            Phase = receipt.Phase,
                            Pid = receipt.Pid,
                            ProcessTime = receipt.ProcessTime,
                            ExitCode = receipt.ExitCode,
                            Detail = JsonIo.Bounded(receipt.Detail, 1000),
                            AtUtc = receipt.At,
                            JobMemoryLimit = receipt.JobMemory?.LimitBytes,
                            JobPeakBytes = receipt.JobMemory?.PeakBytes
                        };
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { }
                if (Job is null)
                {
                    try { Job = new Job(Store.JobName(Id), openOnly: true); }
                    catch (Win32Exception e) when (e.NativeErrorCode == 2) { }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
                }
            }

            public int MemberCount()
            {
                try { return Job?.Members().Length ?? -1; }
                catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException) { return -1; }
            }

            public bool JobDrained()
            {
                try { return Job is not null && Job.Members().Length == 0; }
                catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException) { return false; }
            }

            public bool Succeeded()
            {
                var progress = Progress;
                return progress is not null &&
                       progress.Status == "succeeded" &&
                       progress.StopReason == "stop-file" &&
                       progress.OmpExitCode == 0 &&
                       !progress.OmpForcedTermination &&
                       progress.Cycles > 0 && progress.ActivityCount > 0 &&
                       !progress.ReplayAttempted &&
                       Job?.QueryMemory() is { LimitBytes: ResourcePolicy.DefaultJobMemoryLimit, PeakBytes: > 0 } &&
                       Worker.ExitCode == 0 &&
                       Receipt?.Phase == "Finished" && Receipt.ExitCode == 0;
            }

            public void TerminateOwnJob()
            {
                try
                {
                    if (Job is null) return;
                    if (Job.Members().Length > 0) Native.TerminateJobObject(Job.Handle, 1);
                }
                catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException) { }
            }

            public DriverEvidence Evidence()
            {
                JobRecord = new JobEvidence
                {
                    AttemptId = Id,
                    Found = Job is not null,
                    MemberCount = MemberCount(),
                    Drained = JobDrained()
                };
                if (Job is not null)
                {
                    try
                    {
                        var members = Job.Members();
                        JobRecord.MemberCount = members.Length;
                        JobRecord.Drained = members.Length == 0;
                        var memory = Job.QueryMemory();
                        JobRecord.MemoryLimit = memory?.LimitBytes;
                        JobRecord.PeakBytes = memory?.PeakBytes;
                        if (memory is null) JobRecord.LastError = "Job memory query unavailable";
                    }
                    catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException)
                    {
                        JobRecord.LastError = e.Message;
                    }
                }
                return new DriverEvidence
                {
                    Position = Position,
                    AttemptId = Id,
                    WorkerPid = Worker.Process.Id,
                    WorkerExitCode = Worker.ExitCode,
                    WorkerTimedOut = WorkerTimedOut,
                    WorkerStdOutLog = Worker.StdOutPath,
                    WorkerStdErrLog = Worker.StdErrPath,
                    WorkerLogsTruncated = Worker.StdOutTruncated || Worker.StdErrTruncated,
                    ProgressPath = ResultFile,
                    StopFile = StopFile,
                    Progress = Progress,
                    Receipt = Receipt,
                    Job = JobRecord,
                    ExitMatchesExpected = Worker.ExitCode == 0 && Receipt?.ExitCode == 0 && Progress?.Status == "succeeded"
                };
            }

            public void Dispose()
            {
                Job?.Dispose();
                Job = null;
                Worker.Dispose();
            }
        }
    }
}
