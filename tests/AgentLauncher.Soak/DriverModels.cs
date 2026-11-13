namespace SoakRunner;

internal sealed class DriverProgress
{
    public int SchemaVersion { get; set; } = 1;
    public string Phase { get; set; } = "starting";
    public string? Status { get; set; }
    public string? FailureKind { get; set; }
    public int Pid { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? ReadyAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public string? StopReason { get; set; }
    public int OmpPid { get; set; }
    public long OmpProcessTimeUtc { get; set; }
    public int? OmpExitCode { get; set; }
    public bool OmpForcedTermination { get; set; }
    public string ExpectedVersion { get; set; } = "";
    public string? ActualVersion { get; set; }
    public ReadyInfo? Ready { get; set; }
    public int Cycles { get; set; }
    public long ActivityCount { get; set; }
    public Dictionary<string, long> Attempts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Successes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Failures { get; set; } = new(StringComparer.Ordinal);
    public DateTimeOffset? FirstCycleAtUtc { get; set; }
    public DateTimeOffset? LastCycleAtUtc { get; set; }
    public DateTimeOffset? LastActivityAtUtc { get; set; }
    public string? LastError { get; set; }
    public string? LastCommandKind { get; set; }
    public long LastCommandLatencyMs { get; set; }
    public List<string> BashResponseDataKeysObserved { get; set; } = [];
    public bool BashExitCodeObserved { get; set; }
    public string ProtocolVerification { get; set; } = "";
    public bool ReplayAttempted { get; set; }
    public List<string> EnvironmentKeys { get; set; } = [];
    public int CredentialLikeParentVariables { get; set; }
    public string EnvironmentPolicy { get; set; } = "allowlist";
    public string Workspace { get; set; } = "";
    public string AgentDirectory { get; set; } = "";
    public string HomeDirectory { get; set; } = "";
    public long OmpStderrCharacters { get; set; }
    public bool OmpStderrTruncated { get; set; }
    public string OmpStderrLog { get; set; } = "";
}
