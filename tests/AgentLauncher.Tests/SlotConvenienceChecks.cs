using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class SlotConvenienceChecks
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("UX11 duplicate configuration has independent identity and profiles", () =>
        {
            var source = new Slot
            {
                Enabled = true, AgentId = "codex", CustomName = "接口实现", Directory = "C:\\work\\api", Note = "review endpoints",
                Profiles = new() { ["codex"] = new() { Executable = "codex.cmd", Arguments = ["--model", "model with spaces", ""] }, ["omp"] = new() { Arguments = ["--continue"] } }
            };
            var duplicate = source.Duplicate();
            assert(duplicate.Id != source.Id && duplicate.Id != Guid.Empty && !duplicate.Enabled && source.Enabled, "duplicate reused identity or changed enabled source");
            assert(duplicate.AgentId == source.AgentId && duplicate.DisplayName == "接口实现 副本" && duplicate.Directory == source.Directory && duplicate.Note == source.Note, "copied configuration lost fields");
            assert(duplicate.Current.Arguments.SequenceEqual(source.Current.Arguments), "structured arguments were changed");
            duplicate.Profiles["codex"].Arguments[1] = "other model";
            duplicate.Profiles["omp"].Arguments[0] = "--new";
            duplicate.Profiles["codex"].Executable = "other.exe";
            assert(source.Current.Arguments[1] == "model with spaces" && source.Current.Executable == "codex.cmd" && source.Profiles["omp"].Arguments[0] == "--continue", "duplicate edits leaked into an original profile");
            assert(source.Copy().Id == source.Id, "snapshot Copy must preserve identity");
        });
        test("UX11 duplicate preserves unresolved directory requirements", () =>
        {
            var source = new Slot { RequiresDirectory = true };
            var first = source.Duplicate(); var second = source.Duplicate();
            assert(first.RequiresDirectory && first.Directory == "" && first.Id != second.Id, "copy silently inherited a project or reused identity");
            var draft = new Draft { Slots = [source, first, second] }; draft.Validate();
        });
        test("UX11 role names persist for built-in agents and portable templates", () =>
        {
            var config = new Configuration();
            config.Edit(d => { d.Slots[0].AgentId = "codex"; d.Slots[0].CustomName = "代码审查"; d.Slots[0].Directory = "C:\\work\\review"; });
            config.SaveTemplate("review", portable: true);
            var restored = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config, Store.Json), Store.Json)!;
            restored.Restore(restored.Templates["review"]);
            var slot = restored.Draft.Slots[0];
            assert(slot.DisplayName == "代码审查" && slot.AgentId == "codex" && slot.RequiresDirectory, "template lost role/type or cleared directory safety requirement");
            slot.CustomName = " "; assert(slot.DisplayName == "Codex", "empty role should use Agent name");
            slot.AgentId = "claude"; slot.CustomName = "自用工具";
            assert(slot.DisplayName == "自用工具", "existing role naming changed");
        });
    }
}
