namespace SoakRunner;

internal sealed class ReadyInfo
{
    public int ProtocolVersion { get; set; }
    public int[] SupportedProtocolVersions { get; set; } = [];
    public int MaxFrameBytes { get; set; }
    public int MaxReassembledFrameBytes { get; set; }
    public bool SupportsBashExcludeFromContext { get; set; }
}

internal sealed class EnvironmentEvidence
{
    public string Root { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string RunnerExecutable { get; set; } = "";
    public string RunnerSha256 { get; set; } = "";
    public string RuntimeDirectory { get; set; } = "";
    public Dictionary<string, string> RuntimeSha256 { get; set; } = new(StringComparer.Ordinal);
    public string OmpPath { get; set; } = "";
    public string OmpSha256 { get; set; } = "";
    public string OmpVersionRaw { get; set; } = "";
    public string? OmpVersionParsed { get; set; }
    public string NodePath { get; set; } = "";
    public string NodeSha256 { get; set; } = "";
    public string NodeVersionRaw { get; set; } = "";
    public string GitPath { get; set; } = "";
    public string GitSha256 { get; set; } = "";
    public string GitVersionRaw { get; set; } = "";
    public string OsDescription { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public string TestRootEnvironment { get; set; } = "";
    public bool TestRootMatches { get; set; }
    public int CredentialLikeParentVariables { get; set; }
    public string EnvironmentPolicy { get; set; } = "allowlist with isolated HOME/APPDATA/TEMP/XDG/PI_CODING_AGENT_DIR";
}

internal sealed class MemoryEvidence
{
    public ulong Total { get; set; }
    public ulong Available { get; set; }
    public ulong CommitLimit { get; set; }
    public ulong CommitAvailable { get; set; }
    public double CpuPercent { get; set; }
    public DateTimeOffset At { get; set; }
}

internal sealed class PolicyEvidence
{
    public string Mode { get; set; } = "";
    public ulong JobMemoryLimit { get; set; }
    public ulong PlanningBytes { get; set; }
    public ulong PhysicalFloor { get; set; }
    public ulong CommitFloor { get; set; }
    public int CpuRate { get; set; }
    public string PoolMode { get; set; } = "";
    public ulong PoolMemoryLimit { get; set; }
    public string PolicyHash { get; set; } = "";
}

internal sealed class BudgetEvidence
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public ulong PlanningBytes { get; set; }
    public ulong JobMemoryLimit { get; set; }
}

internal sealed class AdmissionEvidence
{
    public DateTimeOffset SampledAtUtc { get; set; }
    public MemoryEvidence Sample { get; set; } = new();
    public PolicyEvidence Policy { get; set; } = new();
    public List<BudgetEvidence> Requested { get; set; } = [];
    public List<BudgetEvidence> Reserved { get; set; } = [];
    public string Decision { get; set; } = "";
    public string Reason { get; set; } = "";
    public ulong PlanningRequested { get; set; }
    public ulong PlanningReserved { get; set; }
    public ulong CapSumRequested { get; set; }
    public ulong CapSumReserved { get; set; }
    public ulong SoftCap { get; set; }
    public ulong PhysicalFloor { get; set; }
    public ulong CommitFloor { get; set; }
    public bool SampleValid { get; set; }
    public int? MonitoredRootCpuRate { get; set; }
    public int ExpectedRootCpuRate { get; set; } = 3500;
    public bool RootCpuMatches { get; set; }
    public string? Blocker { get; set; }
}

internal sealed class ReservationEvidence
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string? Epoch { get; set; }
    public int? RootCpuRate { get; set; }
    public string? AdmissionBlockedReason { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public List<EntryEvidence> Entries { get; set; } = [];
}

internal sealed class EntryEvidence
{
    public Guid Id { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public string Digest { get; set; } = "";
    public string Cwd { get; set; } = "";
    public string WorkspacePath { get; set; } = "";
    public string WorkspaceKey { get; set; } = "";
    public string Executable { get; set; } = "";
    public ulong Budget { get; set; }
    public ulong? PlanningBytes { get; set; }
    public ulong? JobMemoryLimit { get; set; }
    public int? CpuRate { get; set; }
    public string? Mode { get; set; }
    public string? Pool { get; set; }
}

internal sealed class ReceiptEvidence
{
    public string Phase { get; set; } = "";
    public int Pid { get; set; }
    public long ProcessTime { get; set; }
    public int? ExitCode { get; set; }
    public string Detail { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }
    public ulong? JobMemoryLimit { get; set; }
    public ulong? JobPeakBytes { get; set; }
}

internal sealed class JobEvidence
{
    public Guid AttemptId { get; set; }
    public bool Found { get; set; }
    public int MemberCount { get; set; }
    public ulong? MemoryLimit { get; set; }
    public ulong? PeakBytes { get; set; }
    public bool Drained { get; set; }
    public string? LastError { get; set; }
}

internal sealed class ResourceSample
{
    public DateTimeOffset AtUtc { get; set; }
    public ulong Total { get; set; }
    public ulong Available { get; set; }
    public ulong CommitLimit { get; set; }
    public ulong CommitAvailable { get; set; }
    public double CpuPercent { get; set; }
    public long Driver1Activity { get; set; }
    public long Driver2Activity { get; set; }
    public int Driver1Members { get; set; }
    public int Driver2Members { get; set; }
    public string Driver1Status { get; set; } = "";
    public string Driver2Status { get; set; } = "";
}

internal sealed class DriverEvidence
{
    public int Position { get; set; }
    public Guid AttemptId { get; set; }
    public int WorkerPid { get; set; }
    public int? WorkerExitCode { get; set; }
    public bool WorkerTimedOut { get; set; }
    public string WorkerStdOutLog { get; set; } = "";
    public string WorkerStdErrLog { get; set; } = "";
    public bool WorkerLogsTruncated { get; set; }
    public string ProgressPath { get; set; } = "";
    public string StopFile { get; set; } = "";
    public DriverProgress? Progress { get; set; }
    public ReceiptEvidence? Receipt { get; set; }
    public JobEvidence? Job { get; set; }
    public bool ExitMatchesExpected { get; set; }
}

internal sealed class RoundEvidence
{
    public int Index { get; set; }
    public string Kind { get; set; } = "stability";
    public int RequiredSeconds { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public DateTimeOffset? BothReadyAtUtc { get; set; }
    public DateTimeOffset? StopFileWrittenAtUtc { get; set; }
    public double? ObservedOverlapSeconds { get; set; }
    public double? RequiredOverlapSeconds { get; set; }
    public bool OverlapSatisfied { get; set; }
    public double? PeakCpuPercent { get; set; }
    public ulong? MinAvailableBytes { get; set; }
    public long? PeakDriverActivity { get; set; }
    public AdmissionEvidence? Admission { get; set; }
    public ReservationEvidence? Reservation { get; set; }
    public List<DriverEvidence> Drivers { get; set; } = [];
    public List<ResourceSample> Samples { get; set; } = [];
    public bool StopFileObservedByBoth { get; set; }
    public bool AllJobsDrained { get; set; }
    public bool AllWorkersExited { get; set; }
    public bool AllDriversSucceeded { get; set; }
    public bool Passed { get; set; }
    public string? Failure { get; set; }
}

internal sealed class StabilityEvidence
{
    public bool Eligible { get; set; }
    public int CompletedRounds { get; set; }
    public int RequiredRounds { get; set; }
    public int RequiredSeconds { get; set; }
    public bool Consecutive { get; set; }
    public string Claim { get; set; } = "";
    public List<string> Reasons { get; set; } = [];
}

internal sealed class CoverageEvidence
{
    public bool ModelFree { get; set; } = true;
    public bool UserPromptsSent { get; set; }
    public bool LoginOrProviderTrafficSent { get; set; }
    public bool BackgroundModelCatalogDiscoveryMayOccur { get; set; } = true;
    public bool SdkOrInternalHostShimUsed { get; set; }
    public string Statement { get; set; } = "Model-free OMP RPC tool/native/IO workload only. No prompts or inference requests are sent by this runner. OMP startup may refresh public model catalogs. The test does not establish model, extension, login, network, interactive CLI, or production health acceptance.";
    public List<string> Limitations { get; set; } =
    [
        "This workload does not replace two-hour interactive Chinese CLI, accessibility, or clean-machine distribution acceptance.",
        "BashResult data fields were not enumerated in the provided RPC documentation; per-iteration marker files and reply success are required evidence.",
        "The runner never answers prompts and never replays an unknown request."
    ];
}

internal sealed class CleanupEvidence
{
    public bool Verified { get; set; }
    public bool OwnJobsTerminated { get; set; }
    public bool OwnProcessesTerminated { get; set; }
    public bool MonitorStopped { get; set; }
    public bool MonitorLeftRunning { get; set; }
    public List<string> Actions { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool FilesPreserved { get; set; } = true;
}

internal sealed class SoakResult
{
    public int SchemaVersion { get; set; } = 1;
    public string Status { get; set; } = "running";
    public string Mode { get; set; } = "";
    public string EvidenceStatus { get; set; } = "operator-unverified";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string Root { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public EnvironmentEvidence? Environment { get; set; }
    public AdmissionEvidence? Preflight { get; set; }
    public List<RoundEvidence> Rounds { get; set; } = [];
    public StabilityEvidence Stability { get; set; } = new();
    public CoverageEvidence Coverage { get; set; } = new();
    public CleanupEvidence Cleanup { get; set; } = new();
    public List<string> ProtocolNotes { get; set; } = [];
    public string? Blocker { get; set; }
    public string? Failure { get; set; }
}

internal sealed class AdmissionDeniedException(string message) : Exception(message);

internal sealed class ExecutionFailureException(string message) : Exception(message);

internal sealed class VersionMismatchException(string message) : Exception(message);
