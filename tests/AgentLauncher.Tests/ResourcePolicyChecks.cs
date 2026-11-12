using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class ResourcePolicyChecks
{
    public static void RunMigration(Action<string, Action> Test, string temp)
    {
        Test("ACC-P02 v1 four-slot config loads with practical defaults and keeps user fields", () =>
        {
            var raw = """
            {"SchemaVersion":1,"Draft":{"SchemaVersion":1,"Revision":3,"Project":"","DirectoryMode":"isolated","Layout":"vertical","Slots":[{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1","Enabled":true,"AgentId":"pi","Directory":"","Note":"","Profiles":{}},{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2","Enabled":false,"AgentId":"codex","Directory":"D:\\proj\\keep","Note":"keep","Profiles":{}},{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3","Enabled":false,"AgentId":"pi","Directory":"","Note":"","Profiles":{}},{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4","Enabled":false,"AgentId":"hermes","Directory":"","Note":"","Profiles":{}}]}}
            """;
            var path = Path.Combine(temp, "legacy-v1.json");
            File.WriteAllText(path, raw);
            var loaded = Store.Read<Configuration>(path)!;
            var accepted = ConfigurationCompatibility.Accept(loaded);
            accepted.Draft.Validate();
            if (accepted.SchemaVersion != ConfigurationCompatibility.CurrentSchemaVersion) throw new Exception("configuration schema not upgraded");
            if (accepted.Draft.SchemaVersion != ConfigurationCompatibility.CurrentSchemaVersion) throw new Exception("draft schema not upgraded");
            if (accepted.Draft.Slots.Count != 4) throw new Exception("slot count changed");
            if (accepted.Draft.Slots[1].Directory != @"D:\proj\keep" || accepted.Draft.Slots[1].Note != "keep") throw new Exception("user fields rewritten");
            if (accepted.Draft.Slots[1].AgentId != "codex" || accepted.Draft.Slots[0].Enabled != true) throw new Exception("slot selection rewritten");
            if (accepted.Draft.Resource.Mode != "practical") throw new Exception("default mode is not practical");
            if (accepted.Draft.Resource.JobMemoryLimit != ResourcePolicy.DefaultJobMemoryLimit) throw new Exception("job cap default lost");
            if (accepted.Draft.Resource.PlanningBytes != ResourcePolicy.DefaultPlanningBytes) throw new Exception("planning default lost");
            if (accepted.Draft.Resource.CpuRate != ResourcePolicy.DefaultCpuRate) throw new Exception("cpu default changed");
            if (ResourcePolicy.DefaultJobMemoryLimit != 2 * ResourcePolicy.GiB) throw new Exception("job default is not 2 GiB");
            if (ResourcePolicy.DefaultCpuRate != 3500) throw new Exception("cpu default is not 35%");
            if (File.ReadAllText(path) != raw) throw new Exception("legacy file overwritten on accept");
        });

        Test("ACC-P01 unknown configuration schema is rejected without overwrite", () =>
        {
            var path = Path.Combine(temp, "future.json");
            var original = """
            {"SchemaVersion":99,"Draft":{"SchemaVersion":99,"Revision":1,"Project":"","DirectoryMode":"isolated","Layout":"vertical","Slots":[{"Id":"00000000-0000-0000-0000-000000000001","Enabled":true,"AgentId":"pi","Directory":"","Profiles":{}}]}}
            """;
            File.WriteAllText(path, original);
            var loaded = Store.Read<Configuration>(path)!;
            var rejected = false;
            try { ConfigurationCompatibility.Accept(loaded); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("future schema accepted");
            if (File.ReadAllText(path) != original) throw new Exception("file overwritten");
        });

        Test("ACC-P01 unknown draft schema is rejected without overwrite", () =>
        {
            var path = Path.Combine(temp, "future-draft.json");
            var original = """
            {"SchemaVersion":1,"Draft":{"SchemaVersion":99,"Revision":1,"Project":"","DirectoryMode":"isolated","Layout":"vertical","Slots":[{"Id":"00000000-0000-0000-0000-000000000001","Enabled":true,"AgentId":"pi","Directory":"","Profiles":{}}]}}
            """;
            File.WriteAllText(path, original);
            var loaded = Store.Read<Configuration>(path)!;
            var rejected = false;
            try { ConfigurationCompatibility.Accept(loaded); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("future draft schema accepted");
            if (File.ReadAllText(path) != original) throw new Exception("file overwritten");
        });

        Test("ACC-L02 validate accepts 16 slots and rejects 17", () =>
        {
            var d = new Draft { SchemaVersion = 2, Slots = Enumerable.Range(0, 16).Select(_ => new Slot()).ToList() };
            d.Slots[0].Enabled = true;
            d.Validate();
            d.Slots.Add(new Slot());
            var rejected = false;
            try { d.Validate(); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("17 slots accepted");
        });

        Test("Draft.Copy preserves schema and resource", () =>
        {
            var d = new Draft { SchemaVersion = 2 };
            d.Resource.Mode = "custom";
            d.Resource.JobMemoryLimit = 3 * ResourcePolicy.GiB;
            d.Resource.PlanningBytes = 512 * ResourcePolicy.MiB;
            d.Resource.CpuRate = 4000;
            var c = d.Copy();
            if (c.SchemaVersion != 2 || c.Resource.Mode != "custom" || c.Resource.JobMemoryLimit != 3 * ResourcePolicy.GiB) throw new Exception("copy dropped fields");
            if (c.Resource.PlanningBytes != 512 * ResourcePolicy.MiB || c.Resource.CpuRate != 4000) throw new Exception("copy dropped planning or cpu");
            c.Resource.Mode = "conservative";
            if (d.Resource.Mode != "custom") throw new Exception("copy aliased resource");
        });

        Test("ACC-C02 custom resource settings reject sub-GiB cap and empty mode", () =>
        {
            var d = new Draft { SchemaVersion = 2, Resource = { Mode = "custom", JobMemoryLimit = ResourcePolicy.DefaultJobMemoryLimit, PlanningBytes = ResourcePolicy.DefaultPlanningBytes, CpuRate = ResourcePolicy.DefaultCpuRate } };
            d.Validate();
            d.Resource.JobMemoryLimit = 512 * ResourcePolicy.MiB;
            var capRejected = false;
            try { d.Validate(); } catch (InvalidDataException) { capRejected = true; }
            if (!capRejected) throw new Exception("sub-GiB cap accepted");
            d.Resource.JobMemoryLimit = ResourcePolicy.DefaultJobMemoryLimit;
            d.Resource.Mode = "unsupported";
            var modeRejected = false;
            try { d.Validate(); } catch (InvalidDataException) { modeRejected = true; }
            if (!modeRejected) throw new Exception("unknown resource mode accepted");
        });
    }
    public static void RunReviewFixes(Action<string, Action> Test, string temp)
    {
        Test("ACC-P01 Accept schema 2 explicit null Resource is rejected without rewrite", () =>
        {
            var path = Path.Combine(temp, "v2-null-resource.json");
            var original = """
            {"SchemaVersion":2,"Draft":{"SchemaVersion":2,"Revision":1,"Project":"","DirectoryMode":"isolated","Layout":"vertical","Resource":null,"Slots":[{"Id":"00000000-0000-0000-0000-000000000001","Enabled":true,"AgentId":"pi","Directory":"","Note":"","Profiles":{}}]}}
            """;
            File.WriteAllText(path, original);
            var loaded = Store.Read<Configuration>(path)!;
            var rejected = false;
            try { ConfigurationCompatibility.Accept(loaded); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("schema 2 null Resource was silently defaulted");
            if (File.ReadAllText(path) != original) throw new Exception("file overwritten");
        });

        Test("ACC-P01 Accept schema 2 explicit empty Mode and zero budgets are rejected without rewrite", () =>
        {
            var path = Path.Combine(temp, "v2-zero-resource.json");
            var original = """
            {"SchemaVersion":2,"Draft":{"SchemaVersion":2,"Revision":1,"Project":"","DirectoryMode":"isolated","Layout":"vertical","Resource":{"Mode":"","JobMemoryLimit":0,"PlanningBytes":0,"CpuRate":0},"Slots":[{"Id":"00000000-0000-0000-0000-000000000001","Enabled":true,"AgentId":"pi","Directory":"","Note":"","Profiles":{}}]}}
            """;
            File.WriteAllText(path, original);
            var loaded = Store.Read<Configuration>(path)!;
            var rejected = false;
            try { ConfigurationCompatibility.Accept(loaded); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("schema 2 explicit empty/zero Resource was silently defaulted");
            if (File.ReadAllText(path) != original) throw new Exception("file overwritten");
        });

        Test("ACC-C02 fixed upper bounds reject 9 GiB / 17 GiB / ulong.MaxValue", () =>
        {
            void Reject(Action<Draft> edit, string label)
            {
                var d = new Draft { SchemaVersion = 2 };
                d.Resource.Mode = "custom";
                d.Resource.JobMemoryLimit = ResourcePolicy.DefaultJobMemoryLimit;
                d.Resource.PlanningBytes = ResourcePolicy.DefaultPlanningBytes;
                d.Resource.CpuRate = ResourcePolicy.DefaultCpuRate;
                edit(d);
                var rejected = false;
                try { d.Validate(); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception(label + " accepted");
            }
            Reject(d => d.Resource.JobMemoryLimit = 9 * ResourcePolicy.GiB, "9 GiB job cap");
            Reject(d => d.Resource.JobMemoryLimit = ulong.MaxValue, "ulong.MaxValue job cap");
            Reject(d => d.Resource.PhysicalFloorOverride = 9 * ResourcePolicy.GiB, "9 GiB physical floor");
            Reject(d => d.Resource.PhysicalFloorOverride = ulong.MaxValue, "ulong.MaxValue physical floor");
            Reject(d => d.Resource.CommitFloorOverride = 17 * ResourcePolicy.GiB, "17 GiB commit floor");
            Reject(d => d.Resource.CommitFloorOverride = ulong.MaxValue, "ulong.MaxValue commit floor");

            var path = Path.Combine(temp, "v2-job-9gib.json");
            var original = "{\"SchemaVersion\":2,\"Draft\":{\"SchemaVersion\":2,\"Revision\":1,\"Project\":\"\",\"DirectoryMode\":\"isolated\",\"Layout\":\"vertical\",\"Resource\":{\"Mode\":\"custom\",\"JobMemoryLimit\":" + (9 * ResourcePolicy.GiB) + ",\"PlanningBytes\":" + ResourcePolicy.DefaultPlanningBytes + ",\"CpuRate\":" + ResourcePolicy.DefaultCpuRate + "},\"Slots\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Enabled\":true,\"AgentId\":\"pi\",\"Directory\":\"\",\"Note\":\"\",\"Profiles\":{}}]}}";
            File.WriteAllText(path, original);
            var loaded = Store.Read<Configuration>(path)!;
            var rejected = false;
            try { ConfigurationCompatibility.Accept(loaded); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("Accept allowed 9 GiB job cap");
            if (File.ReadAllText(path) != original) throw new Exception("file overwritten");

            var legal = new Draft { SchemaVersion = 2 };
            legal.Resource.Mode = "custom";
            legal.Resource.JobMemoryLimit = 8 * ResourcePolicy.GiB;
            legal.Resource.PlanningBytes = ResourcePolicy.DefaultPlanningBytes;
            legal.Resource.CpuRate = ResourcePolicy.DefaultCpuRate;
            legal.Resource.PhysicalFloorOverride = 8 * ResourcePolicy.GiB;
            legal.Resource.CommitFloorOverride = 16 * ResourcePolicy.GiB;
            legal.Validate();
        });

        Test("SoftCap is overflow-safe and never exceeds 8 GiB", () =>
        {
            if (ResourcePolicy.SoftCap(16 * ResourcePolicy.GiB) != 16 * ResourcePolicy.GiB * 35 / 100) throw new Exception("16 GiB soft cap changed");
            var overflowTotal = ulong.MaxValue / 35 + 1;
            var overflowed = ResourcePolicy.SoftCap(overflowTotal);
            if (overflowed != 8 * ResourcePolicy.GiB) throw new Exception("overflow-boundary soft cap: " + overflowed);
            var max = ResourcePolicy.SoftCap(ulong.MaxValue);
            if (max != 8 * ResourcePolicy.GiB) throw new Exception("ulong.MaxValue soft cap overflowed or exceeded 8 GiB: " + max);
            if (ResourcePolicy.SoftCap(0) != 0) throw new Exception("zero total soft cap");
        });

        Test("16-slot mixed draft persists and reloads through Accept", () =>
        {
            var agents = new[] { "pi", "codex", "claude", "gemini", "opencode", "hermes", "omp", "kimi", "qoder", "custom", "pi", "codex", "claude", "gemini", "hermes", "custom" };
            var config = new Configuration { SchemaVersion = 2 };
            config.Draft.SchemaVersion = 2;
            config.Draft.Project = Path.Combine(temp, "project");
            config.Draft.Slots = Enumerable.Range(0, 16).Select(i => new Slot
            {
                Enabled = i % 3 == 0,
                AgentId = agents[i],
                CustomName = agents[i] == "custom" ? "自定义" + i : null,
                Directory = Path.Combine(temp, "slot-" + i),
                Note = "note-" + i,
                Profiles = new() { [agents[i]] = new() { Executable = Path.Combine(temp, agents[i] + i + ".exe"), Arguments = ["--n", i.ToString()] } }
            }).ToList();
            var path = Path.Combine(temp, "sixteen-slots.json");
            Store.Save(path, config);
            var loaded = ConfigurationCompatibility.Accept(Store.Read<Configuration>(path)!);
            if (loaded.Draft.Slots.Count != 16) throw new Exception("slot count lost");
            for (var i = 0; i < 16; i++)
            {
                var a = config.Draft.Slots[i];
                var b = loaded.Draft.Slots[i];
                if (a.Id != b.Id || a.Enabled != b.Enabled || a.AgentId != b.AgentId || a.CustomName != b.CustomName || a.Directory != b.Directory || a.Note != b.Note) throw new Exception("slot " + i + " fields lost");
                if (b.Current.Executable != a.Current.Executable || !b.Current.Arguments.SequenceEqual(a.Current.Arguments)) throw new Exception("slot " + i + " profile lost");
            }
        });

        Test("Accept migrates nested v1 LastEdited and templates without aliasing", () =>
        {
            var path = Path.Combine(temp, "v1-nested.json");
            var original = """
            {"SchemaVersion":1,"Draft":{"SchemaVersion":1,"Revision":4,"Project":"P-draft","DirectoryMode":"isolated","Layout":"vertical","Slots":[{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1","Enabled":true,"AgentId":"pi","Directory":"D:\\draft","Note":"draft","Profiles":{}}]},"LastEdited":{"SchemaVersion":1,"Revision":3,"Project":"P-edit","DirectoryMode":"shared","Layout":"horizontal","Slots":[{"Id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1","Enabled":false,"AgentId":"codex","Directory":"D:\\edit","Note":"edit","Profiles":{"codex":{"Executable":"D:\\codex.exe","Arguments":["--edit"]}}}]},"Templates":{"alpha":{"SchemaVersion":1,"Revision":1,"Project":"","DirectoryMode":"isolated","Layout":"vertical","Slots":[{"Id":"cccccccc-cccc-cccc-cccc-ccccccccccc1","Enabled":true,"AgentId":"hermes","Directory":"D:\\alpha","Note":"alpha","Profiles":{}}]},"beta":{"SchemaVersion":1,"Revision":2,"Project":"","DirectoryMode":"prepared","Layout":"vertical","IsPortableTemplate":true,"Slots":[{"Id":"dddddddd-dddd-dddd-dddd-ddddddddddd1","Enabled":true,"AgentId":"gemini","Directory":"","Note":"beta","RequiresDirectory":true,"Profiles":{}}]}}}
            """;
            File.WriteAllText(path, original);
            var accepted = ConfigurationCompatibility.Accept(Store.Read<Configuration>(path)!);
            if (File.ReadAllText(path) != original) throw new Exception("v1 nested file overwritten");
            if (accepted.SchemaVersion != 2 || accepted.Draft.SchemaVersion != 2 || accepted.LastEdited!.SchemaVersion != 2 || accepted.Templates["alpha"].SchemaVersion != 2 || accepted.Templates["beta"].SchemaVersion != 2) throw new Exception("nested schema not upgraded");
            if (accepted.Draft.Slots[0].Directory != @"D:\draft" || accepted.Draft.Slots[0].Note != "draft") throw new Exception("draft fields lost");
            if (accepted.LastEdited.Slots[0].Directory != @"D:\edit" || accepted.LastEdited.DirectoryMode != "shared" || accepted.LastEdited.Slots[0].Current.Arguments[0] != "--edit") throw new Exception("LastEdited lost");
            if (accepted.Templates["alpha"].Slots[0].Note != "alpha" || accepted.Templates["beta"].Slots[0].Note != "beta" || !accepted.Templates["beta"].IsPortableTemplate || !accepted.Templates["beta"].Slots[0].RequiresDirectory) throw new Exception("templates lost");
            if (accepted.Draft.Resource.Mode != "practical" || accepted.LastEdited.Resource.Mode != "practical" || accepted.Templates["alpha"].Resource.JobMemoryLimit != ResourcePolicy.DefaultJobMemoryLimit) throw new Exception("v1 nested defaults missing");
            accepted.Templates["alpha"].Resource.Mode = "conservative";
            accepted.Templates["alpha"].Slots[0].Note = "changed";
            if (accepted.Draft.Resource.Mode != "practical" || accepted.LastEdited.Resource.Mode != "practical" || accepted.Templates["beta"].Slots[0].Note != "beta") throw new Exception("nested objects were aliased");
        });

        Test("corrupt primary and backup are not silently repaired or overwritten", () =>
        {
            var primary = Path.Combine(temp, "corrupt-primary.json");
            var backup = Path.Combine(temp, "corrupt-primary.json.bak");
            var garbage = "{not-json";
            File.WriteAllText(primary, garbage);
            File.WriteAllText(backup, garbage);
            var primaryRejected = false;
            try { Store.Read<Configuration>(primary); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { primaryRejected = true; }
            var backupRejected = false;
            try { Store.Read<Configuration>(backup); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException) { backupRejected = true; }
            if (!primaryRejected || !backupRejected) throw new Exception("corrupt file accepted");
            if (File.ReadAllText(primary) != garbage || File.ReadAllText(backup) != garbage) throw new Exception("corrupt file overwritten");
        });
    }

    public static void RunDecisions(Action<string, Action> Test)
    {
        var total = 16 * ResourcePolicy.GiB;
        var policy = new ResourcePolicySnapshot("practical", ResourcePolicy.DefaultJobMemoryLimit, ResourcePolicy.DefaultPlanningBytes,
            ResourcePolicy.DefaultPhysicalFloor(total), ResourcePolicy.DefaultCommitFloor(32 * ResourcePolicy.GiB),
            ResourcePolicy.DefaultCpuRate, ResourcePolicy.GitJobMemoryLimit, ResourcePolicy.DefaultGitPlanningBytes);
        BudgetItem Agent(int i, ulong? plan = null, ulong? cap = null) =>
            new(Guid.NewGuid(), "agent", plan ?? policy.PlanningBytes, cap ?? policy.JobMemoryLimit);
        MemorySnapshot Ok(ulong avail = 8 * ResourcePolicy.GiB, ulong commitAvail = 20 * ResourcePolicy.GiB, double cpu = 10, int ageMs = 0) =>
            new(total, avail, 32 * ResourcePolicy.GiB, commitAvail, cpu, DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(ageMs));

        Test("ACC-R03 stale or invalid sample is hard deny", () =>
        {
            var stale = ResourcePolicy.Evaluate(Ok(ageMs: 4000), policy, [], [Agent(0)]);
            var badCpu = ResourcePolicy.Evaluate(Ok() with { CpuPercent = double.NaN }, policy, [], [Agent(0)]);
            var infCpu = ResourcePolicy.Evaluate(Ok() with { CpuPercent = double.PositiveInfinity }, policy, [], [Agent(0)]);
            var overCpu = ResourcePolicy.Evaluate(Ok() with { CpuPercent = 100.1 }, policy, [], [Agent(0)]);
            var negCpu = ResourcePolicy.Evaluate(Ok() with { CpuPercent = -0.1 }, policy, [], [Agent(0)]);
            var availOverTotal = ResourcePolicy.Evaluate(Ok(avail: total + 1), policy, [], [Agent(0)]);
            var zeroTotal = ResourcePolicy.Evaluate(Ok() with { Total = 0 }, policy, [], [Agent(0)]);
            if (stale.Kind != AdmissionKind.Deny || badCpu.Kind != AdmissionKind.Deny || infCpu.Kind != AdmissionKind.Deny || overCpu.Kind != AdmissionKind.Deny || negCpu.Kind != AdmissionKind.Deny || availOverTotal.Kind != AdmissionKind.Deny || zeroTotal.Kind != AdmissionKind.Deny || stale.SampleValid || badCpu.SampleValid || infCpu.SampleValid || overCpu.SampleValid || negCpu.SampleValid || availOverTotal.SampleValid || zeroTotal.SampleValid)
                throw new Exception("invalid sample not hard-denied");
        });

        Test("ACC-R02 hard floor cannot be waived", () =>
        {
            var d = ResourcePolicy.Evaluate(Ok(avail: 3 * ResourcePolicy.GiB / 2), policy, [], [Agent(0)]);
            if (d.Kind != AdmissionKind.Deny || !d.Reason.Contains("内存")) throw new Exception(d.Reason);
            var cpu = ResourcePolicy.Evaluate(Ok(cpu: 80), policy, [], [Agent(0)]);
            if (cpu.Kind != AdmissionKind.Deny) throw new Exception("CPU 80 was not hard-denied");
            var commit = ResourcePolicy.Evaluate(Ok(commitAvail: ResourcePolicy.GiB / 2), policy, [], [Agent(0)]);
            if (commit.Kind != AdmissionKind.Deny) throw new Exception("commit floor was waived");
        });

        Test("ACC-R01 practical soft overage requires waiver when hard checks pass", () =>
        {
            var many = Enumerable.Range(0, 8).Select(i => Agent(i)).ToArray();
            var d = ResourcePolicy.Evaluate(Ok(avail: 12 * ResourcePolicy.GiB, commitAvail: 24 * ResourcePolicy.GiB), policy, [], many);
            if (d.Kind != AdmissionKind.RequireWaiver) throw new Exception("expected waiver, got " + d.Kind + " " + d.Reason);
            if (d.CapSumRequested != 8 * policy.JobMemoryLimit) throw new Exception("soft cap used planning bytes");
        });

        Test("ACC-R06 conservative soft overage is denied", () =>
        {
            var cons = policy with { Mode = "conservative" };
            var many = Enumerable.Range(0, 8).Select(i => Agent(i)).ToArray();
            var d = ResourcePolicy.Evaluate(Ok(avail: 12 * ResourcePolicy.GiB, commitAvail: 24 * ResourcePolicy.GiB), cons, [], many);
            if (d.Kind != AdmissionKind.Deny) throw new Exception("conservative allowed overage");
        });

        Test("ACC-R04 legacy reserved item uses 768 MiB planning and Plan.Budget cap", () =>
        {
            var legacy = new BudgetItem(Guid.NewGuid(), "agent", ResourcePolicy.DefaultPlanningBytes, 2 * ResourcePolicy.GiB);
            var d = ResourcePolicy.Evaluate(Ok(), policy, [legacy], [Agent(0)]);
            if (d.PlanningReserved != ResourcePolicy.DefaultPlanningBytes) throw new Exception("legacy planning wrong");
            if (d.CapSumReserved != 2 * ResourcePolicy.GiB) throw new Exception("legacy cap wrong");
        });

        Test("ACC-C02 custom job cap cannot go below 1 GiB or zero floors", () =>
        {
            var bad = false;
            try { ResourcePolicy.ValidateSnapshot(policy with { JobMemoryLimit = 512 * ResourcePolicy.MiB }, total, 32 * ResourcePolicy.GiB); }
            catch (InvalidDataException) { bad = true; }
            if (!bad) throw new Exception("sub-GiB cap accepted");
            var zeroFloor = false;
            try { ResourcePolicy.ValidateSnapshot(policy with { PhysicalFloor = 0, Mode = "custom" }, total, 32 * ResourcePolicy.GiB); }
            catch (InvalidDataException) { zeroFloor = true; }
            if (!zeroFloor) throw new Exception("zero floor accepted");
        });

        Test("ACC-C01 default CPU rate stays 3500", () =>
        {
            if (ResourcePolicy.DefaultCpuRate != 3500) throw new Exception("cpu default changed");
            if (ResourcePolicy.DefaultJobMemoryLimit != 2 * ResourcePolicy.GiB) throw new Exception("job default changed");
        });

        Test("ACC-R01 custom soft overage requires waiver and two agents stay allow", () =>
        {
            var custom = policy with { Mode = "custom" };
            var many = Enumerable.Range(0, 8).Select(i => Agent(i)).ToArray();
            var over = ResourcePolicy.Evaluate(Ok(avail: 12 * ResourcePolicy.GiB, commitAvail: 24 * ResourcePolicy.GiB), custom, [], many);
            if (over.Kind != AdmissionKind.RequireWaiver) throw new Exception("custom should require waiver, got " + over.Kind);
            var two = ResourcePolicy.Evaluate(Ok(avail: 12 * ResourcePolicy.GiB, commitAvail: 24 * ResourcePolicy.GiB), policy, [], [Agent(0), Agent(1)]);
            if (two.Kind != AdmissionKind.Allow) throw new Exception("two agents under soft cap were not allowed: " + two.Kind + " " + two.Reason);
            if (two.CapSumRequested != 2 * policy.JobMemoryLimit) throw new Exception("two-agent cap sum");
        });

        Test("ACC-R04 git uses 256 MiB planning and 1 GiB cap", () =>
        {
            var git = new BudgetItem(Guid.NewGuid(), "git", 0, 0);
            var d = ResourcePolicy.Evaluate(Ok(), policy, [], [git, Agent(0)]);
            if (d.PlanningRequested != policy.GitPlanningBytes + policy.PlanningBytes) throw new Exception("git planning wrong: " + d.PlanningRequested);
            if (d.CapSumRequested != policy.GitJobMemoryLimit + policy.JobMemoryLimit) throw new Exception("git cap wrong: " + d.CapSumRequested);
            if (d.Kind != AdmissionKind.Allow) throw new Exception("git+agent should allow, got " + d.Kind + " " + d.Reason);
        });

        Test("ACC-C02 ValidateSnapshot applies sample-dependent tighter caps", () =>
        {
            var overJob = false;
            try { ResourcePolicy.ValidateSnapshot(policy with { Mode = "custom", JobMemoryLimit = 8 * ResourcePolicy.GiB, PlanningBytes = ResourcePolicy.DefaultPlanningBytes }, 4 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB); }
            catch (InvalidDataException) { overJob = true; }
            if (!overJob) throw new Exception("job cap above physical total accepted");
            var overFloor = false;
            try { ResourcePolicy.ValidateSnapshot(policy with { Mode = "custom", PhysicalFloor = 8 * ResourcePolicy.GiB }, 8 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB); }
            catch (InvalidDataException) { overFloor = true; }
            if (!overFloor) throw new Exception("physical floor above total/2 accepted");
        });
    }

    public static void RunPoolAccounting(Action<string, Action> Test)
    {
        var total = 16 * ResourcePolicy.GiB;
        var jobLimit = 4 * ResourcePolicy.GiB;
        var poolId = Guid.NewGuid();
        var shared = new ResourcePolicySnapshot("practical", jobLimit, ResourcePolicy.DefaultPlanningBytes,
            ResourcePolicy.DefaultPhysicalFloor(total), ResourcePolicy.DefaultCommitFloor(32 * ResourcePolicy.GiB),
            ResourcePolicy.DefaultCpuRate, ResourcePolicy.GitJobMemoryLimit, ResourcePolicy.DefaultGitPlanningBytes,
            ResourcePolicy.PoolShared, jobLimit);
        BudgetItem Pooled(int i) => new(Guid.NewGuid(), "agent", ResourcePolicy.DefaultPlanningBytes, jobLimit, poolId, shared.PoolMemoryLimit);
        BudgetItem Git() => new(Guid.NewGuid(), "git", ResourcePolicy.DefaultGitPlanningBytes, ResourcePolicy.GitJobMemoryLimit);
        MemorySnapshot Ok() => new(total, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 10, DateTimeOffset.UtcNow);

        Test("ACC-S3A01 shared pool bills its limit once instead of summing every item cap", () =>
        {
            var independent = shared with { PoolMode = ResourcePolicy.PoolIndependent, PoolMemoryLimit = 0 };
            var items = new[] { Pooled(0), Pooled(1) };
            var perItem = independent with { JobMemoryLimit = jobLimit };
            var summed = ResourcePolicy.Evaluate(Ok(), perItem, [], items.Select(i => i with { PoolId = null }).ToArray());
            var pooled = ResourcePolicy.Evaluate(Ok(), shared, [], items);
            if (summed.Kind != AdmissionKind.RequireWaiver) throw new Exception("independent caps did not exceed the soft budget: " + summed.Kind);
            if (pooled.Kind != AdmissionKind.Allow) throw new Exception("shared pool summed per-item caps: " + pooled.Kind + " " + pooled.Reason);
            if (pooled.CapSumRequested != jobLimit) throw new Exception("shared pool charged more than one pool limit");
        });

        Test("ACC-S3A02 a pool already reserved is not charged twice for a queued item in the same pool", () =>
        {
            var reserved = new[] { Pooled(0) };
            var requested = new[] { Pooled(1) };
            var d = ResourcePolicy.Evaluate(Ok(), shared, reserved, requested);
            if (d.Kind != AdmissionKind.Allow || d.CapSumReserved != jobLimit) throw new Exception("reserved pool charged twice");
        });

        Test("ACC-S3A03 git stays independently billed beside a shared pool", () =>
        {
            var items = new[] { Pooled(0), Pooled(1), Git() };
            var d = ResourcePolicy.Evaluate(Ok(), shared, [], items);
            if (d.Kind != AdmissionKind.Allow || d.CapSumRequested != jobLimit + ResourcePolicy.GitJobMemoryLimit)
                throw new Exception("git cap leaked into the pool bill");
        });

        Test("ACC-S3A04 pool identity and limits are part of the launch digest and policy hash", () =>
        {
            var item = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "pooled", "C:\\p", new WorkspaceIdentity("k", "C:\\p"), false,
                new("C:\agent.exe", [], "exe"), jobLimit, "agent", PoolId: poolId);
            var other = item with { PoolId = Guid.NewGuid() };
            if (ResourcePolicy.PlanDigest([item]) == ResourcePolicy.PlanDigest([other])) throw new Exception("plan digest ignored the pool");
            if (ResourcePolicy.PolicyHash(shared) == ResourcePolicy.PolicyHash(shared with { PoolMemoryLimit = 2 * ResourcePolicy.GiB })) throw new Exception("policy hash ignored the pool limit");
            if (ResourcePolicy.PolicyHash(shared) == ResourcePolicy.PolicyHash(shared with { PoolMode = ResourcePolicy.PoolIndependent })) throw new Exception("policy hash ignored the pool mode");
        });

        Test("ACC-S3A06 ledger migration reads version 1 as independent and rejects unknown pool references", () =>
        {
            var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "pooled", "C:\\p", new WorkspaceIdentity("k", "C:\\p"), false,
                new("C:\\agent.exe", [], "exe"), jobLimit, "agent");
            var legacy = new Ledger { SchemaVersion = 1 };
            legacy.Entries.Add(new() { Plan = plan });
            var migrated = LedgerMigration.Accept(legacy);
            if (migrated.SchemaVersion != LedgerMigration.CurrentSchemaVersion || migrated.Pools.Count != 0 || migrated.TaskClaims.Count != 0) throw new Exception("version 1 was not read as independent without invented claims");
            var future = new Ledger { SchemaVersion = int.MaxValue };
            var rejected = false;
            try { LedgerMigration.Accept(future); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("unknown ledger version accepted");
            var orphan = new Ledger { SchemaVersion = 2 };
            orphan.Entries.Add(new() { Plan = plan with { PoolId = poolId }, State = "Pending" });
            rejected = false;
            try { LedgerMigration.Accept(orphan); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("entry without its pool record accepted");
            var retired = new Ledger { SchemaVersion = 2 };
            retired.Entries.Add(new() { Plan = plan with { PoolId = poolId }, State = "Settled" });
            if (LedgerMigration.Accept(retired).Pools.Count != 0) throw new Exception("finished history invented a pool record");
            var duplicate = new Ledger { SchemaVersion = 2 };
            duplicate.Pools.Add(new() { Id = poolId, MemoryLimit = jobLimit, PolicyHash = "h", BatchId = Guid.NewGuid() });
            duplicate.Pools.Add(new() { Id = poolId, MemoryLimit = jobLimit, PolicyHash = "h", BatchId = Guid.NewGuid() });
            rejected = false;
            try { LedgerMigration.Accept(duplicate); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("duplicate pool records accepted");
        });

        Test("QQ-C06 ledger claims reject ambiguous ownership and survive migration and boot rollover", () =>
        {
            var claim = new TaskReservationClaim(Guid.NewGuid(), Guid.NewGuid());
            var current = new Ledger { BootId = "old", TaskClaims = [claim] };
            LedgerMigration.Accept(current);
            LedgerMigration.Rollover(current, "new");
            if (current.TaskClaims.Single() != claim) throw new Exception("boot rollover erased a queue claim");
            foreach (var claims in new List<TaskReservationClaim>[]
            {
                [claim, new(claim.EntryId, Guid.NewGuid())],
                [claim, new(Guid.NewGuid(), claim.AttemptId)],
                [new(Guid.Empty, Guid.NewGuid())],
                [new(Guid.NewGuid(), Guid.Empty)],
                Enumerable.Range(0, LedgerMigration.MaxTaskClaims + 1).Select(_ => new TaskReservationClaim(Guid.NewGuid(), Guid.NewGuid())).ToList()
            })
            {
                var rejected = false;
                try { LedgerMigration.Accept(new Ledger { TaskClaims = claims }); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("ambiguous or over-budget task claims were accepted");
            }
        });

        Test("QQ-C11 raw schema3 claim presence fails closed while legacy files migrate to empty", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "AgentLauncher-tests-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string Raw(string name, string json)
            {
                var path = Path.Combine(temp, name);
                File.WriteAllText(path, json);
                return path;
            }
            void Rejected(string name, string json)
            {
                var path = Raw(name, json);
                var rejected = false;
                try { LedgerMigration.Accept(Store.Read<Ledger>(path)!); } catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception(name + " was accepted without a TaskClaims presence proof");
                if (File.ReadAllText(path) != json) throw new Exception(name + " was rewritten by a rejected read");
            }
            Rejected("ledger-missing-claims.json", """{"SchemaVersion":3,"BootId":"boot","Entries":[],"Pools":[]}""");
            Rejected("ledger-null-claims.json", """{"SchemaVersion":3,"BootId":"boot","Entries":[],"Pools":[],"TaskClaims":null}""");
            Rejected("ledger-legacy-null-claims.json", """{"SchemaVersion":2,"BootId":"boot","Entries":[],"Pools":[],"TaskClaims":null}""");
            foreach (var version in new[] { 1, 2 })
            {
                var raw = "{\"SchemaVersion\":" + version + ",\"BootId\":\"boot\",\"Entries\":[],\"Pools\":[]}";
                var path = Raw("ledger-missing-claims-v" + version + ".json", raw);
                var migrated = LedgerMigration.Accept(Store.Read<Ledger>(path)!);
                if (migrated.SchemaVersion != LedgerMigration.CurrentSchemaVersion || migrated.TaskClaims.Count != 0)
                    throw new Exception("legacy schema " + version + " missing claims did not migrate to an empty list");
                if (File.ReadAllText(path) != raw) throw new Exception("legacy schema " + version + " was rewritten by migration");
            }
            var present = Raw("ledger-empty-claims.json", """{"SchemaVersion":3,"BootId":"boot","Entries":[],"Pools":[],"TaskClaims":[]}""");
            if (LedgerMigration.Accept(Store.Read<Ledger>(present)!).TaskClaims.Count != 0) throw new Exception("explicit empty claims did not stay empty");
            LedgerMigration.Accept(new Ledger());
            var roundTrip = Path.Combine(temp, "ledger-default-round-trip.json");
            Store.Save(roundTrip, new Ledger());
            LedgerMigration.Accept(Store.Read<Ledger>(roundTrip)!);
        });

        Test("QQ-C12 raw schema3 claim identity validation stays fail-closed", () =>
        {
            var temp = Path.Combine(Path.GetTempPath(), "AgentLauncher-tests-ledger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var path = Path.Combine(temp, "ledger-invalid-claims.json");
            var attempt = Guid.NewGuid();
            var raw = """{"SchemaVersion":3,"BootId":"boot","Entries":[],"Pools":[],"TaskClaims":[{"EntryId":"1f4e2d3c-4b5a-6978-8a9b-0c1d2e3f4a5b","AttemptId":"00000000-0000-0000-0000-000000000000"}]}""";
            File.WriteAllText(path, raw);
            var rejected = false;
            try { LedgerMigration.Accept(Store.Read<Ledger>(path)!); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("raw empty AttemptId was accepted");
            raw = "{\"SchemaVersion\":3,\"BootId\":\"boot\",\"Entries\":[],\"Pools\":[],\"TaskClaims\":["
                + "{\"EntryId\":\"" + Guid.NewGuid() + "\",\"AttemptId\":\"" + attempt + "\"},"
                + "{\"EntryId\":\"" + Guid.NewGuid() + "\",\"AttemptId\":\"" + attempt + "\"}]}";
            File.WriteAllText(path, raw);
            rejected = false;
            try { LedgerMigration.Accept(Store.Read<Ledger>(path)!); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("raw duplicate AttemptId was accepted");
            if (File.ReadAllText(path) != raw) throw new Exception("invalid claim file was rewritten");
        });

        Test("ACC-S3A07 boot rollover settles entries and retires pool records", () =>
        {
            var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "pooled", "C:\\p", new WorkspaceIdentity("k", "C:\\p"), false,
                new("C:\\agent.exe", [], "exe"), jobLimit, "agent", PoolId: poolId);
            var ledger = new Ledger { SchemaVersion = 2, BootId = "old" };
            ledger.Pools.Add(new() { Id = poolId, MemoryLimit = jobLimit, PolicyHash = "h", BatchId = Guid.NewGuid() });
            ledger.Entries.Add(new() { Plan = plan, State = "Creating" });
            var changed = LedgerMigration.Rollover(ledger, "new");
            if (!changed || ledger.BootId != "new" || ledger.Pools.Count != 0 || ledger.Entries[0].State != "Settled")
                throw new Exception("boot rollover kept pools or active entries");
            if (LedgerMigration.Rollover(ledger, "new")) throw new Exception("unchanged boot reported a rollover");
            LedgerMigration.Accept(ledger);
        });

        Test("ACC-S3A05 settings and launch items reject unsupported pool combinations", () =>
        {
            var draft = new Draft();
            draft.Resource.Pool = "shared";
            draft.Resource.JobMemoryLimit = jobLimit;
            draft.Resource.PoolMemoryLimit = 512 * ResourcePolicy.MiB;
            var rejected = false;
            try { draft.Validate(); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("pool limit below 1 GiB accepted");
            draft.Resource.PoolMemoryLimit = jobLimit;
            draft.Resource.Pool = "sometimes";
            rejected = false;
            try { draft.Validate(); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("unknown pool mode accepted");
            draft.Resource.Pool = ResourcePolicy.PoolShared;
            var item = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "pooled", "C:\\p", new WorkspaceIdentity("k", "C:\\p"), false,
                new("C:\agent.exe", [], "exe"), jobLimit, "agent");
            rejected = false;
            try { ResourcePolicy.ValidateLaunchItem(item, shared, total); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("shared mode accepted an item without a pool id");
            var independent = shared with { PoolMode = ResourcePolicy.PoolIndependent, PoolMemoryLimit = 0 };
            rejected = false;
            try { ResourcePolicy.ValidateLaunchItem(item with { PoolId = poolId }, independent, total); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("independent mode accepted a pool id");
        });
    }

    public static void RunWaivers(Action<string, Action> Test)
    {
        var total = 16 * ResourcePolicy.GiB;
        var policy = new ResourcePolicySnapshot("practical", ResourcePolicy.DefaultJobMemoryLimit, ResourcePolicy.DefaultPlanningBytes,
            ResourcePolicy.DefaultPhysicalFloor(total), ResourcePolicy.DefaultCommitFloor(32 * ResourcePolicy.GiB),
            ResourcePolicy.DefaultCpuRate, ResourcePolicy.GitJobMemoryLimit, ResourcePolicy.DefaultGitPlanningBytes);
        var now = new DateTimeOffset(2026, 11, 10, 13, 34, 0, TimeSpan.Zero);
        var clock = now;
        var table = new WaiverTable(() => clock);
        var slots = new Guid[] { Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"), Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1") };
        string Hash() => ResourcePolicy.PolicyHash(policy);
        var digest = "plan-digest-one";
        var require = new AdmissionDecision(AdmissionKind.RequireWaiver, "soft", 8 * policy.PlanningBytes, 0, 8 * policy.JobMemoryLimit, 0, ResourcePolicy.SoftCap(total), policy.PhysicalFloor, policy.CommitFloor, true);
        var deny = require with { Kind = AdmissionKind.Deny, Reason = "硬保护" };
        var allow = require with { Kind = AdmissionKind.Allow, Reason = "资源充足" };

        Test("ACC-R01 waiver issues only for RequireWaiver and is single-use", () =>
        {
            var denied = table.Issue(deny, 1, Hash(), digest, slots, now);
            if (denied.WaiverId != null) throw new Exception("hard deny issued a waiver");
            var allowed = table.Issue(allow, 1, Hash(), digest, slots, now);
            if (allowed.WaiverId != null) throw new Exception("allow issued a waiver");
            var issued = table.Issue(require, 1, Hash(), digest, slots, now);
            if (issued.WaiverId is not { } id || issued.WaiverExpiresAt != now.AddSeconds(ResourcePolicy.WaiverTtlSeconds)) throw new Exception("issue did not return a 45s waiver");
            if (!table.Consume(id, 1, Hash(), digest, slots)) throw new Exception("first consume failed");
            if (table.Consume(id, 1, Hash(), digest, slots)) throw new Exception("waiver was reused");
        });

        Test("ACC-R01 expired or rebound waiver cannot be consumed", () =>
        {
            clock = now;
            var issued = table.Issue(require, 4, Hash(), digest, slots, now);
            var id = issued.WaiverId ?? throw new Exception("missing waiver");
            if (table.Consume(id, 5, Hash(), digest, slots)) throw new Exception("revision change consumed");
            var issued2 = table.Issue(require, 4, Hash(), digest, slots, now);
            var id2 = issued2.WaiverId ?? throw new Exception("missing waiver");
            if (table.Consume(id2, 4, "deadbeef", digest, slots)) throw new Exception("policy hash change consumed");
            var issued3 = table.Issue(require, 4, Hash(), digest, slots, now);
            var id3 = issued3.WaiverId ?? throw new Exception("missing waiver");
            if (table.Consume(id3, 4, Hash(), "other-plan", slots)) throw new Exception("plan digest change consumed");
            var issued4 = table.Issue(require, 4, Hash(), digest, slots, now);
            var id4 = issued4.WaiverId ?? throw new Exception("missing waiver");
            clock = now.AddSeconds(46);
            if (table.Consume(id4, 4, Hash(), digest, slots)) throw new Exception("expired waiver consumed");
            if (ResourcePolicy.WaiverTtlSeconds != 45) throw new Exception("product TTL changed");
        });

        Test("ACC-R02 Issue does not accept a client confirmed flag", () =>
        {
            if (typeof(Request).GetProperty("Confirmed") != null || typeof(WaiverTable).GetMethod("Issue")!.GetParameters().Any(p => p.Name == "confirmed"))
                throw new Exception("client confirmed flag exists");
            var hash = Hash();
            if (ResourcePolicy.PolicyHash(policy) != hash) throw new Exception("PolicyHash is not stable");
            if (ResourcePolicy.PolicyHash(policy with { CpuRate = 4000 }) == hash) throw new Exception("PolicyHash ignored cpu");
        });
    }

    public static void RunAuthorityFixes(Action<string, Action> Test)
    {
        var total = 16 * ResourcePolicy.GiB;
        var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), total, 32 * ResourcePolicy.GiB);

        var digestRoot = Path.Combine(Path.GetTempPath(), "AgentLauncher-digest-" + Guid.NewGuid().ToString("N"));
        var planDir = Path.Combine(digestRoot, "plan");
        var otherDir = Path.Combine(digestRoot, "other");
        Directory.CreateDirectory(planDir);
        Directory.CreateDirectory(otherDir);

        LaunchItem Item(string name, string[] args, bool shared = true, string? cwd = null, ulong? planning = null, ulong? cap = null)
        {
            var directory = cwd ?? planDir;
            return new(Guid.NewGuid(), Guid.NewGuid(), 1, name, directory, PhysicalDirectory.Workspace(directory), shared,
                new CommandSpec("C:\\exe\\agent.exe", args, "exe"), cap ?? ResourcePolicy.DefaultJobMemoryLimit, "agent",
                PlanningBytes: planning, ResourceMode: "practical");
        }

        Test("ACC-R01 PlanDigest is unambiguous and includes Shared and Workspace", () =>
        {
            var a = Item("n", ["a", "b"]);
            var collided = Item("n", ["a\u0001b"]) with { Id = a.Id, BatchId = a.BatchId };
            if (ResourcePolicy.PlanDigest([a]) == ResourcePolicy.PlanDigest([collided]))
                throw new Exception("argument delimiter collision");
            var shared = Item("n", ["--x"]);
            var exclusive = shared with { Shared = false };
            if (ResourcePolicy.PlanDigest([shared]) == ResourcePolicy.PlanDigest([exclusive]))
                throw new Exception("Shared was omitted from digest");
            var relocated = shared with { Cwd = otherDir, Workspace = PhysicalDirectory.Workspace(otherDir) };
            if (ResourcePolicy.PlanDigest([shared]) == ResourcePolicy.PlanDigest([relocated]))
                throw new Exception("Workspace/Cwd was omitted from digest");
            var bound = shared with { SourceSlotId = Guid.NewGuid(), AgentId = "pi" };
            if (ResourcePolicy.PlanDigest([bound]) == ResourcePolicy.PlanDigest([bound with { SourceSlotId = Guid.NewGuid() }]))
                throw new Exception("stable source slot was omitted from digest");
            if (ResourcePolicy.PlanDigest([bound]) == ResourcePolicy.PlanDigest([bound with { AgentId = "codex" }]))
                throw new Exception("AgentId was omitted from digest");
            if (ResourcePolicy.PlanDigest([bound]) == ResourcePolicy.PlanDigest([bound with { Position = 16 }]))
                throw new Exception("slot position was omitted from digest");
        });

        Test("ACC-C02 default floors on large machines are not capped by custom maxima", () =>
        {
            var large = 128 * ResourcePolicy.GiB;
            var commit = 400 * ResourcePolicy.GiB;
            var settings = new ResourceSettings();
            ResourcePolicy.ValidateSettings(settings, large, commit);
            var snapshot = ResourcePolicy.SnapshotFor(settings, large, commit);
            if (snapshot.PhysicalFloor != ResourcePolicy.DefaultPhysicalFloor(large) || snapshot.PhysicalFloor != 16 * ResourcePolicy.GiB)
                throw new Exception("default physical floor was changed: " + snapshot.PhysicalFloor);
            if (snapshot.CommitFloor != ResourcePolicy.DefaultCommitFloor(commit) || snapshot.CommitFloor != 20 * ResourcePolicy.GiB)
                throw new Exception("default commit floor was changed: " + snapshot.CommitFloor);
            var decision = ResourcePolicy.Evaluate(new MemorySnapshot(large, 64 * ResourcePolicy.GiB, commit, 200 * ResourcePolicy.GiB, 10, DateTimeOffset.UtcNow), snapshot, [], [new BudgetItem(Guid.NewGuid(), "agent", ResourcePolicy.DefaultPlanningBytes, ResourcePolicy.DefaultJobMemoryLimit)]);
            if (decision.Kind == AdmissionKind.Deny && decision.Reason.Contains("临界余量"))
                throw new Exception("default 16 GiB floor was rejected: " + decision.Reason);
            var customRejected = false;
            try { ResourcePolicy.ValidateSettings(new ResourceSettings { Mode = "custom", PhysicalFloorOverride = 16 * ResourcePolicy.GiB }, large, commit); }
            catch (InvalidDataException) { customRejected = true; }
            if (!customRejected) throw new Exception("custom 16 GiB physical floor accepted");
            var customCommit = false;
            try { ResourcePolicy.ValidateSettings(new ResourceSettings { Mode = "custom", CommitFloorOverride = 20 * ResourcePolicy.GiB }, large, commit); }
            catch (InvalidDataException) { customCommit = true; }
            if (!customCommit) throw new Exception("custom 20 GiB commit floor accepted");
        });

        Test("ACC-R02 new requests reject illegal item planning and caps before reserve", () =>
        {
            ResourcePolicy.ValidateLaunchItem(Item("ok", ["--a"], planning: ResourcePolicy.DefaultPlanningBytes), policy, total);
            var tiny = false;
            try { ResourcePolicy.ValidateLaunchItem(Item("bad", ["--a"], planning: 1), policy, total); }
            catch (InvalidDataException) { tiny = true; }
            if (!tiny) throw new Exception("explicit planning=1 accepted");
            ResourcePolicy.ValidateLaunchItem(Item("legacy", ["--a"], planning: null), policy, total);
            var overTotal = false;
            try { ResourcePolicy.ValidateLaunchItem(Item("cap", ["--a"], planning: ResourcePolicy.DefaultPlanningBytes, cap: 8 * ResourcePolicy.GiB), policy, 4 * ResourcePolicy.GiB); }
            catch (InvalidDataException) { overTotal = true; }
            if (!overTotal) throw new Exception("cap above machine total accepted");
            var mismatch = false;
            try { ResourcePolicy.ValidateLaunchItem(Item("mis", ["--a"], planning: ResourcePolicy.DefaultPlanningBytes, cap: 4 * ResourcePolicy.GiB), policy, total); }
            catch (InvalidDataException) { mismatch = true; }
            if (!mismatch) throw new Exception("item cap disagreed with approved policy");
        });

        Test("ACC-R02 nondefault custom planning cannot use legacy request omission", () =>
        {
            var custom = policy with { Mode = "custom", PlanningBytes = ResourcePolicy.GiB };
            foreach (var missing in new ulong?[] { null, 0 })
            {
                var candidate = Item("custom-missing", [], planning: missing) with { ResourceMode = "custom" };
                var rejected = false;
                try { ResourcePolicy.ValidateLaunchItem(candidate, custom, total); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("missing custom planning was accepted and counted as " + ResourcePolicy.BudgetOf(candidate).PlanningBytes);
            }
            var explicitPlan = Item("custom-explicit", [], planning: ResourcePolicy.GiB) with { ResourceMode = "custom" };
            ResourcePolicy.ValidateLaunchItem(explicitPlan, custom, total);
            if (ResourcePolicy.BudgetOf(explicitPlan).PlanningBytes != custom.PlanningBytes) throw new Exception("custom explicit planning changed");
            var legacy = Item("legacy-active", [], planning: null);
            if (ResourcePolicy.BudgetOf(legacy).PlanningBytes != ResourcePolicy.DefaultPlanningBytes || ResourcePolicy.BudgetOf(legacy).JobMemoryLimit != legacy.Budget)
                throw new Exception("legacy active ledger semantics changed");
            ResourcePolicy.ValidateLaunchItem(legacy, policy, total);
            ResourcePolicy.ValidateLaunchItem(legacy with { PlanningBytes = 0 }, policy, total);
            ResourcePolicy.ValidateLaunchItem(legacy, custom with { PlanningBytes = ResourcePolicy.DefaultPlanningBytes }, total);
            var git = legacy with { Kind = "git", Budget = ResourcePolicy.GitJobMemoryLimit, PlanningBytes = null };
            ResourcePolicy.ValidateLaunchItem(git, custom, total);
            if (ResourcePolicy.BudgetOf(git).PlanningBytes != ResourcePolicy.DefaultGitPlanningBytes || ResourcePolicy.BudgetOf(git).JobMemoryLimit != ResourcePolicy.GitJobMemoryLimit)
                throw new Exception("Git fixed budget changed with custom policy");
        });

        Test("ACC-R01 waiver binds ordered stable slots and full configuration identity", () =>
        {
            var clock = DateTimeOffset.UtcNow;
            var table = new WaiverTable(() => clock);
            var require = new AdmissionDecision(AdmissionKind.RequireWaiver, "soft", 0, 0, 0, 0, ResourcePolicy.SoftCap(total), policy.PhysicalFloor, policy.CommitFloor, true);
            var slots = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var grant = table.Issue(require, 3, "policy", "plan", slots, clock, "configuration-a");
            if (grant.WaiverId is not { } first || table.Consume(first, 3, "policy", "plan", slots.Reverse().ToArray(), "configuration-a"))
                throw new Exception("waiver accepted reordered source slots");
            grant = table.Issue(require, 3, "policy", "plan", slots, clock, "configuration-a");
            if (grant.WaiverId is not { } second || table.Consume(second, 3, "policy", "plan", slots, "configuration-b"))
                throw new Exception("waiver survived a same-revision configuration change");
            grant = table.Issue(require, 3, "policy", "plan", slots, clock, "configuration-a");
            table.InvalidateMismatched(3, "policy", "configuration-b");
            if (table.Count != 0 || grant.WaiverId is not { } third || table.Consume(third, 3, "policy", "plan", slots, "configuration-a"))
                throw new Exception("changed authority did not invalidate grants");
        });

        Test("ACC-R05 begin hard-only check ignores conservative soft overage from later batches", () =>
        {
            var conservative = policy with { Mode = "conservative" };
            var first = Enumerable.Range(0, 2).Select(i => new BudgetItem(Guid.NewGuid(), "agent", policy.PlanningBytes, policy.JobMemoryLimit)).ToArray();
            var later = Enumerable.Range(0, 3).Select(i => new BudgetItem(Guid.NewGuid(), "agent", policy.PlanningBytes, policy.JobMemoryLimit)).ToArray();
            var sample = new MemorySnapshot(total, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 10, DateTimeOffset.UtcNow);
            var full = ResourcePolicy.Evaluate(sample, conservative, later, []);
            if (full.Kind != AdmissionKind.Deny) throw new Exception("setup: conservative soft overage should deny full evaluate, got " + full.Kind);
            var hardOnly = ResourcePolicy.Evaluate(sample, conservative, later.Concat(first).ToArray(), [], includeSoftBudget: false);
            if (hardOnly.Kind != AdmissionKind.Allow) throw new Exception("begin hard-only failed: " + hardOnly.Kind + " " + hardOnly.Reason);
        });

        Test("ACC-R03 pressure requires three consecutive high samples and five healthy recoveries", () =>
        {
            var floor = ResourcePolicy.DefaultPhysicalFloor(total);
            var tracker = new PressureTracker();
            MemorySnapshot Sample(ulong avail, double cpu, int ageMs = 0) => new(total, avail, 32 * ResourcePolicy.GiB, 20 * ResourcePolicy.GiB, cpu, DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(ageMs));
            tracker.Observe(Sample(12 * ResourcePolicy.GiB, 85), floor);
            tracker.Observe(Sample(12 * ResourcePolicy.GiB, 85), floor);
            if (tracker.Pressure) throw new Exception("pressure latched after two CPU highs");
            tracker.Observe(Sample(12 * ResourcePolicy.GiB, 85), floor);
            if (!tracker.Pressure) throw new Exception("pressure not latched after three CPU highs");
            var reset = new PressureTracker();
            reset.Observe(Sample(ResourcePolicy.GiB, 10), floor);
            if (reset.Pressure) throw new Exception("single low-physical sample latched pressure");
            reset.Observe(Sample(ResourcePolicy.GiB, 10), floor);
            reset.Observe(Sample(ResourcePolicy.GiB, 10), floor);
            if (!reset.Pressure) throw new Exception("pressure not latched after three low-physical samples");
            var healthyCpuHigh = new PressureTracker();
            healthyCpuHigh.Observe(Sample(12 * ResourcePolicy.GiB, 85), floor);
            healthyCpuHigh.Observe(Sample(12 * ResourcePolicy.GiB, 85), floor);
            healthyCpuHigh.Observe(Sample(12 * ResourcePolicy.GiB, 85), floor);
            healthyCpuHigh.Observe(Sample(12 * ResourcePolicy.GiB, 85), floor);
            if (!healthyCpuHigh.Pressure) throw new Exception("setup failed");
            if (healthyCpuHigh.HealthyStreak != 0) throw new Exception("CPU 80+ sample was counted healthy");
            for (var i = 0; i < 4; i++) healthyCpuHigh.Observe(Sample(12 * ResourcePolicy.GiB, 10), floor);
            if (!healthyCpuHigh.Pressure) throw new Exception("pressure cleared before five healthy samples");
            healthyCpuHigh.Observe(Sample(12 * ResourcePolicy.GiB, 10), floor);
            if (healthyCpuHigh.Pressure) throw new Exception("pressure not cleared after five healthy samples");
            var invalid = new PressureTracker();
            invalid.Observe(Sample(ResourcePolicy.GiB, 10), floor);
            invalid.Observe(Sample(ResourcePolicy.GiB, 10), floor);
            invalid.Observe(Sample(12 * ResourcePolicy.GiB, 10, ageMs: 4000), floor);
            if (invalid.HighStreak == 3 || invalid.Pressure) throw new Exception("invalid sample counted toward pressure");
        });

        Test("waiver table prunes expired grants and enforces a capacity bound", () =>
        {
            var clock = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
            var table = new WaiverTable(() => clock);
            var require = new AdmissionDecision(AdmissionKind.RequireWaiver, "soft", 0, 0, 0, 0, ResourcePolicy.SoftCap(total), policy.PhysicalFloor, policy.CommitFloor, true);
            for (var i = 0; i < WaiverTable.MaxActive; i++)
            {
                var issued = table.Issue(require, 1, "hash", "digest-" + i, [Guid.NewGuid()], clock);
                if (issued.WaiverId is null) throw new Exception("issue failed before capacity");
            }
            var overflow = table.Issue(require, 1, "hash", "overflow", [Guid.NewGuid()], clock);
            if (overflow.WaiverId is not null) throw new Exception("capacity was not enforced");
            if (table.Count != WaiverTable.MaxActive) throw new Exception("count drifted");
            clock = clock.AddSeconds(46);
            table.PruneExpired();
            if (table.Count != 0) throw new Exception("expired grants were not pruned: " + table.Count);
            var again = table.Issue(require, 1, "hash", "after-prune", [Guid.NewGuid()], clock);
            if (again.WaiverId is null) throw new Exception("issue failed after prune");
        });
    }
}
