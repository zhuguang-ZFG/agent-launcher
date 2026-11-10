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
}
