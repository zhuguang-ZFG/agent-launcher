using AgentLauncher.Core;
using AgentLauncher.Windows;
using AgentLauncher.Worker;

namespace AgentLauncher.Tests;

internal static class RemoteReliabilityChecks
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("C1 repeated remote resume preserves options and replaces the prior task", () =>
        {
            string[] arguments = ["-p", "--model", "chosen-model", "--no-session", "original task"];
            for (var i = 1; i <= 3; i++)
            {
                arguments = RemoteAgentMaintenance.ResumeArguments(arguments, ["--continue"], "task " + i);
                assert(arguments.SequenceEqual(new[] { "-p", "--model", "chosen-model", "--continue", "task " + i }), "Repeated resume changed options, duplicated --continue, or retained the old task");
            }
            foreach (var selector in new[] { "-c", "--resume", "-r", "--session", "--session=abc", "--continue=abc" })
            {
                var rejected = false;
                try { RemoteAgentMaintenance.ResumeArguments(["-p", selector, "old task"], ["--continue"], "new task"); }
                catch (InvalidDataException e) { rejected = !e.Message.Contains("详情") && e.Message.Contains("会话"); }
                assert(rejected, "A distinct session selector was accepted or its error advised editing an immutable historical plan: " + selector);
            }
        });

        test("R24 SSH loss and local disposal preserve synchronization until remote exit is known", () =>
        {
            var fakeSsh = Path.Combine(AppContext.BaseDirectory, "ssh.exe");
            assert(File.Exists(fakeSsh), "fake SSH missing");
            foreach (var mode in new[] { "disconnect", "running", "completed", "not-started" })
            {
                var spec = new RemoteExecutionSpec("fake.example.com", 22, "testuser", "agent", "omp", "/home/agent/work", mode == "disconnect" ? ["disconnect"] : [], null, null);
                var sync = new TrackingSyncSession();
                var backend = SshRunnerBackend.Create(spec);
                backend.StartInfo.FileName = fakeSsh;
                backend.SyncSession = sync;
                try
                {
                    if (mode != "not-started") backend.Start();
                    if (mode is "disconnect" or "completed") assert(backend.WaitForExit(10000), "fake SSH did not exit");
                    backend.Dispose();
                    assert(sync.Disposals == (mode is "completed" or "not-started" ? 1 : 0), "Incorrect sync cleanup for " + mode);
                }
                finally { backend.Dispose(); }
            }
        });

        test("R33 reattach shell states that agent terminal and conversation are not restored", () =>
        {
            var shell = RemoteReattach.AttachShell(Guid.NewGuid(), "/home/agent/work");
            assert(shell.Contains("工作 Shell") && shell.Contains("未恢复原 Agent") && shell.Contains("exec bash"), "Remote working shell masquerades as the old Agent terminal");
            assert(LaunchSurface.ReattachIsNewPane.Contains("未恢复") && LaunchSurface.SyncHonesty(WorkspaceSyncMode.SshfsMount).Contains("不会上传"), "Reattach or SSHFS UI hides its actual behavior");
        });

        test("R33 saved Mutagen session reuses exact endpoints and fails closed on uncertainty", () =>
        {
            const string name = "agent-launcher-reconnect";
            var local = Path.Combine(Path.GetTempPath(), "AgentLauncher-tests-reconnect");
            var spec = new WorkspaceSyncSpec(WorkspaceSyncMode.Mutagen, local, "/home/agent/work", name, null);
            var remote = new RemoteExecutionSpec("fixture.example.com", 22, "root", "agent", "omp", spec.RemotePath, [], null, null, spec);
            var output = $"Name: {name}\nSynchronization mode: Two Way Safe\nAlpha:\n    URL: {local}\nBeta:\n    URL: agent@fixture.example.com:22:/home/agent/work\nStatus: Watching for changes\n";
            var creates = 0;
            Task<IWorkspaceSyncSession> Create() { creates++; return Task.FromResult<IWorkspaceSyncSession>(new TrackingSyncSession()); }
            var session = WorkspaceSync.RestoreMutagenAsync(spec, remote, output, default, Create).GetAwaiter().GetResult();
            assert(creates == 0 && session.SessionName == name && session.GetRemoteWorkingDirectory() == spec.RemotePath, "Reconnecting duplicated a live matching session");
            foreach (var uncertain in new[] { "", "Name: " + name, output.Replace("agent@fixture", "other@fixture"), output.Replace("/home/agent/work", "/home/agent/other"), output + "----------\n" + output })
            {
                var failed = false;
                try { WorkspaceSync.RestoreMutagenAsync(spec, remote, uncertain, default, Create).GetAwaiter().GetResult(); }
                catch (IOException) { failed = true; }
                assert(failed && creates == 0, "Uncertain or mismatched session triggered new sync or was reused");
            }
            WorkspaceSync.RestoreMutagenAsync(spec, remote, "No sessions found", default, Create).GetAwaiter().GetResult();
            assert(creates == 1, "A confirmed missing session was not recreated");
        });
    }

    private sealed class TrackingSyncSession : IWorkspaceSyncSession
    {
        public int Disposals { get; private set; }
        public string? SessionName => "agent-launcher-fixture";
        public Task WaitReadyAsync(CancellationToken cancellation = default) => Task.CompletedTask;
        public string GetRemoteWorkingDirectory() => "/home/agent/work";
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
        public Task<string?> TryDisposeAsync() { Disposals++; return Task.FromResult<string?>(null); }
    }
}
