using AgentLauncher.Core;

internal static class LongTaskResourceChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("ACC-R10 Omp low limits suggest a long-task profile only when enabled", () =>
        {
            foreach (var agent in new[] { "omp" })
            {
                var draft = new Draft { Slots = [new() { AgentId = agent, Enabled = true }] };
                Assert(LongTaskResources.Suggested(draft), "missing low-limit advice for " + agent);
                draft.Slots[0].Enabled = false;
                Assert(!LongTaskResources.Suggested(draft), "disabled item triggered advice");
                draft.Slots[0].Enabled = true;
                draft.Resource = LongTaskResources.Create(draft.Resource);
                Assert(!LongTaskResources.Suggested(draft), "advice remained after applying profile");
            }
        });
        test("ACC-R10 long-task profile preserves CPU floors and original frozen settings", () =>
        {
            var original = new ResourceSettings { CpuRate = 2500, PhysicalFloorOverride = 2 * ResourcePolicy.GiB,
                CommitFloorOverride = 3 * ResourcePolicy.GiB, Pool = ResourcePolicy.PoolShared, PoolMemoryLimit = 4 * ResourcePolicy.GiB };
            var next = LongTaskResources.Create(original);
            Assert(next.JobMemoryLimit == 4 * ResourcePolicy.GiB && next.PlanningBytes == 1536 * ResourcePolicy.MiB, "incorrect long-task budget");
            Assert(next.Mode == "custom" && next.Pool == ResourcePolicy.PoolIndependent && next.PoolMemoryLimit is null, "profile still shares the old pool ceiling");
            Assert(next.CpuRate == original.CpuRate && next.PhysicalFloorOverride == original.PhysicalFloorOverride && next.CommitFloorOverride == original.CommitFloorOverride, "profile weakened another protection");
            Assert(original.JobMemoryLimit == 2 * ResourcePolicy.GiB && original.Pool == ResourcePolicy.PoolShared, "original settings were mutated");
            var larger = LongTaskResources.Create(new() { JobMemoryLimit = 8 * ResourcePolicy.GiB, PlanningBytes = 3 * ResourcePolicy.GiB });
            Assert(larger.JobMemoryLimit == 8 * ResourcePolicy.GiB && larger.PlanningBytes == 3 * ResourcePolicy.GiB, "profile lowered existing larger budgets");
            var rejected = false;
            try { ResourcePolicy.ValidateSettings(next, 3 * ResourcePolicy.GiB, 8 * ResourcePolicy.GiB); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "profile bypassed small-machine validation");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
