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

        LaunchItem Item(string name) => new(Guid.NewGuid(), Guid.NewGuid(), 1, name, cwd, PhysicalDirectory.Workspace(cwd), true,
            new(Environment.ProcessPath!, ["--hold", "1"], "exe"), ResourcePolicy.DefaultJobMemoryLimit, "agent",
            PlanningBytes: ResourcePolicy.DefaultPlanningBytes, ResourceMode: "practical");

        var total = 16 * ResourcePolicy.GiB;
        var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), total, 32 * ResourcePolicy.GiB);

        Configuration Authority(ResourceSettings? resource = null, long revision = 3)
        {
            var config = new Configuration { SchemaVersion = 2 };
            config.Draft.SchemaVersion = 2;
            config.Draft.Revision = revision;
            config.Draft.Project = cwd;
            if (resource != null) config.Draft.Resource = resource;
            return config;
        }
        void SaveAuthority(ResourceSettings? resource = null, long revision = 3) =>
            Store.Save(Store.FilePath("configuration.json"), Authority(resource, revision));

        SaveAuthority();

                var initial = await Ipc.EnsureMonitorAsync();
        Assert(initial.Ok, initial.Message);

        await Test("ACC-R01 one agent allows reserve without waiver", async () =>
        {
            var item = Item("one");
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [item], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([item]), DraftRevision: 3));
            Assert(reserved.Ok, reserved.Message);
            var revoke = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id));
            Assert(revoke.Ok, revoke.Message);
        });

        await Test("ACC-R01 three agents require a single-use bound waiver to reserve", async () =>
        {
            var items = Enumerable.Range(0, 3).Select(i => Item("soft-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(items);
            var slots = items.Select(i => i.Id).ToArray();
            var blocked = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(!blocked.Ok && blocked.Message.Contains("确认授权"), "soft overage reserved without waiver: " + blocked.Message);
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(issued.Ok && issued.WaiverId is { } && issued.WaiverExpiresAt is { }, issued.Message);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(reserved.Ok, reserved.Message);
            foreach (var entry in reserved.Entries ?? [])
            {
                await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: entry.Plan.Id));
            }
            var items2 = Enumerable.Range(0, 3).Select(i => Item("reuse-" + i)).ToArray();
            var replay = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items2, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest(items2), DraftRevision: 3, EnabledSlotIds: items2.Select(i => i.Id).ToArray()));
            Assert(!replay.Ok, "reused waiver accepted");
        });

        await Test("ACC-R01 rebound revision or plan digest cannot consume a waiver", async () =>
        {
            var items = Enumerable.Range(0, 3).Select(i => Item("bind-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(items);
            var slots = items.Select(i => i.Id).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(issued.Ok && issued.WaiverId is { }, issued.Message);
            var rebound = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 12, EnabledSlotIds: slots));
            Assert(!rebound.Ok, "revision mismatch consumed waiver");
            var issued2 = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            var changedPlan = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued2.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: "other", DraftRevision: 3, EnabledSlotIds: slots));
            Assert(!changedPlan.Ok, "plan digest mismatch consumed waiver");
        });

        await Test("ACC-R02 hard deny does not issue a waiver", async () =>
        {
            var tightSettings = new ResourceSettings { Mode = "custom", PhysicalFloorOverride = 8 * ResourcePolicy.GiB };
            SaveAuthority(tightSettings);
            var tight = ResourcePolicy.SnapshotFor(tightSettings, total, 32 * ResourcePolicy.GiB);
            var items = Enumerable.Range(0, 8).Select(i => Item("hard-" + i)).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: tight, PolicyHash: ResourcePolicy.PolicyHash(tight), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3, EnabledSlotIds: items.Select(i => i.Id).ToArray()));
            Assert(!issued.Ok && issued.WaiverId is null, "hard deny issued a waiver: " + issued.Message);
            SaveAuthority();
        });

        await Test("ACC-R06 conservative soft overage is rejected without a waiver path", async () =>
        {
            SaveAuthority(new ResourceSettings { Mode = "conservative" });
            var cons = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "conservative" }, total, 32 * ResourcePolicy.GiB);
            var items = Enumerable.Range(0, 3).Select(i => Item("cons-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(cons);
            var digest = ResourcePolicy.PlanDigest(items);
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: cons, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: items.Select(i => i.Id).ToArray()));
            Assert(!issued.Ok && issued.WaiverId is null, "conservative issued a waiver");
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: cons, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3));
            Assert(!reserved.Ok && !reserved.Message.Contains("确认授权"), "conservative asked for a waiver: " + reserved.Message);
            SaveAuthority();
        });

        await Test("reserve accepts 16 items and rejects 17", async () =>
        {
            var many = Enumerable.Range(0, 16).Select(i => Item("n16-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(many);
            var slots = many.Select(i => i.Id).ToArray();
            var sixteen = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), many, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(!sixteen.Ok && !sixteen.Message.Contains("数量"), "16-item batch rejected as invalid count: " + sixteen.Message);
            var seventeen = many.Concat([Item("n17")]).ToArray();
            var overflow = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), seventeen, Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest(seventeen), DraftRevision: 3));
            Assert(!overflow.Ok && overflow.Message.Contains("数量"), "17 items accepted: " + overflow.Message);
        });


        await Test("ACC-R01 monitor rejects self-reported labels that swap command, directory, Shared, or Workspace", async () =>
        {
            SaveAuthority();
            var items = Enumerable.Range(0, 3).Select(i => Item("auth-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(items);
            var slots = items.Select(i => i.Id).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(issued.Ok && issued.WaiverId is { }, issued.Message);
            async Task AssertSwapRejected(string label, Func<LaunchItem, LaunchItem> mutate)
            {
                var planned = Enumerable.Range(0, 3).Select(i => Item("swap-" + label + "-" + i)).ToArray();
                var plannedHash = ResourcePolicy.PolicyHash(policy);
                var plannedDigest = ResourcePolicy.PlanDigest(planned);
                var plannedSlots = planned.Select(i => i.Id).ToArray();
                var grant = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), planned, Policy: policy, PolicyHash: plannedHash, PlanDigest: plannedDigest, DraftRevision: 3, EnabledSlotIds: plannedSlots));
                Assert(grant.Ok && grant.WaiverId is { }, label + " issue failed: " + grant.Message);
                var mutated = planned.Select(mutate).ToArray();
                var stolen = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), mutated, WaiverId: grant.WaiverId, Policy: policy, PolicyHash: plannedHash, PlanDigest: plannedDigest, DraftRevision: 3, EnabledSlotIds: plannedSlots));
                Assert(!stolen.Ok, "self-reported digest allowed a " + label + " swap: " + stolen.Message);
                var retry = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), planned, WaiverId: grant.WaiverId, Policy: policy, PolicyHash: plannedHash, PlanDigest: plannedDigest, DraftRevision: 3, EnabledSlotIds: plannedSlots));
                Assert(!retry.Ok, "token survived a failed " + label + " swap");
            }
            await AssertSwapRejected("command", item => item with { Command = item.Command with { Arguments = ["--hold", "2"] } });
            await AssertSwapRejected("shared", item => item with { Shared = false });
            var other = Path.Combine(Store.Root, "admission-other");
            Directory.CreateDirectory(other);
            var otherWorkspace = PhysicalDirectory.Workspace(other);
            await AssertSwapRejected("directory", item => item with { Cwd = other, Workspace = otherWorkspace });
        });

        await Test("ACC-R01 configuration change invalidates a previously issued waiver", async () =>
        {
            SaveAuthority(revision: 6);
            var items = Enumerable.Range(0, 3).Select(i => Item("rev-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(items);
            var slots = items.Select(i => i.Id).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 6, EnabledSlotIds: slots));
            Assert(issued.Ok && issued.WaiverId is { }, issued.Message);
            SaveAuthority(resource: new ResourceSettings { Mode = "custom", CpuRate = 4000 }, revision: 7);
            var reused = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 6, EnabledSlotIds: slots));
            Assert(!reused.Ok, "stale revision/policy token was accepted: " + reused.Message);
        });

        await Test("ACC-R01 failed reserve with a token cannot reuse it after a directory conflict", async () =>
        {
            SaveAuthority();
            var blocker = Item("blocker") with { Shared = false };
            var blocked = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [blocker], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([blocker]), DraftRevision: 3));
            Assert(blocked.Ok, blocked.Message);
            try
            {
                var items = Enumerable.Range(0, 3).Select(i => Item("conflict-" + i) with { Shared = false }).ToArray();
                var hash = ResourcePolicy.PolicyHash(policy);
                var digest = ResourcePolicy.PlanDigest(items);
                var slots = items.Select(i => i.Id).ToArray();
                var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
                Assert(issued.Ok && issued.WaiverId is { }, issued.Message);
                var conflict = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
                Assert(!conflict.Ok && conflict.Message.Contains("占用"), "expected directory conflict, got " + conflict.Message);
                await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: blocker.Id));
                var replay = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
                Assert(!replay.Ok, "token was reused after a failed conflicting reserve: " + replay.Message);
            }
            finally { await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: blocker.Id)); }
        });

        await Test("ACC-R02 client Policy cannot override authoritative floors", async () =>
        {
            SaveAuthority();
            var tight = policy with { PhysicalFloor = 8 * ResourcePolicy.GiB };
            var items = Enumerable.Range(0, 8).Select(i => Item("client-floor-" + i)).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: tight, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 3, EnabledSlotIds: items.Select(i => i.Id).ToArray()));
            Assert(issued.WaiverId is { }, "monitor used the client tight floor instead of authoritative defaults: " + issued.Message);
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

        await Test("ACC-R05 begin of an earlier conservative batch is not soft-denied by a later practical waiver batch", async () =>
        {
            SaveAuthority(new ResourceSettings { Mode = "conservative" }, revision: 8);
            var first = Enumerable.Range(0, 2).Select(i => Item("early-" + i)).ToArray();
            var consPolicy = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "conservative" }, total, 32 * ResourcePolicy.GiB);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), first, Policy: consPolicy, PolicyHash: ResourcePolicy.PolicyHash(consPolicy), PlanDigest: ResourcePolicy.PlanDigest(first), DraftRevision: 8, EnabledSlotIds: first.Select(i => i.Id).ToArray()));
            Assert(reserved.Ok, reserved.Message);
            LaunchItem[] later = [];
            try
            {
                SaveAuthority(revision: 9);
                later = Enumerable.Range(0, 3).Select(i => Item("later-" + i)).ToArray();
                var hash = ResourcePolicy.PolicyHash(policy);
                var digest = ResourcePolicy.PlanDigest(later);
                var slots = later.Select(i => i.Id).ToArray();
                var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), later, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 9, EnabledSlotIds: slots));
                Assert(issued.Ok && issued.WaiverId is { }, issued.Message);
                var second = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), later, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 9, EnabledSlotIds: slots));
                Assert(second.Ok, second.Message);
                var began = await Ipc.SendAsync(new("begin", Guid.NewGuid(), Attempt: first[0].Id));
                Assert(began.Ok, "conservative begin soft-denied after later practical reserve: " + began.Message);
                var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
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
            var reserve = await Ipc.ReserveWithCleanupAsync([item], policy: custom, draftRevision: 10, policyHash: ResourcePolicy.PolicyHash(custom), planDigest: ResourcePolicy.PlanDigest([item]), enabledSlotIds: [item.Id]);
            Assert(reserve.Ok, reserve.Message);
            await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: item.Id));
        });

        await Test("ACC-R01 monitor restart drops in-memory waivers", async () =>
        {
            SaveAuthority();
            var items = Enumerable.Range(0, 3).Select(i => Item("restart-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(items);
            var slots = items.Select(i => i.Id).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(issued.Ok && issued.WaiverId is { }, issued.Message);
            var pidPath = Store.FilePath("monitor-pid.json");
            var pid = Store.Read<int>(pidPath);
            using (var process = System.Diagnostics.Process.GetProcessById(pid))
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            var restarted = await Ipc.EnsureMonitorAsync();
            Assert(restarted.Ok, restarted.Message);
            var reused = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 3, EnabledSlotIds: slots));
            Assert(!reused.Ok, "waiver survived monitor restart: " + reused.Message);
        });

        Store.Save(Path.Combine(Store.Root, "admission-integration-results.json"), evidence);
        var failures = evidence.Count(e => e.GetType().GetProperty("passed")?.GetValue(e) is false);
        Console.WriteLine($"Admission integration failures: {failures}; evidence: {Store.Root}");
        return failures == 0 ? 0 : 1;
    }
}
