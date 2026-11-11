#if INTEGRATION_TESTS
using System.IO;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

/// <summary>
/// Consumer-visible S4 review boundaries. These cover what the window must not let the user get wrong
/// (reviewing another directory, stale passes, CI color, non-loopback previews, results from another attempt)
/// without touching a real repository or network. The main thread owns the real-window smoke scenario.
/// </summary>
internal static class ReviewUiChecks
{
    internal static int Run(Action<string, bool> check)
    {
        var count = 0;
        void Check(string name, bool value) { count++; check(name, value); }
        void Throws(string name, Action action)
        {
            try { action(); Check(name, false); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException) { Check(name, true); }
        }

        var attempt = Guid.NewGuid();
        var batch = Guid.NewGuid();
        const string key = "volume:file";
        const string workspace = @"C:\review\worktree";
        var plan = new LaunchItem(attempt, batch, 1, "review", workspace, new WorkspaceIdentity(workspace, key), false,
            new CommandSpec(@"C:\Windows\System32\cmd.exe", [], "exe"), ResourcePolicy.AgentBudget);
        var entry = new ControlEntry { Plan = plan, State = "Settled" };

        Check("S4 review: an attempt without a plan cannot be mapped to a workspace path",
            ReviewWindowPolicy.LockedWorkspacePath(null, attempt, batch, key) is null
            && ReviewWindowPolicy.LockedWorkspacePath([], attempt, batch, key) is null
            && ReviewWindowPolicy.LockedWorkspacePath([entry], Guid.NewGuid(), batch, key) is null);
        Check("S4 review: the attempt's own plan locks the exact workspace path",
            ReviewWindowPolicy.LockedWorkspacePath([entry], attempt, batch, key) == workspace);
        Throws("S4 review: a plan from another batch is refused instead of reviewed",
            () => ReviewWindowPolicy.LockedWorkspacePath([entry], attempt, Guid.NewGuid(), key));
        Throws("S4 review: a plan whose workspace key differs from the run is refused instead of reviewed",
            () => ReviewWindowPolicy.LockedWorkspacePath([entry], attempt, batch, "volume:other"));
        Throws("S4 review: a run without a workspace key is refused",
            () => ReviewWindowPolicy.LockedWorkspacePath([entry], attempt, batch, ""));
        Check("S4 review: the physical workspace key must be non-empty and identical to the attempt record",
            ReviewWindowPolicy.WorkspaceKeyMatches(key, new WorkspaceIdentity(workspace, key))
            && !ReviewWindowPolicy.WorkspaceKeyMatches(key, new WorkspaceIdentity(workspace, "volume:other"))
            && !ReviewWindowPolicy.WorkspaceKeyMatches("", new WorkspaceIdentity(workspace, "")));

        var sha = new string('a', 40);
        var runId = Guid.NewGuid();
        var profile = new VerificationProfile(Guid.NewGuid(), "构建检查", new CommandSpec(@"C:\Windows\System32\cmd.exe", ["/c", "echo ok"], "exe"), 120);
        var fingerprint = ReviewService.CommandFingerprint(profile);
        var snapshot = new ReviewSnapshot(workspace, key, new string('0', 40), sha, "index-fingerprint", "worktree-fingerprint", [], DateTimeOffset.UtcNow);
        var passed = new VerificationResult(Guid.NewGuid(), runId, sha, snapshot.WorktreeFingerprint, profile.Id, fingerprint,
            "Passed", 0, 1500, "build ok", "", DateTimeOffset.UtcNow, snapshot.WorkspaceKey);
        Check("S4 review: a stored pass is current only for the same task run, HEAD, worktree and command",
            ReviewPolicy.IsCurrent(passed, runId, snapshot, fingerprint)
            && !ReviewPolicy.IsCurrent(passed, Guid.NewGuid(), snapshot, fingerprint)
            && !ReviewPolicy.IsCurrent(passed, runId, snapshot with { HeadSha = new string('b', 40) }, fingerprint)
            && !ReviewPolicy.IsCurrent(passed, runId, snapshot with { WorktreeFingerprint = "changed" }, fingerprint)
            && !ReviewPolicy.IsCurrent(passed, runId, snapshot with { WorkspaceKey = "volume:other" }, fingerprint)
            && !ReviewPolicy.IsCurrent(passed, runId, snapshot, ReviewService.CommandFingerprint(
                profile with { Command = profile.Command with { Arguments = ["/c", "echo changed"] } })));
        var otherRun = passed with { Id = Guid.NewGuid(), TaskRunId = Guid.NewGuid(), State = "Failed" };
        var reviewBook = new ReviewBook { Results = [passed, otherRun] };
        var visible = ReviewWindowPolicy.ResultsFor(reviewBook, runId);
        Check("S4 review: results from another task run never appear in this attempt",
            visible.Count == 1 && visible[0].Id == passed.Id);

        var exact = ReviewWindowPolicy.CiAppearance(new CiReport("owner/repo", sha, "Passed", "", []), sha);
        Check("S4 review: CI is green only for an exact-SHA passed report", exact.Green);
        var states = new[] { "Failed", "Running", "NoWorkflow", "Unauthorized", "Unavailable", "Unknown", "SomethingNew" };
        var neverGreen = true;
        foreach (var state in states)
        {
            var appearance = ReviewWindowPolicy.CiAppearance(new CiReport("owner/repo", sha, state, "", []), sha);
            neverGreen &= !appearance.Green;
        }
        Check("S4 review: failed/running/no-workflow/unauthorized/network/unknown CI never looks green", neverGreen);
        var mismatch = ReviewWindowPolicy.CiAppearance(new CiReport("owner/repo", new string('c', 40), "Passed", "", []), sha);
        Check("S4 review: a green report for a different SHA is rejected as a mismatch", !mismatch.Green && mismatch.Error);

        Check("S4 review: preview browser opens only service-returned loopback URLs",
            ReviewWindowPolicy.IsLoopbackUrl("http://127.0.0.1:5173/")
            && ReviewWindowPolicy.IsLoopbackUrl("https://localhost:8443/app")
            && ReviewWindowPolicy.IsLoopbackUrl("http://[::1]:3000/")
            && !ReviewWindowPolicy.IsLoopbackUrl("http://0.0.0.0:5173/")
            && !ReviewWindowPolicy.IsLoopbackUrl("http://192.168.1.20:5173/")
            && !ReviewWindowPolicy.IsLoopbackUrl("http://127.0.0.1.evil.example/")
            && !ReviewWindowPolicy.IsLoopbackUrl("http://127.0.0.1@evil.example/")
            && !ReviewWindowPolicy.IsLoopbackUrl("file:///C:/repo/index.html")
            && !ReviewWindowPolicy.IsLoopbackUrl("javascript:alert(1)")
            && !ReviewWindowPolicy.IsLoopbackUrl(""));

        // The ledger entry must also survive the schema gate the monitor applies before it is trusted for a launch.
        var ledger = new Ledger { SchemaVersion = LedgerMigration.CurrentSchemaVersion };
        ledger.Entries.Add(entry);
        Check("S4 review: a current-schema ledger keeps the attempt's planned workspace",
            ReviewWindowPolicy.LockedWorkspacePath(LedgerMigration.Accept(ledger).Entries, attempt, batch, key) == workspace);

        return count;
    }
}
#endif
