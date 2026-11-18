using System.Diagnostics;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static partial class RemoteExecutionIntegration
{
    // R33：fake-ssh，不打开真实 VPS。确认才挂接；成功不终态化；进程已不在则拒绝且不释放；没有自动重连；8 名额不变。
    private static async Task ReattachAsync(Action<bool, string> Assert, Func<string, Func<Task>, Task> Test, List<Process> workerProcesses, string fakeSshDir, string? originalPath)
    {
        await Test("R33 operator-confirmed reattach", async () =>
        {
            var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
            async Task<ControlEntry?> EntryAsync(Guid id)
            {
                var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status.Ok, "Status failed: " + status.Message);
                return status.Entries?.FirstOrDefault(e => e.Plan.Id == id);
            }
            async Task<ControlEntry> WaitAsync(Guid id, string state)
            {
                ControlEntry? entry = null;
                for (var i = 0; i < 48; i++)
                {
                    entry = await EntryAsync(id);
                    if (entry?.State == state) return entry;
                    await Task.Delay(250);
                }
                throw new Exception($"wanted {state}, got {entry?.State}: {entry?.Detail}");
            }
            int LiveAgents(Response status) => status.Entries?.Count(e => (e.Plan.Kind is null or "agent") && !Store.IsTerminal(e)) ?? 0;
            LaunchItem Make(string name, string host, string[] args, WorkspaceSyncSpec? sync = null)
            {
                var dir = Path.Combine(Store.Root, name);
                Directory.CreateDirectory(dir);
                return new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, name, dir, PhysicalDirectory.Workspace(dir), false, new CommandSpec("pi", args, "remote"), 0,
                    RemoteExecution: new RemoteExecutionSpec(host, 22, "testuser", "agent", "pi", "/home/agent/work", args, null, null, sync));
            }
            async Task ReserveAsync(LaunchItem plan)
            {
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                Assert(reserved.Ok, "reserve failed: " + reserved.Message);
            }
            Process StartPane(Guid id, bool reattach, bool fakeSync)
            {
                var start = new ProcessStartInfo(Store.WorkerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("pane");
                start.ArgumentList.Add(id.ToString());
                if (reattach) start.ArgumentList.Add("reattach");
                start.Environment["AGENT_LAUNCHER_TEST_ROOT"] = Store.Root;
                start.Environment["PATH"] = fakeSshDir + Path.PathSeparator + originalPath;
                if (fakeSync) start.Environment["AGENT_LAUNCHER_FAKE_SYNC"] = "1";
                var process = Process.Start(start)!;
                process.StandardInput.Close();
                workerProcesses.Add(process);
                return process;
            }

            var plan = Make("reattach-none", "reattach-alive.example.com", ["disconnect"]);
            await ReserveAsync(plan);
            var pending = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: plan.Id, Reattach: new OperatorReattach(true)));
            Assert(!pending.Ok, "Pending entry was reattached");
            Assert((await EntryAsync(plan.Id))!.State == "Pending", "Pending reattach changed state");
            Assert(!File.Exists(Store.OperatorReattachPath(plan.Id)), "Pending reattach wrote evidence");

            StartPane(plan.Id, false, false);
            var disconnected = await WaitAsync(plan.Id, "Disconnected");
            Assert(!Store.IsTerminal(disconnected), "Disconnected became terminal");
            await Task.Delay(1200);
            var still = await EntryAsync(plan.Id);
            Assert(still?.State == "Disconnected" && !File.Exists(Store.OperatorReattachPath(plan.Id)), "disconnected entry reattached without an operator request");

            var unconfirmed = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: plan.Id, Reattach: new OperatorReattach(false)));
            Assert(!unconfirmed.Ok && (await EntryAsync(plan.Id))!.State == "Disconnected" && !File.Exists(Store.OperatorReattachPath(plan.Id)), "unconfirmed reattach changed the ledger");
            var missing = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: plan.Id));
            Assert(!missing.Ok && !File.Exists(Store.OperatorReattachPath(plan.Id)), "missing confirmation was accepted");
            var releaseInstead = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: plan.Id, Release: new OperatorRelease("Running", "进程仍在", true)));
            Assert(!releaseInstead.Ok && (await EntryAsync(plan.Id))!.State == "Disconnected" && !File.Exists(Store.OperatorReattachPath(plan.Id)) && !File.Exists(Store.OperatorReleasePath(plan.Id)), "release started reattach or changed the ledger");

            var before = await Ipc.SendAsync(new("status", Guid.NewGuid()));
            var beforeCount = LiveAgents(before);
            var authorized = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: plan.Id, Reattach: new OperatorReattach(true)));
            Assert(authorized.Ok, "confirmed reattach failed: " + authorized.Message);
            Assert(authorized.Entries is { Length: 1 } && authorized.Entries[0].State == "Reattaching" && !Store.IsTerminal(authorized.Entries[0]), "reattach settled or added entries");
            var evidence = Store.Read<OperatorReattachRecord>(Store.OperatorReattachPath(plan.Id));
            Assert(evidence is { SchemaVersion: 1, Confirmed: true, Probe: "ALIVE", SyncMode: "None" } && evidence.AttemptId == plan.Id && evidence.Host == "reattach-alive.example.com" && evidence.RemoteDirectory == "/home/agent/work", "operator-reattach.json invalid");
            Assert(!File.Exists(Store.OperatorReleasePath(plan.Id)), "reattach wrote operator-release.json");
            Assert(Store.Read<Ledger>(Store.FilePath("control.json"))!.Entries.Count(e => e.Plan.Id == plan.Id) == 1, "reattach created a second ledger entry");

            StartPane(plan.Id, true, false);
            var attached = await WaitAsync(plan.Id, "Started");
            Assert(attached.Detail.Contains("不视为已同步") && !attached.Detail.Contains("双向同步"), "None sync was described as synced");
            Assert(!File.Exists(Store.SyncSessionPath(plan.Id)) && !File.Exists(Store.SyncFinalPath(plan.Id)), "None reattach wrote sync evidence");
            var during = await Ipc.SendAsync(new("status", Guid.NewGuid()));
            Assert(LiveAgents(during) == beforeCount && during.Entries!.Count(e => e.Plan.Id == plan.Id) == 1 && !Store.IsTerminal(during.Entries!.Single(e => e.Plan.Id == plan.Id)), "reattach changed the 8-pane count or settled the entry");
            var releaseWhileAttached = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: plan.Id, Release: new OperatorRelease("Gone", "已核对", true)));
            Assert(!releaseWhileAttached.Ok && (await EntryAsync(plan.Id))!.State == "Started" && !File.Exists(Store.OperatorReleasePath(plan.Id)), "release while attached settled the entry");
            var ended = await WaitAsync(plan.Id, "Disconnected");
            Assert(!Store.IsTerminal(ended) && !File.Exists(Store.OperatorReleasePath(plan.Id)), "attachment exit settled or released the attempt");

            var syncDir = Path.Combine(Store.Root, "reattach-sync");
            Directory.CreateDirectory(syncDir);
            var syncPlan = Make("reattach-sync", "reattach-alive.example.com", ["disconnect"], new WorkspaceSyncSpec(WorkspaceSyncMode.Mutagen, syncDir, "/home/agent/work", null, null));
            await ReserveAsync(syncPlan);
            StartPane(syncPlan.Id, false, true);
            await WaitAsync(syncPlan.Id, "Disconnected");
            var oldSession = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(syncPlan.Id));
            Assert(oldSession is not null && File.Exists(Store.SyncFinalPath(syncPlan.Id)), "initial sync evidence missing");
            var syncOk = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: syncPlan.Id, Reattach: new OperatorReattach(true)));
            Assert(syncOk.Ok, "sync reattach was refused: " + syncOk.Message);
            StartPane(syncPlan.Id, true, true);
            var synced = await WaitAsync(syncPlan.Id, "Started");
            var newSession = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(syncPlan.Id));
            Assert(synced.Detail.Contains("双向同步") && newSession is not null && newSession.Name != oldSession!.Name && !File.Exists(Store.SyncFinalPath(syncPlan.Id)), "sync was not re-established, or a finished sync receipt was left in place");
            Assert(!File.Exists(Store.OperatorReleasePath(syncPlan.Id)) && !Store.IsTerminal(synced), "sync reattach settled the attempt");
            var syncEnded = await WaitAsync(syncPlan.Id, "Disconnected");
            Assert(!Store.IsTerminal(syncEnded) && !File.Exists(Store.SyncFinalPath(syncPlan.Id)) && !File.Exists(Store.OperatorReleasePath(syncPlan.Id)), "sync attachment exit pretended the session had converged or released the slot");

            var gone = Make("reattach-gone", "reattach-gone.example.com", ["disconnect"]);
            await ReserveAsync(gone);
            StartPane(gone.Id, false, false);
            await WaitAsync(gone.Id, "Disconnected");
            var goneReply = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: gone.Id, Reattach: new OperatorReattach(true)));
            Assert(!goneReply.Ok && goneReply.Message.Contains("已不在"), "gone process was reattached: " + goneReply.Message);
            Assert((await EntryAsync(gone.Id))!.State == "Disconnected" && !File.Exists(Store.OperatorReattachPath(gone.Id)) && !File.Exists(Store.OperatorReleasePath(gone.Id)), "gone refusal changed the ledger or auto-released");

            var unsafePlan = Make("reattach-unsafe", "reattach-alive.example.com", ["hold=30"]);
            await ReserveAsync(unsafePlan);
            Store.Save(Store.ReceiptPath(unsafePlan.Id), new Receipt("SpawnIntent", 0, 0, null, "准备启动远程进程", DateTimeOffset.UtcNow));
            var monitorPid = Store.Read<int>(Store.FilePath("monitor-pid.json"));
            if (monitorPid > 0)
            {
                using var monitorProcess = Process.GetProcessById(monitorPid);
                monitorProcess.Kill();
                monitorProcess.WaitForExit(5000);
            }
            var ledger = Store.Read<Ledger>(Store.FilePath("control.json"))!;
            var unsafeEntry = ledger.Entries.Single(e => e.Plan.Id == unsafePlan.Id);
            unsafeEntry.State = "Disconnected";
            unsafeEntry.Digest = Store.Digest(unsafePlan);
            Store.Save(Store.FilePath("control.json"), ledger);
            var restarted = await Ipc.EnsureMonitorAsync();
            Assert(restarted.Ok, "monitor restart failed: " + restarted.Message);
            await WaitAsync(unsafePlan.Id, "Disconnected");
            var unsafeReply = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: unsafePlan.Id, Reattach: new OperatorReattach(true)));
            Assert(!unsafeReply.Ok && unsafeReply.Message.Contains("尚未进入已启动"), "creation-interrupted entry was reattached: " + unsafeReply.Message);
            Assert((await EntryAsync(unsafePlan.Id))!.State == "Disconnected" && !File.Exists(Store.OperatorReattachPath(unsafePlan.Id)), "unsafe refusal changed the ledger");

            var counted = await Ipc.SendAsync(new("status", Guid.NewGuid()));
            var live = LiveAgents(counted);
            var n = 0;
            while (live < ResourcePolicy.MaxConcurrentAgents)
            {
                var filler = Make("reattach-cap-" + n, "cap.example.com", []);
                await ReserveAsync(filler);
                live++;
                n++;
            }
            Assert(ResourcePolicy.MaxConcurrentAgents == 8, "cap constant changed");
            var ninth = Make("reattach-cap-ninth", "cap.example.com", []);
            var denied = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [ninth], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([ninth]), DraftRevision: 3));
            Assert(!denied.Ok && denied.Message.Contains("最多 8"), "ninth pane was admitted while disconnected/reattached entries were live: " + denied.Message);
            var recount = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: plan.Id, Reattach: new OperatorReattach(true)));
            Assert(recount.Ok && recount.Entries![0].State == "Reattaching" && !Store.IsTerminal(recount.Entries[0]), "second confirm reattach settled the entry: " + recount.Message);
            var after = Make("reattach-cap-after", "cap.example.com", []);
            var deniedAgain = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [after], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([after]), DraftRevision: 3));
            Assert(!deniedAgain.Ok && deniedAgain.Message.Contains("最多 8"), "reattach freed a pane slot: " + deniedAgain.Message);
            Assert(Store.Read<Ledger>(Store.FilePath("control.json"))!.Entries.Count(e => e.Plan.Id == plan.Id) == 1, "cap reattach duplicated the entry");
        });
    }
}
