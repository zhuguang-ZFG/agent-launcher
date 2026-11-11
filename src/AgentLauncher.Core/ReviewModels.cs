namespace AgentLauncher.Core;

public sealed record ReviewFile(string Path, string Status, bool Untracked, bool Binary, long SizeBytes, string ContentHash);
public sealed record ReviewSnapshot(string WorkspacePath, string WorkspaceKey, string BaseSha, string HeadSha,
    string IndexFingerprint, string WorktreeFingerprint, ReviewFile[] Files, DateTimeOffset At);
public sealed record ReviewDiff(string Text, bool Truncated, string Summary);
public sealed record VerificationProfile(Guid Id, string Name, CommandSpec Command, int TimeoutSeconds);
public sealed record VerificationResult(Guid Id, Guid TaskRunId, string HeadSha, string WorktreeFingerprint,
    Guid CommandId, string CommandFingerprint, string State, int? ExitCode, long DurationMilliseconds,
    string Output, string Error, DateTimeOffset At, string WorkspaceKey);
public sealed record CiRun(long Id, string Name, string Sha, string Status, string? Conclusion, string Url);
public sealed record CiReport(string Repository, string Sha, string State, string Message, CiRun[] Runs);
public sealed record ManagedPreviewPort(int Port, int ProcessId, string Url);

public sealed class ReviewBook
{
    public int SchemaVersion { get; set; } = 1;
    public List<VerificationProfile> Profiles { get; set; } = [];
    public List<VerificationResult> Results { get; set; } = [];
}

public static class ReviewPolicy
{
    public static bool IsCurrent(VerificationResult result, Guid taskRunId, ReviewSnapshot snapshot, string commandFingerprint) =>
        result.TaskRunId == taskRunId && string.Equals(result.HeadSha, snapshot.HeadSha, StringComparison.Ordinal)
        && string.Equals(result.WorkspaceKey, snapshot.WorkspaceKey, StringComparison.Ordinal)
        && string.Equals(result.WorktreeFingerprint, snapshot.WorktreeFingerprint, StringComparison.Ordinal)
        && string.Equals(result.CommandFingerprint, commandFingerprint, StringComparison.Ordinal);

    public static string CiState(string sha, IEnumerable<CiRun> runs)
    {
        var matching = runs.Where(run => string.Equals(run.Sha, sha, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length == 0) return "NoWorkflow";
        if (matching.Any(run => run.Status != "completed")) return "Running";
        if (matching.Any(run => run.Conclusion is "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure")) return "Failed";
        return matching.Any(run => run.Conclusion == "success") && matching.All(run => run.Conclusion is "success" or "neutral" or "skipped") ? "Passed" : "Unknown";
    }

    public static void ValidateProfile(VerificationProfile profile)
    {
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 120)
            throw new InvalidDataException("检查名称或身份无效。");
        if (profile.TimeoutSeconds is < 1 or > 3600) throw new InvalidDataException("检查超时必须在 1–3600 秒内。");
        if (string.IsNullOrWhiteSpace(profile.Command.Executable) || profile.Command.Executable.Contains('\0')
            || profile.Command.Arguments.Length > 64 || profile.Command.Arguments.Any(value => value.Contains('\0') || value.Length > 8192))
            throw new InvalidDataException("检查命令无效；请逐项填写程序与参数。");
    }
}
