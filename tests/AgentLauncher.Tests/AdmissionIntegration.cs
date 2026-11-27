using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class AdmissionIntegration
{
    public static async Task<int> RunAsync()
    {
        if (!(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "").Contains("AgentLauncher-tests-"))
            throw new InvalidOperationException("Admission integration requires an isolated test root.");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") != "1")
            throw new InvalidOperationException("Admission integration requires AGENT_LAUNCHER_FAKE_SAMPLE=1.");
        Directory.CreateDirectory(Store.Root);
        var evidence = new List<object>();
        var cwd = Path.Combine(Store.Root, "admission-cwd");
        Directory.CreateDirectory(cwd);
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); evidence.Add(new { name, passed = true }); }
            catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); evidence.Add(new { name, passed = false, error = e.ToString() }); }
        }

        var authoritySlots=Enumerable.Range(0,ResourcePolicy.MaxSlots).Select(index=>new Slot{Enabled=index<3,AgentId=index%2==0?"omp":"codex"}).ToArray();
        LaunchItem Item(string name,int position=1) => new(Guid.NewGuid(), Guid.NewGuid(), position, name, cwd, PhysicalDirectory.Workspace(cwd), true,
            new(Environment.ProcessPath!, ["--hold", "1"], "exe"), ResourcePolicy.DefaultJobMemoryLimit, "agent",
            PlanningBytes: ResourcePolicy.DefaultPlanningBytes, ResourceMode: "practical",SourceSlotId:authoritySlots[position-1].Id,AgentId:authoritySlots[position-1].AgentId);
        LaunchItem GitItem(string name,int position=1) => Item(name,position) with { Kind="git", Budget=ResourcePolicy.GitJobMemoryLimit, PlanningBytes=null };

        var total = 16 * ResourcePolicy.GiB;
        var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), total, 32 * ResourcePolicy.GiB);

        Configuration Authority(ResourceSettings? resource = null, long revision = 3,int enabledCount=3)
        {
            var config = new Configuration { SchemaVersion = 2 };
            config.Draft.SchemaVersion = 2;
            config.Draft.Revision = revision;
            config.Draft.Project = cwd;
            config.Draft.DirectoryMode="shared";
            config.Draft.Slots=authoritySlots.Select((slot,index)=>{var copy=slot.Copy();copy.Enabled=index<enabledCount;return copy;}).ToList();
            if (resource != null) config.Draft.Resource = resource;
            return config;
        }
        void SaveAuthority(ResourceSettings? resource = null, long revision = 3,int enabledCount=3) =>
            Store.Save(Store.FilePath("configuration.json"), Authority(resource, revision,enabledCount));

        SaveAuthority();

        var initial = await Ipc.EnsureMonitorAsync();
        Assert(initial.Ok, initial.Message);

        await Test("ACC-R01 one agent allows reserve", async () =>
        {
            var item = Item("one");
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [item], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([item]), DraftRevision: 3));
            Assert(reserved.Ok, reserved.Message);
            var revoke = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id));
            Assert(revoke.Ok, revoke.Message);
        });

        await Test("ACC-R01 practical soft overage reserves with advisory instead of blocking", async () =>
        {
            var items = Enumerable.Range(0, 3).Select(i => Item("soft-" + i,i+1)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(items);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3));
            Assert(reserved.Ok, "practical soft overage was blocked: " + reserved.Message);
            foreach (var entry in reserved.Entries ?? [])
            {
                await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: entry.Plan.Id));
            }
        });

        await Test("ACC-R02 conservative physical floor denies reserve", async () =>
        {
            var tightSettings = new ResourceSettings { Mode = "conservative", PhysicalFloorOverride = 8 * ResourcePolicy.GiB };
            SaveAuthority(tightSettings,enabledCount:8);
            var tight = ResourcePolicy.SnapshotFor(tightSettings, total, 32 * ResourcePolicy.GiB);
            var items = Enumerable.Range(0, 8).Select(i => Item("hard-" + i,i+1)).ToArray();
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: tight, PolicyHash: ResourcePolicy.PolicyHash(tight), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3));
            Assert(!reserved.Ok && reserved.Message.Contains("可用内存"), "conservative floor was not enforced: " + reserved.Message);
            SaveAuthority();
        });

        await Test("ACC-R06 conservative soft overage is rejected", async () =>
        {
            SaveAuthority(new ResourceSettings { Mode = "conservative" });
            var cons = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "conservative" }, total, 32 * ResourcePolicy.GiB);
            var items = Enumerable.Range(0, 3).Select(i => Item("cons-" + i,i+1)).ToArray();
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: cons, PolicyHash: ResourcePolicy.PolicyHash(cons), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3));
            Assert(!reserved.Ok && reserved.Message.Contains("总预算") && !reserved.Message.Contains("实用模式不拦截"), "conservative soft overage was allowed: " + reserved.Message);
            SaveAuthority();
        });

        await Test("R21 concurrency cap allows 6 agents, rejects the 7th, and does not count git", async () =>
        {
            Assert(ResourcePolicy.MaxConcurrentAgents==6,"running pane cap changed");
            SaveAuthority(enabledCount:16);
            var six = Enumerable.Range(0, ResourcePolicy.MaxConcurrentAgents).Select(i => Item("cap-" + i,i+1)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(six);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), six, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3));
            Assert(reserved.Ok, "6-agent batch was rejected: " + reserved.Message);
            var replay = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), six, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3));
            Assert(replay.Ok && replay.Message == "请求已记录", "exact replay was not idempotent: " + replay.Message);
            var seventh = Item("cap-7",ResourcePolicy.MaxConcurrentAgents+1);
            var overflow = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [seventh], Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest([seventh]), DraftRevision: 3));
            Assert(!overflow.Ok && overflow.Message.Contains($"同时运行的 Agent 窗格最多 {ResourcePolicy.MaxConcurrentAgents} 个"), "7th agent was accepted: " + overflow.Message);
            var git = GitItem("cap-git",ResourcePolicy.MaxSlots);
            var gitReserve = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [git], Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest([git]), DraftRevision: 3));
            Assert(gitReserve.Ok, "git preparation was blocked by the agent concurrency cap: " + gitReserve.Message);
            foreach (var item in six.Concat([git]))
            {
                await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id));
            }
            // 17 items exceed MaxSlots and must be rejected before any launch; slot position stays at the default so
            // constructing the batch itself cannot index past the authority slot list (this assert never ran before).
            var seventeen = Enumerable.Range(0, 17).Select(i => Item("n17-" + i)).ToArray();
            var overflowCount = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), seventeen, Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest(seventeen), DraftRevision: 3));
            Assert(!overflowCount.Ok && overflowCount.Message.Contains("数量"), "17 items accepted: " + overflowCount.Message);
            SaveAuthority();
        });

        await Test("ACC-R01 monitor rejects self-reported labels that swap command, directory, Shared, or Workspace", async () =>
        {
            SaveAuthority();
            async Task AssertSwapRejected(string label, Func<LaunchItem, LaunchItem> mutate)
            {
                var planned = Enumerable.Range(0, 3).Select(i => Item("swap-" + label + "-" + i,i+1)).ToArray();
                var hash = ResourcePolicy.PolicyHash(policy);
                var digest = ResourcePolicy.PlanDigest(planned);
                var mutated = planned.Select(mutate).ToArray();
                var stolen = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), mutated, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3));
                Assert(!stolen.Ok && stolen.Message.Contains("计划摘要"), "self-reported digest allowed a " + label + " swap: " + stolen.Message);
            }
            await AssertSwapRejected("command", item => item with { Command = item.Command with { Arguments = ["--hold", "2"] } });
            await AssertSwapRejected("shared", item => item with { Shared = false });
            var other = Path.Combine(Store.Root, "admission-other");
            Directory.CreateDirectory(other);
            var otherWorkspace = PhysicalDirectory.Workspace(other);
            await AssertSwapRejected("directory", item => item with { Cwd = other, Workspace = otherWorkspace });
        });

        await Test("ACC-R01 stale draft revision is rejected", async () =>
        {
            SaveAuthority(revision: 7);
            var items = Enumerable.Range(0, 3).Select(i => Item("rev-" + i,i+1)).ToArray();
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 6));
            Assert(!reserved.Ok && reserved.Message.Contains("草稿修订号"), "stale revision was accepted: " + reserved.Message);
            SaveAuthority();
        });

        await Test("ACC-R01 failed reserve can retry after the directory conflict is revoked", async () =>
        {
            SaveAuthority();
            var blocker = Item("blocker") with { Shared = false };
            var blocked = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [blocker], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([blocker]), DraftRevision: 3));
            Assert(blocked.Ok, blocked.Message);
            try
            {
                // The conflict batch shares its directory internally; only the non-shared blocker collides with it.
                // Marking the batch itself non-shared made it self-conflict on retry, so the retry assert never ran.
                var items = Enumerable.Range(0, 3).Select(i => Item("conflict-" + i,i+1)).ToArray();
                var conflict = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3));
                Assert(!conflict.Ok && conflict.Message.Contains("占用"), "expected directory conflict, got " + conflict.Message);
                await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: blocker.Id));
                var retry = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3));
                Assert(retry.Ok, "retry after revoke failed: " + retry.Message);
                foreach (var item in items) { try { await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id)); } catch { } }
            }
            finally { await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: blocker.Id)); }
        });

        await Test("ACC-R02 client Policy cannot override authoritative floors", async () =>
        {
            SaveAuthority(new ResourceSettings { Mode = "conservative" });
            var cons = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "conservative" }, total, 32 * ResourcePolicy.GiB);
            var items = Enumerable.Range(0, 3).Select(i => Item("client-floor-" + i,i+1)).ToArray();
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(cons), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3));
            Assert(!reserved.Ok && !reserved.Message.Contains("实用模式不拦截"), "monitor used the client practical policy instead of authoritative conservative: " + reserved.Message);
            SaveAuthority();
        });

        await Test("ACC-C02 explicit illegal planning is rejected and does not enter the ledger", async () =>
        {
            SaveAuthority();
            var item = Item("tiny-plan") with { PlanningBytes = 1 };
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [item], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([item]), DraftRevision: 3));
            Assert(!reserved.Ok, "planning=1 was reserved: " + reserved.Message);
            var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
            Assert(status.Entries is null || status.Entries.All(e => e.Plan.Id != item.Id), "illegal item entered the ledger");
        });

        await Test("ACC-R05 begin of an earlier conservative batch is not soft-denied by a later practical advisory batch", async () =>
        {
            SaveAuthority(new ResourceSettings { Mode = "conservative" }, revision: 8);
            var first = Enumerable.Range(0, 2).Select(i => Item("early-" + i,i+1)).ToArray();
            var consPolicy = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "conservative" }, total, 32 * ResourcePolicy.GiB);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), first, Policy: consPolicy, PolicyHash: ResourcePolicy.PolicyHash(consPolicy), PlanDigest: ResourcePolicy.PlanDigest(first), DraftRevision: 8));
            Assert(reserved.Ok, reserved.Message);
            LaunchItem[] later = [];
            try
            {
                SaveAuthority(revision: 9);
                later = Enumerable.Range(0, 3).Select(i => Item("later-" + i,i+1)).ToArray();
                var second = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), later, Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest(later), DraftRevision: 9));
                Assert(second.Ok, "practical advisory batch was blocked: " + second.Message);
                var began = await Ipc.SendAsync(new("begin", Guid.NewGuid(), Attempt: first[0].Id));
                Assert(began.Ok, "conservative begin soft-denied after later practical reserve: " + began.Message);
            }
            finally
            {
                foreach (var item in first.Concat(later ?? [])) { try { await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id)); } catch { } }
            }
        });

        await Test("Ipc.ReserveWithCleanupAsync carries verified policy context", async () =>
        {
            SaveAuthority(new ResourceSettings { Mode = "custom", PhysicalFloorOverride = ResourcePolicy.MinCustomPhysicalFloor }, revision: 10);
            var item = Item("cleanup-context");
            var custom = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "custom", PhysicalFloorOverride = ResourcePolicy.MinCustomPhysicalFloor }, total, 32 * ResourcePolicy.GiB);
            var reserve = await Ipc.ReserveWithCleanupAsync([item], policy: custom, draftRevision: 10, policyHash: ResourcePolicy.PolicyHash(custom), planDigest: ResourcePolicy.PlanDigest([item]), enabledSlotIds: authoritySlots.Take(3).Select(slot=>slot.Id).ToArray());
            Assert(reserve.Ok, reserve.Message);
            await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id));
        });

        await Test("monitor restart preserves the persisted ledger", async () =>
        {
            SaveAuthority();
            var items = Enumerable.Range(0, 3).Select(i => Item("restart-" + i,i+1)).ToArray();
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3));
            Assert(reserved.Ok, reserved.Message);
            var pidPath = Store.FilePath("monitor-pid.json");
            var pid = Store.Read<int>(pidPath);
            using (var process = System.Diagnostics.Process.GetProcessById(pid))
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            var restarted = await Ipc.EnsureMonitorAsync();
            Assert(restarted.Ok, restarted.Message);
            var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
            var survived = status.Entries ?? [];
            Assert(items.All(i => survived.Any(e => e.Plan.Id == i.Id)), "ledger entries were lost across monitor restart");
            foreach (var item in items) { try { await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id)); } catch { } }
        });

        // The fixed fake sample (12 GiB available, 24 GiB commit available, 5% CPU) cannot be pushed below any
        // authority-valid floor: overrides are rejected above min(total/2, 8 GiB), so an out-of-process monitor can never
        // latch pressure in this harness. Pressure is therefore latched on a directly constructed monitor, exactly as CR-N01
        // does, and the IPC monitor is only used to prove the two new status fields cross the wire.
        await Test("ACC-R07 latched pressure gates reservations by the entry's frozen mode, not the global mode", async () =>
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var monitorType = System.Reflection.Assembly.LoadFrom(Store.WorkerPath.Replace(".exe", ".dll", StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor", true)!;
            object? Invoke(object monitor, string name, params object?[] args)
            {
                try { return (monitorType.GetMethod(name, flags) ?? throw new MissingMethodException(name)).Invoke(monitor, args); }
                catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    throw;
                }
            }
            void Set(object monitor, string field, object? value) => monitorType.GetField(field, flags)!.SetValue(monitor, value);
            T Get<T>(object monitor, string field) => (T)monitorType.GetField(field, flags)!.GetValue(monitor)!;
            var sample = new MemorySnapshot(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
            var taskPath = Store.FilePath("tasks.json");
            Response? granted = null;
            string? practicalRefusal = null;
            string? conservativeRefusal = null;
            SaveAuthority(new ResourceSettings { Mode = "conservative" });
            try
            {
                var command = new CommandSpec(Environment.ProcessPath!, ["--hold", "1"], "exe");
                var profile = new AgentProfile { Executable = command.Executable, Arguments = command.Arguments };
                var book = new TaskBook();
                var practicalTask = TaskBookOps.Create(book, "frozen practical", cwd, "codex", profile, "", [], "shared", new ResourceSettings { Mode = "practical" }, 0);
                var practicalQueue = TaskBookOps.Enqueue(book, practicalTask.Id, command, AgentProbe.Fingerprint(command));
                var conservativeTask = TaskBookOps.Create(book, "frozen conservative", cwd, "codex", profile, "", [], "shared", new ResourceSettings { Mode = "conservative" }, 0);
                var conservativeQueue = TaskBookOps.Enqueue(book, conservativeTask.Id, command, AgentProbe.Fingerprint(command));
                Store.Save(taskPath, book);
                var fixture = Activator.CreateInstance(monitorType, true)!;
                try
                {
                    Set(fixture, "ledger", new Ledger());
                    Set(fixture, "ledgerPath", Store.FilePath("admission-pressure-" + Guid.NewGuid().ToString("N") + ".json"));
                    Set(fixture, "configurationPath", Store.FilePath("configuration.json"));
                    Set(fixture, "sample", sample);
                    Set(fixture, "rootCpuRate", ResourcePolicy.DefaultCpuRate);
                    var tracker = Get<PressureTracker>(fixture, "pressureTracker");
                    void Latch()
                    {
                        // Three high readings cross the tracker's hysteresis; a later healthy sample only starts the five-sample
                        // recovery streak, so the latch survives every admission probe in this test.
                        for (var i = 0; i < 3; i++) tracker.Observe(sample with { CpuPercent = 95 }, ResourcePolicy.DefaultPhysicalFloor(sample.Total));
                        Assert(tracker.Pressure, "pressure fixture did not latch");
                    }
                    Latch();
                    var status = (Response)Invoke(fixture, "Handle", new Request("status", Guid.NewGuid()))!;
                    Assert(status.AdmissionPressure, "status did not expose the raw pressure latch");
                    Assert(status.AdmissionBlockedReason?.Contains("资源压力尚未连续恢复，稍后重新检查。") == true, "global conservative mode did not report the pressure blocker: " + (status.AdmissionBlockedReason ?? "<null>"));
                    Assert(status.AdmissionHardBlockedReason == null, "pressure was reported as a hard admission reason: " + (status.AdmissionHardBlockedReason ?? "<null>"));
                    var viaIpc = await Ipc.EnsureMonitorAsync();
                    Assert(viaIpc.Ok && !viaIpc.AdmissionPressure && viaIpc.AdmissionHardBlockedReason == null, "idle monitor reported an unexpected pressure or hard blocker: " + viaIpc.Message);
                    var practicalPlan = Item("frozen-practical") with { SourceSlotId = practicalTask.Id, AgentId = practicalTask.AgentId, Command = command, ResourceMode = "practical" };
                    var practicalPolicy = ResourcePolicy.SnapshotFor(practicalQueue.Resource, sample.Total, sample.CommitLimit);
                    Latch();
                    try
                    {
                        granted = (Response)Invoke(fixture, "Handle", new Request("reserve-task", Guid.NewGuid(), [practicalPlan], Policy: practicalPolicy, PolicyHash: ResourcePolicy.PolicyHash(practicalPolicy), PlanDigest: ResourcePolicy.PlanDigest([practicalPlan]), TaskEntryId: practicalQueue.Id))!;
                    }
                    catch (IOException e) { practicalRefusal = e.Message; }
                    Assert(practicalRefusal is null && granted?.Ok == true, "frozen practical queue task was hard-blocked by latched pressure: " + (practicalRefusal ?? granted?.Message));
                    var conservativePlan = Item("frozen-conservative") with { SourceSlotId = conservativeTask.Id, AgentId = conservativeTask.AgentId, Command = command, ResourceMode = "conservative" };
                    var conservativePolicy = ResourcePolicy.SnapshotFor(conservativeQueue.Resource, sample.Total, sample.CommitLimit);
                    Latch();
                    try
                    {
                        _ = Invoke(fixture, "Handle", new Request("reserve-task", Guid.NewGuid(), [conservativePlan], Policy: conservativePolicy, PolicyHash: ResourcePolicy.PolicyHash(conservativePolicy), PlanDigest: ResourcePolicy.PlanDigest([conservativePlan]), TaskEntryId: conservativeQueue.Id));
                    }
                    catch (IOException e) { conservativeRefusal = e.Message; }
                    Assert(conservativeRefusal == "资源压力尚未连续恢复，稍后重新检查。", "frozen conservative queue task was not refused by latched pressure: " + (conservativeRefusal ?? "<accepted>"));
                }
                finally { ((IDisposable)fixture).Dispose(); }
            }
            finally
            {
                Store.Save(taskPath, new TaskBook());
                SaveAuthority();
            }
        });

        // W2: the idle working-set trim is a monitor behavior — only latched physical pressure unlocks it, only CPU-silent
        // Started panes are paged out, the reclaim is reported on the entry, and a busy pane in the same set is spared.
        await Test("W2 pressure trims only idle Started panes and reports the reclaim", async () =>
        {
            var type = System.Reflection.Assembly.LoadFrom(Store.WorkerPath.Replace(".exe", ".dll", StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor", true)!;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            System.Reflection.FieldInfo Field(string name) => type.GetField(name, flags) ?? throw new MissingFieldException(type.FullName, name);
            var refresh = type.GetMethod("Refresh", flags) ?? throw new MissingMethodException(type.FullName, "Refresh");
            var sample = new MemorySnapshot(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
            var isolatedRoot = Store.RootJob + "-trim-" + Guid.NewGuid().ToString("N");
            using var rootJob = new Job(isolatedRoot, root: true);
            var idleItem = Item("trim-idle");
            var busyItem = Item("trim-busy");
            var idleOk = Path.Combine(Store.Root, "trim-idle-" + Guid.NewGuid().ToString("N") + ".ok");
            using var idleJob = new Job(Store.JobName(idleItem.Id), ResourcePolicy.AgentBudget);
            using var busyJob = new Job(Store.JobName(busyItem.Id), ResourcePolicy.AgentBudget);
            using var idleHolder = new ProtectedProcess(new(Environment.ProcessPath!, ["--allocate-mib", "384", "30000", idleOk], "exe"), cwd, rootJob, idleJob);
            using var busyHolder = new ProtectedProcess(new(Environment.ProcessPath!, ["--spin", "30000"], "exe"), cwd, rootJob, busyJob);
            try
            {
                idleHolder.Resume();
                busyHolder.Resume();
                for (var i = 0; i < 100 && !File.Exists(idleOk); i++) await Task.Delay(100);
                Assert(File.Exists(idleOk), "idle fixture did not allocate its pages");
                var idleEntry = new ControlEntry { Plan = idleItem, Digest = Store.Digest(idleItem), State = "Started", Detail = "已启动进程（不代表任务完成）" };
                var busyEntry = new ControlEntry { Plan = busyItem, Digest = Store.Digest(busyItem), State = "Started", Detail = "已启动进程（不代表任务完成）" };
                Store.Save(Store.ReceiptPath(idleItem.Id), new Receipt("Started", idleHolder.Process.Id, idleHolder.CreationTime, null, "已启动进程（不代表任务完成）", DateTimeOffset.UtcNow));
                Store.Save(Store.ReceiptPath(busyItem.Id), new Receipt("Started", busyHolder.Process.Id, busyHolder.CreationTime, null, "已启动进程（不代表任务完成）", DateTimeOffset.UtcNow));
                using var monitor = (IDisposable)Activator.CreateInstance(type, true)!;
                Field("sample").SetValue(monitor, sample);
                Field("ledger").SetValue(monitor, new Ledger { Entries = [idleEntry, busyEntry] });
                Field("ledgerPath").SetValue(monitor, Path.Combine(Store.Root, "trim-" + Guid.NewGuid().ToString("N") + ".json"));
                Field("root").SetValue(monitor, new Job(isolatedRoot, root: true));
                var elapsed = TimeSpan.Zero;
                Field("trimClockOverride").SetValue(monitor, (Func<TimeSpan>)(() => elapsed));
                Field("trimPhysicalFloor").SetValue(monitor, ResourcePolicy.DefaultPhysicalFloor(sample.Total));
                var jobsDict = (Dictionary<Guid, Job>)Field("jobs").GetValue(monitor)!;
                jobsDict[idleItem.Id] = idleJob;
                jobsDict[busyItem.Id] = busyJob;
                var tracker = (PressureTracker)Field("pressureTracker").GetValue(monitor)!;
                void RefreshNow(MemorySnapshot? reading = null)
                {
                    // A fresh injected sample keeps Refresh from resampling the real machine, so the manually
                    // latched tracker is the only pressure signal in this fixture.
                    Field("sample").SetValue(monitor, (reading ?? sample) with { At = DateTimeOffset.UtcNow });
                    refresh.Invoke(monitor, [false]);
                }
                var idleBefore = JobUsageReader.Read(idleJob).PrivateWorkingSetBytes ?? 0;
                Assert(idleBefore >= 320UL * 1024 * 1024, "idle fixture working set too small to prove trim: " + idleBefore);
                RefreshNow();
                await Task.Delay(350);
                RefreshNow();
                var idleAfterCalm = JobUsageReader.Read(idleJob).PrivateWorkingSetBytes ?? 0;
                Assert(idleAfterCalm + 64UL * 1024 * 1024 > idleBefore, $"off-pressure trim must not run: before={idleBefore} after={idleAfterCalm}");
                Assert(!idleEntry.Detail.Contains("换出"), "off-pressure detail reported a trim: " + idleEntry.Detail);
                for (var i = 0; i < 3; i++) tracker.Observe(sample with { CpuPercent = 95 }, ResourcePolicy.DefaultPhysicalFloor(sample.Total));
                Assert(tracker.Pressure, "pressure fixture did not latch");
                elapsed = TimeSpan.FromSeconds(31);
                RefreshNow(sample with { CpuPercent = 95 });
                Assert(!idleEntry.Detail.Contains("工作集减少"), "CPU-only pressure triggered paging");
                RefreshNow(sample with { CommitAvailable = ResourcePolicy.MiB });
                Assert(!idleEntry.Detail.Contains("工作集减少"), "commit-only pressure triggered paging");
                var busyBefore = JobUsageReader.Read(busyJob).PrivateWorkingSetBytes ?? 0;
                await Task.Delay(350);
                var lowMemory = sample with { Available = ResourcePolicy.GiB };
                RefreshNow(lowMemory);
                var idleAfter = JobUsageReader.Read(idleJob).PrivateWorkingSetBytes ?? 0;
                var busyAfter = JobUsageReader.Read(busyJob).PrivateWorkingSetBytes ?? 0;
                Assert(idleAfter + 64UL * 1024 * 1024 < idleBefore, $"pressured trim reclaimed too little: before={idleBefore} after={idleAfter}");
                Assert(busyAfter + 8UL * 1024 * 1024 > busyBefore, $"busy pane was trimmed: before={busyBefore} after={busyAfter}");
                Assert(idleEntry.Detail.Contains("工作集减少") && idleEntry.Detail.Contains("MiB"), "trim reclaim was not reported on the entry: " + idleEntry.Detail);
                Assert(!busyEntry.Detail.Contains("工作集减少"), "busy pane reported a trim: " + busyEntry.Detail);
                Assert(!idleHolder.Process.HasExited, "trim killed the idle pane process");
                var nextTrimAt = Field("nextTrimAt").GetValue(monitor);
                elapsed = TimeSpan.FromSeconds(40);
                RefreshNow(lowMemory);
                Assert(Equals(nextTrimAt, Field("nextTrimAt").GetValue(monitor)), "pane was trimmed again during cooldown");
                Assert(idleEntry.State == "Started" && busyEntry.State == "Started", "trim changed launch authority");
                Console.WriteLine($"W2 private working set MiB: idle={idleBefore/1048576.0:0.0}->{idleAfter/1048576.0:0.0}, busy={busyBefore/1048576.0:0.0}->{busyAfter/1048576.0:0.0}");
            }
            finally
            {
                try { idleHolder.Process.Kill(); } catch { }
                try { busyHolder.Process.Kill(); } catch { }
            }
        });

        await Test("R21 legacy ledger with seven pending agents is refused at begin and six still begins", async () =>
        {
            SaveAuthority();
            var controlPath = Store.FilePath("control.json");
            Ledger LedgerFor(int count)
            {
                var ledger = new Ledger { BootId = Store.BootId() };
                for (var index = 0; index < count; index++)
                {
                    var plan = Item("legacy-" + index, index + 1);
                    ledger.Entries.Add(new ControlEntry { Plan = plan, Digest = Store.Digest(plan), State = "Pending" });
                }
                return ledger;
            }
            void StopMonitor()
            {
                var pid = Store.Read<int>(Store.FilePath("monitor-pid.json"));
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                process.Kill();
                Assert(process.WaitForExit(5000), "monitor did not exit before the crafted ledger was published");
            }
            async Task StartWith(Ledger ledger)
            {
                // Ledgers written before the six-pane cap cannot be produced through reserve, so the crafted book is published
                // while no monitor holds it in memory; the order (stop, publish, start) is what makes the load authoritative.
                StopMonitor();
                Store.Save(controlPath, ledger);
                var started = await Ipc.EnsureMonitorAsync();
                Assert(started.Ok, "monitor did not load the crafted ledger: " + started.Message);
            }
            await StartWith(LedgerFor(ResourcePolicy.MaxConcurrentAgents+1));
            var seven = Store.Read<Ledger>(controlPath)!;
            var refused = await Ipc.SendAsync(new("begin", Guid.NewGuid(), Attempt: seven.Entries[0].Plan.Id));
            Assert(!refused.Ok && refused.Message.Contains($"同时运行的 Agent 窗格最多 {ResourcePolicy.MaxConcurrentAgents} 个"), "seven pending agents were not refused at begin: " + refused.Message);
            var retained = Store.Read<Ledger>(controlPath)!;
            Assert(retained.Entries.Count == ResourcePolicy.MaxConcurrentAgents+1 && retained.Entries.All(entry => entry.State == "Pending"), "refused begin changed or consumed the legacy ledger");
            await StartWith(LedgerFor(ResourcePolicy.MaxConcurrentAgents));
            var six = Store.Read<Ledger>(controlPath)!;
            var began = await Ipc.SendAsync(new("begin", Guid.NewGuid(), Attempt: six.Entries[0].Plan.Id));
            Assert(began.Ok, "six pending agents were refused at begin: " + began.Message);
            var cleanup = Store.Read<Ledger>(controlPath)!;
            // The begun entry is Creating: only Pending entries can be revoked, while an unstarted Creating grant settles as
            // FailedBeforeStart on the monitor's next refresh (no receipt, no job member). Wait for that tick before reading.
            foreach (var entry in cleanup.Entries.Where(entry => entry.State == "Pending"))
            {
                var revoked = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: entry.Plan.Id));
                Assert(revoked.Ok, "cleanup revoke failed: " + revoked.Message);
            }
            var allTerminal = false;
            var openStates = "";
            for (var attempt = 0; attempt < 50 && !allTerminal; attempt++)
            {
                var probe = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                var open = probe.Entries?.Where(entry => !Store.IsTerminal(entry)).ToArray() ?? [];
                openStates = string.Join(",", open.Select(entry => entry.State));
                allTerminal = open.Length == 0;
                if (!allTerminal) await Task.Delay(100);
            }
            Assert(allTerminal, "legacy fixture entries were not revoked or settled after cleanup: " + openStates);
        });

        Store.Save(Path.Combine(Store.Root, "admission-integration-results.json"), evidence);
        var failures = evidence.Count(e => e.GetType().GetProperty("passed")?.GetValue(e) is false);
        Console.WriteLine($"Admission integration failures: {failures}; evidence: {Store.Root}");
        return failures == 0 ? 0 : 1;
    }
}
