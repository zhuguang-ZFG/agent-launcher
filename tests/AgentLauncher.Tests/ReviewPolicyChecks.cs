using AgentLauncher.Core;

namespace AgentLauncher.Tests;

internal static class ReviewPolicyChecks
{
    public static void Run(Action<string, Action> test, string temp)
    {
        test("VC-C01 CI evidence must match the complete requested SHA and include success", () =>
        {
            var sha = new string('a', 40); var old = new string('b', 40);
            CiRun Run(long id, string commit, string status, string? conclusion) => new(id, "gate", commit, status, conclusion, "https://github.com/example/repo/actions/runs/" + id);
            Assert(ReviewPolicy.CiState(sha, [Run(1, old, "completed", "success")]) == "NoWorkflow", "old commit success was assigned to the requested commit");
            Assert(ReviewPolicy.CiState(sha, [Run(1, old, "completed", "success"), Run(2, sha, "in_progress", null)]) == "Running", "new commit in progress was reported green");
            Assert(ReviewPolicy.CiState(sha, [Run(1, sha, "completed", "failure")]) == "Failed", "failed target check was not reported failed");
            Assert(ReviewPolicy.CiState(sha, [Run(1, sha, "completed", "skipped")]) == "Unknown", "skipped-only evidence was reported as a passing check");
            Assert(ReviewPolicy.CiState(sha, [Run(1, sha, "completed", "success")]) == "Passed", "completed target success was not accepted");
        });
        test("VC-D01 verification freshness binds task attempt commit contents and command", () =>
        {
            var attempt = Guid.NewGuid();
            var snapshot = new ReviewSnapshot(temp, "workspace", new string('a', 40), new string('a', 40), "index", "content", [], DateTimeOffset.UtcNow);
            var result = new VerificationResult(Guid.NewGuid(), attempt, snapshot.HeadSha, snapshot.WorktreeFingerprint, Guid.NewGuid(), "command", "Passed", 0, 1, "", "", DateTimeOffset.UtcNow, snapshot.WorkspaceKey);
            Assert(ReviewPolicy.IsCurrent(result, attempt, snapshot, "command"), "matching evidence was stale");
            Assert(!ReviewPolicy.IsCurrent(result, Guid.NewGuid(), snapshot, "command"), "another attempt reused the result");
            Assert(!ReviewPolicy.IsCurrent(result, attempt, snapshot with { HeadSha = new string('b', 40) }, "command"), "changed HEAD reused the result");
            Assert(!ReviewPolicy.IsCurrent(result, attempt, snapshot with { WorktreeFingerprint = "new-content" }, "command"), "changed working content reused the result");
            Assert(!ReviewPolicy.IsCurrent(result, attempt, snapshot, "new-command"), "changed check definition reused the result");
        });
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
