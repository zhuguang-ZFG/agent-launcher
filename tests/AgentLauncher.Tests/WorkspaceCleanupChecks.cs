using System.Diagnostics;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class WorkspaceCleanupChecks
{
    internal static void Run(Action<string, Action> test)
    {
    // ACC-WC01 worktree cleanup eligibility (2026-11-13): explicit user cleanup is allowed only when no live session
    // occupies the directory and git reports the tree completely clean; branches are never touched.
    test("ACC-WC01 active session always blocks cleanup", () =>
    {
        var decision = WorkspaceCleanup.Evaluate(sessionActive: true, statusPorcelain: "");
        Assert(!decision.Allowed, "cleanup allowed while a session occupies the directory");
        Assert(decision.Reason.Contains("会话"), "active-session denial lacks an explanation: " + decision.Reason);
    });
    test("ACC-WC01 any tracked or untracked change blocks cleanup but keeps the branch", () =>
    {
        foreach (var porcelain in new[] { " M src/a.cs", "M  staged.cs", "?? .env", " M a\n?? b\n", "UU conflicted.cs", "!! .env", "!! bin/" })
        {
            var decision = WorkspaceCleanup.Evaluate(sessionActive: false, statusPorcelain: porcelain);
            Assert(!decision.Allowed, "cleanup allowed with changes: " + porcelain.Replace("\n", "\\n"));
            Assert(decision.Reason.Contains("分支"), "dirty denial must state the branch is kept: " + decision.Reason);
        }
    });
    test("ACC-WC01 clean tree with no active session is removable", () =>
    {
        var decision = WorkspaceCleanup.Evaluate(sessionActive: false, statusPorcelain: "");
        Assert(decision.Allowed, "clean inactive worktree blocked");
        Assert(decision.Reason.Contains("分支"), "allowance must disclose the branch survives: " + decision.Reason);
    });
    test("ACC-WC01 unknown git state is never clean", () =>
    {
        var decision = WorkspaceCleanup.Evaluate(sessionActive: false, statusPorcelain: null);
        Assert(!decision.Allowed, "unknown git state treated as clean");
    });
    // Advisory 2026-11-13: occupancy must come from a fresh, complete monitor status; the display cache may be a
    // stale preview. Unavailable/unknown occupancy always refuses; the post-confirm re-check must still deny a
    // session that appeared during the confirm box.
    test("ACC-WC02 unknown monitor occupancy always blocks cleanup", () =>
    {
        var decision = WorkspaceCleanup.Evaluate(sessionActive: null, statusPorcelain: "");
        Assert(!decision.Allowed, "cleanup allowed with unknown occupancy");
        Assert(decision.Reason.Contains("监控"), "unknown-occupancy denial must say the monitor state is required: " + decision.Reason);
    });
    test("ACC-WC02 post-confirm re-check denies a session that appeared mid-confirm", () =>
    {
        var before = WorkspaceCleanup.Evaluate(sessionActive: false, statusPorcelain: "");
        Assert(before.Allowed, "pre-confirm state should allow");
        var after = WorkspaceCleanup.Evaluate(sessionActive: true, statusPorcelain: "");
        Assert(!after.Allowed, "session appearing during confirm did not block removal");
        Assert(after.Reason.Contains("会话"), "re-check denial must explain the occupying session: " + after.Reason);
    });
    // Code review 2026-11-13: occupancy must use physical identity (PhysicalDirectory.Resolve + Overlaps), not raw
    // string equality — monitor entries carry resolved roots; a session may also anchor in any checkout subdirectory.
    test("ACC-WC04 occupancy uses physical identity and covers subdirectory anchors", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "wc04-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sub = Path.Combine(root, "sub", "dir");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere"); // worktree-style pointer file
            var elsewhere = Path.Combine(root, "..", "wc04-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(elsewhere);
            try
            {
                ControlEntry Entry(string path, string state) => new()
                {
                    Plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "a", path, PhysicalDirectory.Workspace(path), false,
                        new CommandSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], "exe"), ResourcePolicy.AgentBudget),
                    State = state,
                    Detail = "a",
                };
                var live = Entry(sub, "Started");
                var terminal = Entry(sub, "Settled");
                var other = Entry(elsewhere, "Started");

                Assert(WorkspaceCleanupShell.OccupiedByLiveSession([live], root) == true, "live session in a subdirectory not detected");
                var spelled = root + Path.DirectorySeparatorChar; // raw record spelling may differ from the resolved path
                Assert(WorkspaceCleanupShell.OccupiedByLiveSession([live], spelled) == true, "spelling difference hid occupancy");
                Assert(WorkspaceCleanupShell.OccupiedByLiveSession([terminal], root) == false, "terminal session counts as occupying");
                Assert(WorkspaceCleanupShell.OccupiedByLiveSession([other], root) == false, "unrelated session counts as occupying");
                Assert(WorkspaceCleanupShell.OccupiedByLiveSession(null, root) == null, "missing entries must mean unknown, not free");
                Assert(WorkspaceCleanupShell.OccupiedByLiveSession([], root) == false, "empty entries must mean free");
            }
            finally { Directory.Delete(elsewhere, true); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
    test("ACC-WC05 real clean worktree removal preserves branch and committed content", () =>
    {
        using var fixture = new CleanupFixture();
        Assert(fixture.Status() == "", "clean worktree reported changes");
        var head = fixture.Git(fixture.Target, "rev-parse", "HEAD").Trim();
        fixture.Remove();
        Assert(!Directory.Exists(fixture.Target), "clean worktree was not removed");
        Assert(fixture.Git(fixture.Root, "rev-parse", "refs/heads/launcher-test").Trim() == head, "branch lost its commit");
        Assert(fixture.Git(fixture.Root, "show", "launcher-test:tracked.txt").Trim() == "original", "committed content changed");
    });
    test("ACC-WC05 removal rechecks ignored data created after the initial inspection", () =>
    {
        using var fixture = new CleanupFixture();
        Assert(fixture.Status() == "", "fixture was not initially clean");
        var local = Path.Combine(fixture.Target, ".env");
        File.WriteAllText(local, "local data");
        Assert(fixture.Status().Contains("!! .env"), "ignored file was hidden from inspection");
        Refuses(fixture.Remove, "remove accepted ignored data after inspection");
        Assert(File.ReadAllText(local) == "local data", "ignored data was removed");
    });
    test("ACC-WC06 detached worktree with an unreferenced commit is preserved", () =>
    {
        using var fixture = new CleanupFixture();
        fixture.Git(fixture.Target, "checkout", "--detach");
        fixture.Git(fixture.Target, "commit", "--allow-empty", "-m", "detached work");
        var head = fixture.Git(fixture.Target, "rev-parse", "HEAD").Trim();
        Refuses(() => fixture.Status(), "detached HEAD passed inspection");
        Refuses(fixture.Remove, "detached HEAD was removed");
        Assert(fixture.Git(fixture.Target, "rev-parse", "HEAD").Trim() == head, "detached commit changed");
    });
    test("ACC-WC07 index flags cannot hide tracked edits from cleanup", () =>
    {
        foreach (var flag in new[] { "--assume-unchanged", "--skip-worktree" })
        {
            using var fixture = new CleanupFixture();
            fixture.Git(fixture.Target, "update-index", flag, "tracked.txt");
            var tracked = Path.Combine(fixture.Target, "tracked.txt");
            File.WriteAllText(tracked, "hidden local edits");
            Assert(fixture.Git(fixture.Target, "status", "--porcelain=v1").Length == 0, "fixture did not hide the edit: " + flag);
            Refuses(() => fixture.Status(), "hidden index entry passed inspection: " + flag);
            Refuses(fixture.Remove, "hidden edit was removed: " + flag);
            Assert(File.ReadAllText(tracked) == "hidden local edits", "hidden edit changed: " + flag);
        }
    });
    }

    private static void Refuses(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception(message);
    }

    private sealed class CleanupFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "AgentLauncher-tests-workspace-cleanup-" + Guid.NewGuid().ToString("N"));
        private readonly string git = Commands.FindExe("git.exe") ?? throw new IOException("Git is required for cleanup checks.");
        public string Root => Path.Combine(directory, "source");
        public string Target => Path.Combine(directory, "worktree");

        public CleanupFixture()
        {
            try
            {
                Directory.CreateDirectory(Root);
                Git(Root, "init", "-b", "main");
                Git(Root, "config", "user.name", "Cleanup Test");
                Git(Root, "config", "user.email", "cleanup@example.invalid");
                File.WriteAllText(Path.Combine(Root, "tracked.txt"), "original\n");
                File.WriteAllText(Path.Combine(Root, ".gitignore"), ".env\n");
                Git(Root, "add", ".");
                Git(Root, "commit", "-m", "initial");
                Git(Root, "worktree", "add", "-b", "launcher-test", Target);
            }
            catch { Dispose(); throw; }
        }

        public string Status() => WorkspaceCleanupShell.ReadStatusPorcelainAsync(git, Target, CancellationToken.None).GetAwaiter().GetResult();
        public void Remove() => WorkspaceCleanupShell.RemoveAsync(git, Root, Target, CancellationToken.None).GetAwaiter().GetResult();

        public string Git(string cwd, params string[] arguments)
        {
            var start = new ProcessStartInfo(git) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-c", "commit.gpgSign=false", "-c", "core.hooksPath=" }) start.ArgumentList.Add(arg);
            foreach (var arg in arguments) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Git fixture did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Git fixture timed out."); }
            if (process.ExitCode != 0) throw new IOException("Git fixture failed: " + error.GetAwaiter().GetResult());
            return output.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
