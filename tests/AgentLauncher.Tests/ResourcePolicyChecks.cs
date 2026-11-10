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
}
