#if INTEGRATION_TESTS
using System.Diagnostics;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

// 同步可靠性传播层（R35/R36/R37/R38/R42）：FakeSyncSimulator 用三个本地目录模拟本机/京云/阿里云，
// 不打开真实 VPS。120 秒观察窗只覆盖 ≤4KiB、不在忽略规则内的夹具文件（规格 §8）。
internal static class SyncReliabilityIntegration
{
    public static async Task<int> RunAsync()
    {
        var testRoot = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "";
        if (!testRoot.Contains("AgentLauncher-tests-") || Directory.Exists(testRoot))
            throw new IOException("请使用全新隔离的 AgentLauncher-tests- 测试目录（AGENT_LAUNCHER_TEST_ROOT）。");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") != "1")
            throw new IOException("同步可靠性集成需要 AGENT_LAUNCHER_FAKE_SAMPLE=1（Worker 驱动用例）。");
        Directory.CreateDirectory(testRoot);
        var evidence = new List<object>();
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); evidence.Add(new { name, passed = true }); }
            catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); evidence.Add(new { name, passed = false, error = e.ToString() }); }
        }

        var hub = Path.Combine(testRoot, "hub");
        var jy = Path.Combine(testRoot, "jy-fixture");
        var aly = Path.Combine(testRoot, "aly-fixture");
        foreach (var dir in new[] { hub, jy, aly }) Directory.CreateDirectory(dir);
        var scenarioPath = Path.Combine(testRoot, "fake-sync-scenario.json");
        var callLog = Path.Combine(testRoot, "fake-sync-calls.jsonl");
        var jyName = "agent-launcher-fake-jy-" + Guid.NewGuid().ToString("N");
        var alyName = "agent-launcher-fake-aly-" + Guid.NewGuid().ToString("N");

        void WriteScenario(bool jyOnline, bool alyOnline, bool flushFails = false, bool jyPaused = false, string? jyHalted = null, string? jyAlpha = null) =>
            File.WriteAllText(scenarioPath, JsonSerializer.Serialize(new FakeSyncScenario
            {
                PropagationDelayMs = 0,
                FlushFails = flushFails,
                CallLog = callLog,
                Links =
                [
                    new FakeSyncLink { SessionName = jyName, LinkLabel = "本机—京云", AlphaPath = jyAlpha ?? hub, BetaPath = jy, Online = jyOnline, Paused = jyPaused, Halted = jyHalted },
                    new FakeSyncLink { SessionName = alyName, LinkLabel = "本机—阿里云", AlphaPath = hub, BetaPath = aly, Online = alyOnline },
                ],
            }));
        string[] Calls() => File.Exists(callLog)
            ? File.ReadAllLines(callLog).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("op").GetString()!).ToArray()
            : [];
        string[] CallsFor(string session) => File.Exists(callLog)
            ? File.ReadAllLines(callLog).Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("session").GetString() == session)
                .Select(e => e.GetProperty("op").GetString()!).ToArray()
            : [];

        RemoteExecutionSpec Remote(string host, string local, string remoteDir, WorkspaceSyncSpec sync, string[]? arguments = null) =>
            new(host, 22, "root", "agent", "pi", remoteDir, arguments ?? [], null, null, sync);
        WorkspaceSyncSpec Spec(string name, string local, string remoteDir) =>
            new(WorkspaceSyncMode.Mutagen, local, remoteDir, name, null);

        // 120 秒观察窗（规格 §8）：轮询直到条件成立或超时；超时即失败，不放宽。
        async Task Within120Seconds(Func<bool> condition, string why)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(120))
            {
                FakeSyncSimulator.Tick();
                if (condition()) return;
                await Task.Delay(100);
            }
            throw new Exception("120 秒观察窗超时：" + why);
        }
        byte[] Fixture(string text) => System.Text.Encoding.UTF8.GetBytes(text); // ≤4KiB
        bool SameBytes(string path, byte[] expected) => File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(expected);

        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC", "1");
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC_SCENARIO", scenarioPath);
        WriteScenario(true, true);
        FakeSyncSimulator.Reset();

        await Test("R35 three-way small file propagation within 120 seconds", async () =>
        {
            // 不用 await using：会话必须保留到 R42 的显式 flush 用例。提前 Dispose 会 flush+terminate，
            // 既让后续用例失去注册会话，也污染「离线/恢复不 terminate」的调用日志断言。
            var jySession = await WorkspaceSync.StartAsync(Spec(jyName, hub, jy), Remote("jy.example.com", hub, jy, Spec(jyName, hub, jy)));
            var alySession = await WorkspaceSync.StartAsync(Spec(alyName, hub, aly), Remote("aly.example.com", hub, aly, Spec(alyName, hub, aly)));
            await jySession.WaitReadyAsync();
            await alySession.WaitReadyAsync();
            Assert(jySession.SessionName == jyName && alySession.SessionName == alyName, "会话名与场景不一致");

            var fromHub = Fixture("hub writes R35\n");
            var fromJy = Fixture("jingyun writes R35\n");
            var fromAly = Fixture("aliyun writes R35\n");
            await File.WriteAllBytesAsync(Path.Combine(hub, "hub-r35.txt"), fromHub);
            await File.WriteAllBytesAsync(Path.Combine(jy, "jy-r35.txt"), fromJy);
            await File.WriteAllBytesAsync(Path.Combine(aly, "aly-r35.txt"), fromAly);
            await Within120Seconds(() =>
                SameBytes(Path.Combine(jy, "hub-r35.txt"), fromHub) && SameBytes(Path.Combine(aly, "hub-r35.txt"), fromHub) &&
                SameBytes(Path.Combine(hub, "jy-r35.txt"), fromJy) && SameBytes(Path.Combine(aly, "jy-r35.txt"), fromJy) &&
                SameBytes(Path.Combine(hub, "aly-r35.txt"), fromAly) && SameBytes(Path.Combine(jy, "aly-r35.txt"), fromAly),
                "三端未读到相同字节");
            // 两条链路此时都可判已收敛，整体才是全部已收敛（R34 的传播层佐证）。
            var now = DateTimeOffset.UtcNow;
            var blocks = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList());
            var states = blocks.Select(b => SyncReliability.ParseSession(b)).Where(p => p != null)
                .Select(p => SyncReliability.EvaluateLink(p!, now, now).State).ToArray();
            Assert(states.Length == 2 && states.All(s => s == SyncLinkState.Converged), "传播完成后链路不是已收敛：" + string.Join(",", states));
            Assert(SyncReliability.OverallLabel(states) == "全部已收敛", "双健康链路整体标签错误");
            // 忽略规则内的文件不传播（忽略列表不扩大的反向证明）。
            Directory.CreateDirectory(Path.Combine(hub, "node_modules"));
            await File.WriteAllBytesAsync(Path.Combine(hub, "node_modules", "ignored.txt"), Fixture("ignored"));
            FakeSyncSimulator.Tick(); await Task.Delay(300); FakeSyncSimulator.Tick();
            Assert(!File.Exists(Path.Combine(jy, "node_modules", "ignored.txt")), "忽略规则内文件被传播");
        });

        await Test("R36 both sides write same path different bytes keeps conflict and bytes", async () =>
        {
            // 先建立共同祖先，再双方改同一路径。
            var baseline = Fixture("base R36\n");
            await File.WriteAllBytesAsync(Path.Combine(hub, "conflict-r36.txt"), baseline);
            await Within120Seconds(() => SameBytes(Path.Combine(jy, "conflict-r36.txt"), baseline) && SameBytes(Path.Combine(aly, "conflict-r36.txt"), baseline), "祖先文件未传播");
            var jyBytes = Fixture("jingyun version R36\n");
            var alyBytes = Fixture("aliyun version R36\n");
            // 京云链路上双方分歧：先冻结两条链路（离线开关即冻结传播），hub 与 jy 同时改同一路径，再恢复。
            WriteScenario(false, false);
            await File.WriteAllBytesAsync(Path.Combine(hub, "conflict-r36.txt"), Fixture("hub version R36\n"));
            await File.WriteAllBytesAsync(Path.Combine(jy, "conflict-r36.txt"), jyBytes);
            WriteScenario(true, true);
            await Within120Seconds(() => FakeSyncSimulator.Conflicts(jyName).Contains("conflict-r36.txt"), "双方修改未产生冲突");
            Assert(SameBytes(Path.Combine(jy, "conflict-r36.txt"), jyBytes), "冲突后京云字节被覆盖");
            Assert(SameBytes(Path.Combine(hub, "conflict-r36.txt"), Fixture("hub version R36\n")), "冲突后本机字节被覆盖");
            var now = DateTimeOffset.UtcNow;
            var parsed = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList())
                .Select(SyncReliability.ParseSession).First(p => p?.Name == jyName)!;
            var obs = SyncReliability.EvaluateLink(parsed, now, now);
            Assert(obs.State == SyncLinkState.Conflict, "冲突链路状态错误：" + obs.State);
            var list = SyncReliability.BuildConflictList([(parsed, "本机—京云", "本机", "京云")]);
            Assert(list.Rows.Any(r => r.RelativePath == "conflict-r36.txt" && r.LinkLabel == "本机—京云"), "清单缺少相对路径与受影响会话");
            Assert(SyncReliability.OverallLabel([obs.State, SyncLinkState.Converged]) == "冲突", "整体不是冲突");
            // 消解方式（规格 §5/§9）：删掉应失败的一端后，另一端覆盖。本轮不实现按钮，用文件操作验证语义。
            File.Delete(Path.Combine(jy, "conflict-r36.txt"));
            await Within120Seconds(() => SameBytes(Path.Combine(jy, "conflict-r36.txt"), Fixture("hub version R36\n")) && !FakeSyncSimulator.Conflicts(jyName).Contains("conflict-r36.txt"), "删除一端后冲突未消解");
            // 阿里云链路同路径分歧同样成立（独立会话独立冲突）：同样冻结后让 hub 与 aly 都相对基线改过。
            WriteScenario(false, false);
            await File.WriteAllBytesAsync(Path.Combine(hub, "conflict-r36.txt"), Fixture("hub version 2 R36\n"));
            await File.WriteAllBytesAsync(Path.Combine(aly, "conflict-r36.txt"), alyBytes);
            WriteScenario(true, true);
            await Within120Seconds(() => FakeSyncSimulator.Conflicts(alyName).Contains("conflict-r36.txt"), "阿里云链路双方修改未产生冲突");
            Assert(SameBytes(Path.Combine(aly, "conflict-r36.txt"), alyBytes), "阿里云冲突字节被覆盖");
            File.Delete(Path.Combine(aly, "conflict-r36.txt"));
            await Within120Seconds(() => !FakeSyncSimulator.Conflicts(alyName).Contains("conflict-r36.txt"), "阿里云冲突未消解");
        });

        await Test("R37 hub offline one-sided writes wait and recover on the same session", async () =>
        {
            WriteScenario(false, false); // 枢纽离线：两条链路都断开
            var offlineFile = Fixture("jingyun offline write R37\n");
            await File.WriteAllBytesAsync(Path.Combine(jy, "offline-r37.txt"), offlineFile);
            FakeSyncSimulator.Tick(); await Task.Delay(300); FakeSyncSimulator.Tick();
            Assert(!File.Exists(Path.Combine(aly, "offline-r37.txt")) && !File.Exists(Path.Combine(hub, "offline-r37.txt")), "离线期间文件被传播");
            var now = DateTimeOffset.UtcNow;
            var offlineStates = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList())
                .Select(SyncReliability.ParseSession).Where(p => p != null)
                .Select(p => SyncReliability.EvaluateLink(p!, now, now).State).ToArray();
            Assert(offlineStates.All(s => s == SyncLinkState.Offline), "离线链路状态错误：" + string.Join(",", offlineStates));
            WriteScenario(true, true); // 恢复：不 terminate 不 create
            await Within120Seconds(() => SameBytes(Path.Combine(hub, "offline-r37.txt"), offlineFile) && SameBytes(Path.Combine(aly, "offline-r37.txt"), offlineFile), "恢复后单侧写入未传到另外两端");
            Assert(CallsFor(jyName).Count(op => op == "create") == 1 && !CallsFor(jyName).Contains("terminate"), "恢复重建了会话");
            Assert(FakeSyncSimulator.IsRegistered(jyName) && FakeSyncSimulator.IsRegistered(alyName), "恢复后会话不在");
            // 第二组：离线期间两侧云改同一路径 → 恢复后报告冲突，不自动合并。
            // 偏差（规格 R37「同一路径两侧都改过则报告冲突」）：每条链路是独立会话、各有自己的基线，
            // 先传到枢纽的一侧收敛，另一侧看到「枢纽已变 + 本侧已变」而报告冲突——冲突落在受影响的那条链路，
            // 不要求两条链路同时报告同一路径（计划原 && 与独立会话语义及计划自身的确定性说明矛盾）。
            WriteScenario(false, false);
            var jyConflict = Fixture("jy offline conflict R37\n");
            var alyConflict = Fixture("aly offline conflict R37\n");
            await File.WriteAllBytesAsync(Path.Combine(jy, "offline-conflict-r37.txt"), jyConflict);
            await File.WriteAllBytesAsync(Path.Combine(aly, "offline-conflict-r37.txt"), alyConflict);
            WriteScenario(true, true);
            await Within120Seconds(() => FakeSyncSimulator.Conflicts(jyName).Contains("offline-conflict-r37.txt") || FakeSyncSimulator.Conflicts(alyName).Contains("offline-conflict-r37.txt"), "离线双方修改恢复后未报告冲突");
            Assert(SameBytes(Path.Combine(jy, "offline-conflict-r37.txt"), jyConflict) && SameBytes(Path.Combine(aly, "offline-conflict-r37.txt"), alyConflict), "离线冲突字节被自动合并覆盖");
            var conflictNow = DateTimeOffset.UtcNow;
            var conflictStates = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList())
                .Select(SyncReliability.ParseSession).Where(p => p != null)
                .Select(p => SyncReliability.EvaluateLink(p!, conflictNow, conflictNow).State).ToArray();
            Assert(SyncReliability.OverallLabel(conflictStates) == "冲突", "离线冲突恢复后整体不是冲突：" + string.Join(",", conflictStates));
            File.Delete(Path.Combine(jy, "offline-conflict-r37.txt"));
            File.Delete(Path.Combine(aly, "offline-conflict-r37.txt"));
            await Within120Seconds(() => !FakeSyncSimulator.Conflicts(jyName).Contains("offline-conflict-r37.txt") && !FakeSyncSimulator.Conflicts(alyName).Contains("offline-conflict-r37.txt"), "清理后冲突仍在");
        });

        await Test("R38 one link offline other healthy converges alone", async () =>
        {
            WriteScenario(false, true);
            var healthyFile = Fixture("aliyun healthy R38\n");
            await File.WriteAllBytesAsync(Path.Combine(aly, "healthy-r38.txt"), healthyFile);
            await Within120Seconds(() => SameBytes(Path.Combine(hub, "healthy-r38.txt"), healthyFile), "健康链路未传完自己的小型文件");
            Assert(!File.Exists(Path.Combine(jy, "healthy-r38.txt")), "离线链路收到了文件");
            var now = DateTimeOffset.UtcNow;
            var parsed = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList()).Select(SyncReliability.ParseSession).Where(p => p != null).ToArray();
            var jyObs = SyncReliability.EvaluateLink(parsed.First(p => p!.Name == jyName)!, now, now);
            var alyObs = SyncReliability.EvaluateLink(parsed.First(p => p!.Name == alyName)!, now, now);
            Assert(jyObs.State == SyncLinkState.Offline && alyObs.State == SyncLinkState.Converged, "一离线一健康状态错误");
            Assert(SyncReliability.OverallLabel([jyObs.State, alyObs.State]) != "全部已收敛", "单链路健康被写成全部已收敛");
            Assert(SyncReliability.OverallLabel([alyObs.State]) == "单链路已收敛", "单链路已收敛标签错误");
            WriteScenario(true, true);
            await Within120Seconds(() => SameBytes(Path.Combine(jy, "healthy-r38.txt"), healthyFile), "恢复后健康文件未到京云");
        });

        await Test("R42 offline pause and probe failure never terminate create or reset", async () =>
        {
            await Task.CompletedTask; // 本用例全是同步模拟器开关与断言；保持 Func<Task> 签名（CS1998）。
            var before = Calls();
            // 普通离线：切换开关 + 多轮 list，不 terminate 不 create。
            WriteScenario(false, false);
            FakeSyncSimulator.Tick();
            _ = FakeSyncSimulator.RenderList();
            // 暂停（场景开关模拟操作员 pause 后的状态；本轮产品无 pause 按钮）：同样不 terminate 不 create。
            WriteScenario(true, true, jyPaused: true);
            _ = FakeSyncSimulator.RenderList();
            // 安全暂停（halted）：保持 halted，不自动 resume/reset。
            WriteScenario(true, true, jyHalted: "root deletion");
            _ = FakeSyncSimulator.RenderList();
            // 探针失败：纯函数层评估为未知；产品没有任何因探针失败而写会话的路径。
            var failed = SyncReliability.FailedProbe("mutagen sync list 超时（10 秒），已终止。", DateTimeOffset.UtcNow, SyncLinkState.Converged, DateTimeOffset.UtcNow);
            Assert(failed.State == SyncLinkState.Unknown, "探针失败不是未知");
            WriteScenario(true, true);
            var after = Calls();
            Assert(!after.Contains("terminate") && !after.Contains("reset"), "离线/暂停/探针失败触发了 terminate/reset");
            Assert(after.Count(op => op == "create") == before.Count(op => op == "create"), "离线/暂停/探针失败重建了会话");
            Assert(CallsFor(jyName).SequenceEqual(new[] { "create" }) || CallsFor(jyName).All(op => op is "create" or "list"), "京云会话出现意外写操作：" + string.Join(",", CallsFor(jyName)));
            Assert(FakeSyncSimulator.IsRegistered(jyName) && FakeSyncSimulator.IsRegistered(alyName), "会话被悄悄移除");
            // halted 状态解析为安全暂停（R40 的传播层佐证）。
            WriteScenario(true, true, jyHalted: "root deletion");
            var now = DateTimeOffset.UtcNow;
            var halted = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList()).Select(SyncReliability.ParseSession).First(p => p?.Name == jyName)!;
            Assert(SyncReliability.EvaluateLink(halted, now, now).State == SyncLinkState.SafetyPaused, "模拟器 halted 不是安全暂停");
            WriteScenario(true, true);
        });

        await Test("R42 flush failure keeps session in process", async () =>
        {
            WriteScenario(true, true, flushFails: true);
            var session = await WorkspaceSync.StartAsync(Spec(jyName, hub, jy), Remote("jy.example.com", hub, jy, Spec(jyName, hub, jy)));
            // 同名会话已注册：Register 幂等覆盖状态，但 create 会再记一次——断言按「flush 后无 terminate」判定。
            var error = await session.TryDisposeAsync();
            Assert(error is not null && error.Contains("已保留"), "flush 失败未返回可读保留错误：" + error);
            Assert(FakeSyncSimulator.IsRegistered(jyName), "flush 失败后会话被终止");
            Assert(!CallsFor(jyName).Contains("terminate"), "flush 失败仍 terminate");
            Assert(CallsFor(jyName).Contains("flush"), "flush 未被调用");
            WriteScenario(true, true);
            var ok = await session.TryDisposeAsync();
            Assert(ok is null && !FakeSyncSimulator.IsRegistered(jyName), "flush 成功后未正常终止");
        });

        await Test("R42 worker exit with flush failure writes problem receipt and keeps session evidence", async () =>
        {
            // Worker 驱动：fake-ssh hold=2 正常退出 → Worker flush（场景开关失败）→ sync-final problem + sync-session 保留。
            // 调用日志截断：只断言 Worker 进程自己的操作（前面用例的 create/flush/terminate 不属于本条）。
            if (File.Exists(callLog)) File.Delete(callLog);
            var fakeSshDir = AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(fakeSshDir, "ssh.exe"))) throw new IOException("fake SSH not found: " + fakeSshDir);
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", fakeSshDir + Path.PathSeparator + originalPath);
            var workerProcesses = new List<Process>();
            try
            {
                var cwd = Path.Combine(Store.Root, "r42-cwd"); Directory.CreateDirectory(cwd);
                var authorityConfig = new Configuration { SchemaVersion = 2 };
                authorityConfig.Draft.SchemaVersion = 2; authorityConfig.Draft.Revision = 3;
                authorityConfig.Draft.Project = cwd; authorityConfig.Draft.DirectoryMode = "isolated";
                authorityConfig.Draft.Slots = [new Slot { Enabled = true, AgentId = "pi" }];
                Store.Save(Store.FilePath("configuration.json"), authorityConfig);
                var initial = await Ipc.EnsureMonitorAsync();
                Assert(initial.Ok, "Monitor start failed: " + initial.Message);
                var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "r42-flush", cwd, PhysicalDirectory.Workspace(cwd), false,
                    new CommandSpec("pi", ["hold=2"], "remote"), 0,
                    // 远程工作目录必须是 Unix 路径且与 sync.RemotePath 一致（RemoteExecutionSpec.Validate）；
                    // 模拟器按会话名匹配场景链路，BetaPath 仍是本地夹具 jy，与该名义远程目录解耦。
                    RemoteExecution: Remote("jy.example.com", cwd, "/home/agent/work", Spec(jyName, cwd, "/home/agent/work"), ["hold=2"]));
                var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                Assert(reserved.Ok, "reserve failed: " + reserved.Message);
                WriteScenario(true, true, flushFails: true, jyAlpha: cwd);
                var start = new ProcessStartInfo(Store.WorkerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("pane"); start.ArgumentList.Add(plan.Id.ToString());
                start.Environment["AGENT_LAUNCHER_TEST_ROOT"] = Store.Root;
                start.Environment["PATH"] = fakeSshDir + Path.PathSeparator + originalPath;
                start.Environment["AGENT_LAUNCHER_FAKE_SYNC"] = "1";
                start.Environment["AGENT_LAUNCHER_FAKE_SYNC_SCENARIO"] = scenarioPath;
                var worker = Process.Start(start)!;
                worker.StandardInput.Close();
                workerProcesses.Add(worker);
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(60) && !File.Exists(Store.SyncFinalPath(plan.Id))) await Task.Delay(200);
                Assert(File.Exists(Store.SyncFinalPath(plan.Id)), "Worker 未写 sync-final 回执");
                var final = Store.Read<SyncFinalRecord>(Store.SyncFinalPath(plan.Id))!;
                Assert(final.SchemaVersion == 1 && final.State == SyncFinalRecord.Problem, "flush 失败回执不是 problem：" + final.State);
                Assert(final.Detail.Contains("已保留"), "problem 回执未说明会话保留");
                Assert(File.Exists(Store.SyncSessionPath(plan.Id)), "flush 失败删掉了 sync-session.json");
                var sessionRecord = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(plan.Id))!;
                Assert(sessionRecord.Name == jyName, "会话名证据丢失");
                Assert(!CallsFor(jyName).Contains("terminate"), "Worker flush 失败仍 terminate");
                Assert(!Calls().Contains("reset"), "出现未确认的 reset");
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                foreach (var w in workerProcesses) { try { if (!w.HasExited) w.Kill(true); } catch { } w.Dispose(); }
                Environment.SetEnvironmentVariable("PATH", originalPath);
                WriteScenario(true, true);
            }
        });

        Console.WriteLine($"RESULT {evidence.Count} cases, {evidence.Count(e => e.GetType().GetProperty("passed")?.GetValue(e) is false)} failed. Artifacts: {testRoot}");
        File.WriteAllText(Path.Combine(testRoot, "sync-reliability-results.json"), JsonSerializer.Serialize(evidence));
        return evidence.Any(e => e.GetType().GetProperty("passed")?.GetValue(e) is false) ? 1 : 0;
    }
}
#endif