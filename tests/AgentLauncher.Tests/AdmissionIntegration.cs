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

        var authoritySlots=Enumerable.Range(0,ResourcePolicy.MaxSlots).Select(index=>new Slot{Enabled=index<3,AgentId=index%2==0?"pi":"codex"}).ToArray();
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

        await Test("R21 concurrency cap allows 8 agents, rejects the 9th, and does not count git", async () =>
        {
            SaveAuthority(enabledCount:16);
            var eight = Enumerable.Range(0, 8).Select(i => Item("cap-" + i,i+1)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(eight);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), eight, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3));
            Assert(reserved.Ok, "8-agent batch was rejected: " + reserved.Message);
            var replay = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), eight, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3));
            Assert(replay.Ok && replay.Message == "请求已记录", "exact replay was not idempotent: " + replay.Message);
            var ninth = Item("cap-9",9);
            var overflow = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [ninth], Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest([ninth]), DraftRevision: 3));
            Assert(!overflow.Ok && overflow.Message.Contains($"同时运行的 Agent 窗格最多 {ResourcePolicy.MaxConcurrentAgents} 个"), "9th agent was accepted: " + overflow.Message);
            var git = GitItem("cap-git",10);
            var gitReserve = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [git], Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest([git]), DraftRevision: 3));
            Assert(gitReserve.Ok, "git preparation was blocked by the agent concurrency cap: " + gitReserve.Message);
            foreach (var item in eight.Concat([git]))
            {
                await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id));
            }
            var seventeen = Enumerable.Range(0, 17).Select(i => Item("n17-" + i,i+1)).ToArray();
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
                var items = Enumerable.Range(0, 3).Select(i => Item("conflict-" + i,i+1) with { Shared = false }).ToArray();
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

        Store.Save(Path.Combine(Store.Root, "admission-integration-results.json"), evidence);
        var failures = evidence.Count(e => e.GetType().GetProperty("passed")?.GetValue(e) is false);
        Console.WriteLine($"Admission integration failures: {failures}; evidence: {Store.Root}");
        return failures == 0 ? 0 : 1;
    }
}
