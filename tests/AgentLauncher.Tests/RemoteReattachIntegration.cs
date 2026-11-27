using System.Diagnostics;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static partial class RemoteExecutionIntegration
{
    // R33：fake-ssh，不打开真实 VPS。确认才挂接；成功不终态化；进程已不在则拒绝且不释放；没有自动重连；6 名额不变。
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
                return new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, name, dir, PhysicalDirectory.Workspace(dir), false, new CommandSpec("omp", args, "remote"), 0,
                    AgentId: "omp", RemoteExecution: new RemoteExecutionSpec(host, 22, "testuser", "agent", "omp", "/home/agent/work", args, null, null, sync));
            }
            async Task ReserveAsync(LaunchItem plan)
            {
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                Assert(reserved.Ok, "reserve failed: " + reserved.Message);
            }
            Process StartPane(Guid id, bool reattach, bool fakeSync,string? input=null)
            {
                var start = new ProcessStartInfo(Store.WorkerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("pane");
                start.ArgumentList.Add(id.ToString());
                if (reattach) start.ArgumentList.Add("reattach");
                start.Environment["AGENT_LAUNCHER_TEST_ROOT"] = Store.Root;
                start.Environment["PATH"] = fakeSshDir + Path.PathSeparator + originalPath;
                if (fakeSync) start.Environment["AGENT_LAUNCHER_FAKE_SYNC"] = "1";
                var process = Process.Start(start)!;
                if(input!=null)process.StandardInput.WriteLine(input);
                process.StandardInput.Close();
                workerProcesses.Add(process);
                return process;
            }

            var stdinPlan=Make("interactive-input","verify-stdin.example.com",[]);
            await ReserveAsync(stdinPlan);
            var pane=StartPane(stdinPlan.Id,false,false,"input test");
            await pane.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert(pane.ExitCode==0,"first remote pane did not forward stdin");
            var plan = Make("reattach-none", "slow-probe.example.com", ["disconnect"]);
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
            var probing=Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: plan.Id, Reattach: new OperatorReattach(true)));
            await Task.Delay(500);
            Assert((await EntryAsync(plan.Id).WaitAsync(TimeSpan.FromSeconds(5)))?.State=="Disconnected"&&!probing.IsCompleted,"slow SSH blocked status or prematurely granted reattach");
            var authorized=await probing;
            Assert(authorized.Ok, "confirmed reattach failed: " + authorized.Message);
            Assert(authorized.Entries is { Length: 1 } && authorized.Entries[0].State == "Reattaching" && !Store.IsTerminal(authorized.Entries[0]), "reattach settled or added entries");
            var evidence = Store.Read<OperatorReattachRecord>(Store.OperatorReattachPath(plan.Id));
            Assert(evidence is { SchemaVersion: 1, Confirmed: true, Probe: "ALIVE", SyncMode: "None" } && evidence.AttemptId == plan.Id && evidence.Host == plan.RemoteExecution!.Host && evidence.RemoteDirectory == "/home/agent/work", "operator-reattach.json invalid");
            Assert(!File.Exists(Store.OperatorReleasePath(plan.Id)), "reattach wrote operator-release.json");
            Assert(Store.Read<Ledger>(Store.FilePath("control.json"))!.Entries.Count(e => e.Plan.Id == plan.Id) == 1, "reattach created a second ledger entry");

            StartPane(plan.Id, true, false);
            var attached = await WaitAsync(plan.Id, "Started");
            var receipt=Store.Read<Receipt>(Store.ReceiptPath(plan.Id));
            var duplicate=StartPane(plan.Id,true,false);
            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(duplicate.ExitCode!=0&&Store.Read<Receipt>(Store.ReceiptPath(plan.Id))==receipt,"duplicate pane overwrote live SSH receipt");
            Assert(attached.Detail.Contains("未启用文件同步") && attached.Detail.Contains("未恢复原 Agent") && !attached.Detail.Contains("双向同步"), "None sync or the working shell was misrepresented");
            Assert(!File.Exists(Store.SyncSessionPath(plan.Id)) && !File.Exists(Store.SyncFinalPath(plan.Id)), "None reattach wrote sync evidence");
            var during = await Ipc.SendAsync(new("status", Guid.NewGuid()));
            Assert(LiveAgents(during) == beforeCount && during.Entries!.Count(e => e.Plan.Id == plan.Id) == 1 && !Store.IsTerminal(during.Entries!.Single(e => e.Plan.Id == plan.Id)), "reattach changed the 6-pane count or settled the entry");
            var releaseWhileAttached = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: plan.Id, Release: new OperatorRelease("Gone", "已核对", true)));
            Assert(!releaseWhileAttached.Ok && (await EntryAsync(plan.Id))!.State == "Started" && !File.Exists(Store.OperatorReleasePath(plan.Id)), "release while attached settled the entry");
            var ended = await WaitAsync(plan.Id, "Disconnected");
            Assert(!Store.IsTerminal(ended) && !File.Exists(Store.OperatorReleasePath(plan.Id)), "attachment exit settled or released the attempt");

            var syncDir = Path.Combine(Store.Root, "reattach-sync");
            Directory.CreateDirectory(syncDir);
            var syncPlan = Make("reattach-sync", "reattach-alive.example.com", ["disconnect"], new WorkspaceSyncSpec(WorkspaceSyncMode.Mutagen, syncDir, "/home/agent/work", null, null));
            await ReserveAsync(syncPlan);
            var initialSyncPane=StartPane(syncPlan.Id, false, true);
            await WaitAsync(syncPlan.Id, "Disconnected");
            // 等 pane 完全退出，确认 finally 同样没有终止同步或伪造结束证据。
            await initialSyncPane.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert(initialSyncPane.ExitCode==255,"initial sync pane did not exit with SSH disconnect");
            var oldSession = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(syncPlan.Id));
            Assert(oldSession is {SchemaVersion:1}, "initial sync session evidence missing");
            Assert(!File.Exists(Store.SyncFinalPath(syncPlan.Id)), "disconnected SSH falsely wrote final synchronization evidence");
            Assert(Store.Read<Receipt>(Store.ReceiptPath(syncPlan.Id)) is {Phase:"Disconnected",ExitCode:255}, "initial sync disconnect receipt missing");
            var syncOk = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: syncPlan.Id, Reattach: new OperatorReattach(true)));
            Assert(syncOk.Ok, "sync reattach was refused: " + syncOk.Message);
            var syncReattachPane=StartPane(syncPlan.Id, true, true);
            var synced = await WaitAsync(syncPlan.Id, "Started");
            var newSession = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(syncPlan.Id));
            Assert(synced.Detail.Contains("Mutagen 同步") && synced.Detail.Contains("未恢复原 Agent") && newSession is not null && newSession.Name == oldSession!.Name && !File.Exists(Store.SyncFinalPath(syncPlan.Id)), "saved sync identity was not reused, or shell receipt pretended to restore the Agent terminal");
            Assert(!File.Exists(Store.OperatorReleasePath(syncPlan.Id)) && !Store.IsTerminal(synced), "sync reattach settled the attempt");
            var syncEnded = await WaitAsync(syncPlan.Id, "Disconnected");
            await syncReattachPane.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12));
            Assert(!Store.IsTerminal(syncEnded) && !File.Exists(Store.SyncFinalPath(syncPlan.Id)) && !File.Exists(Store.OperatorReleasePath(syncPlan.Id)), "sync attachment exit pretended the session had converged or released the slot");
            var syncReleased = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: syncPlan.Id, Release: new OperatorRelease("Gone", "fake-ssh 已退出", true)));
            Assert(syncReleased.Ok, "sync reattach fixture was not released: " + syncReleased.Message);

            var gone = Make("reattach-gone", "reattach-gone.example.com", ["disconnect"]);
            await ReserveAsync(gone);
            StartPane(gone.Id, false, false);
            await WaitAsync(gone.Id, "Disconnected");
            var goneReply = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: gone.Id, Reattach: new OperatorReattach(true)));
            Assert(!goneReply.Ok && goneReply.Message.Contains("已不在"), "gone process was reattached: " + goneReply.Message);
            Assert((await EntryAsync(gone.Id))!.State == "Disconnected" && !File.Exists(Store.OperatorReattachPath(gone.Id)) && !File.Exists(Store.OperatorReleasePath(gone.Id)), "gone refusal changed the ledger or auto-released");
            var goneReleased = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: gone.Id, Release: new OperatorRelease("Gone", "fake-ssh 已退出", true)));
            Assert(goneReleased.Ok, "gone reattach fixture was not released: " + goneReleased.Message);

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
            var unsafeReleased = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: unsafePlan.Id, Release: new OperatorRelease("Gone", "fake-ssh 未启动", true)));
            Assert(unsafeReleased.Ok, "unsafe reattach fixture was not released: " + unsafeReleased.Message);

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
            Assert(ResourcePolicy.MaxConcurrentAgents == 6, "cap constant changed");
            var seventh = Make("reattach-cap-seventh", "cap.example.com", []);
            var denied = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [seventh], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([seventh]), DraftRevision: 3));
            Assert(!denied.Ok && denied.Message.Contains("最多 6"), "seventh pane was admitted while disconnected/reattached entries were live: " + denied.Message);
            var recount = await Ipc.SendAsync(new("reattach-remote", Guid.NewGuid(), Attempt: plan.Id, Reattach: new OperatorReattach(true)));
            Assert(recount.Ok && recount.Entries![0].State == "Reattaching" && !Store.IsTerminal(recount.Entries[0]), "second confirm reattach settled the entry: " + recount.Message);
            var after = Make("reattach-cap-after", "cap.example.com", []);
            var deniedAgain = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [after], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([after]), DraftRevision: 3));
            Assert(!deniedAgain.Ok && deniedAgain.Message.Contains("最多 6"), "reattach freed a pane slot: " + deniedAgain.Message);
            Assert(Store.Read<Ledger>(Store.FilePath("control.json"))!.Entries.Count(e => e.Plan.Id == plan.Id) == 1, "cap reattach duplicated the entry");
        });
    }
}
