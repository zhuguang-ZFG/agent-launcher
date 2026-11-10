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
}
