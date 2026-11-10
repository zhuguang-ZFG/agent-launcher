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
        var initial = await Ipc.EnsureMonitorAsync();
        Assert(initial.Ok, initial.Message);

        await Test("ACC-R01 one agent allows reserve without waiver", async () =>
        {
            var item = Item("one");
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [item], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([item]), DraftRevision: 1));
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
            var blocked = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 7, EnabledSlotIds: slots));
            Assert(!blocked.Ok && blocked.Message.Contains("确认授权"), "soft overage reserved without waiver: " + blocked.Message);
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 7, EnabledSlotIds: slots));
            Assert(issued.Ok && issued.WaiverId is { } && issued.WaiverExpiresAt is { }, issued.Message);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 7, EnabledSlotIds: slots));
            Assert(reserved.Ok, reserved.Message);
            foreach (var entry in reserved.Entries ?? [])
            {
                await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: entry.Plan.Id));
            }
            var items2 = Enumerable.Range(0, 3).Select(i => Item("reuse-" + i)).ToArray();
            var replay = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items2, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: ResourcePolicy.PlanDigest(items2), DraftRevision: 7, EnabledSlotIds: items2.Select(i => i.Id).ToArray()));
            Assert(!replay.Ok, "reused waiver accepted");
        });

        await Test("ACC-R01 rebound revision or plan digest cannot consume a waiver", async () =>
        {
            var items = Enumerable.Range(0, 3).Select(i => Item("bind-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(policy);
            var digest = ResourcePolicy.PlanDigest(items);
            var slots = items.Select(i => i.Id).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 11, EnabledSlotIds: slots));
            Assert(issued.Ok && issued.WaiverId is { }, issued.Message);
            var rebound = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 12, EnabledSlotIds: slots));
            Assert(!rebound.Ok, "revision mismatch consumed waiver");
            var issued2 = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: policy, PolicyHash: hash, PlanDigest: digest, DraftRevision: 11, EnabledSlotIds: slots));
            var changedPlan = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, WaiverId: issued2.WaiverId, Policy: policy, PolicyHash: hash, PlanDigest: "other", DraftRevision: 11, EnabledSlotIds: slots));
            Assert(!changedPlan.Ok, "plan digest mismatch consumed waiver");
        });

        await Test("ACC-R02 hard deny does not issue a waiver", async () =>
        {
            var tight = policy with { PhysicalFloor = 8 * ResourcePolicy.GiB };
            var items = Enumerable.Range(0, 8).Select(i => Item("hard-" + i)).ToArray();
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: tight, PolicyHash: ResourcePolicy.PolicyHash(tight), PlanDigest: ResourcePolicy.PlanDigest(items), DraftRevision: 1, EnabledSlotIds: items.Select(i => i.Id).ToArray()));
            Assert(!issued.Ok && issued.WaiverId is null, "hard deny issued a waiver: " + issued.Message);
        });

        await Test("ACC-R06 conservative soft overage is rejected without a waiver path", async () =>
        {
            var cons = policy with { Mode = "conservative" };
            var items = Enumerable.Range(0, 3).Select(i => Item("cons-" + i)).ToArray();
            var hash = ResourcePolicy.PolicyHash(cons);
            var digest = ResourcePolicy.PlanDigest(items);
            var issued = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items, Policy: cons, PolicyHash: hash, PlanDigest: digest, DraftRevision: 1, EnabledSlotIds: items.Select(i => i.Id).ToArray()));
            Assert(!issued.Ok && issued.WaiverId is null, "conservative issued a waiver");
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), items, Policy: cons, PolicyHash: hash, PlanDigest: digest, DraftRevision: 1));
            Assert(!reserved.Ok && !reserved.Message.Contains("确认授权"), "conservative asked for a waiver: " + reserved.Message);
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

        Store.Save(Path.Combine(Store.Root, "admission-integration-results.json"), evidence);
        var failures = evidence.Count(e => e.GetType().GetProperty("passed")?.GetValue(e) is false);
        Console.WriteLine($"Admission integration failures: {failures}; evidence: {Store.Root}");
        return failures == 0 ? 0 : 1;
    }
}
