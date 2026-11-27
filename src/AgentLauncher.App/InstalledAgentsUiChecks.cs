#if INTEGRATION_TESTS
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;
namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunInstalledAgentsChecksAsync(string folder, Action<string, bool> check)
    {
        var originalConfig = config; var originalInstalls = discoveredInstalls; var originalBook = taskBook;
        var originalDiscoveryStatus = DiscoveryStatus.Text; var originalProgress = ProgressText.Text;
        var originalReadOnly = readOnly; var originalBusy = busy; var originalClose = closeRequested;
        var wasPolling = timer.IsEnabled; var wasPumping = taskTimer.IsEnabled;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var diskBefore = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        var binDir = Path.GetFullPath(Path.Combine(folder, "installed-agents-bin"));
        Directory.CreateDirectory(binDir);
        foreach (var name in new[] { "omp.exe", "codex.exe", "claude.exe" }) File.WriteAllText(Path.Combine(binDir, name), "");
        Environment.SetEnvironmentVariable("PATH", binDir + Path.PathSeparator + (originalPath ?? ""));
        try
        {
            timer.Stop(); taskTimer.Stop(); await saveTask;
            taskBook = new()
            {
                Revision = 1,
                Tasks =
                [
                    new() { Id = Guid.NewGuid(), Revision = 1, Name = "claude 任务一", Project = Store.Root, AgentId = "claude" },
                    new() { Id = Guid.NewGuid(), Revision = 1, Name = "claude 任务二", Project = Store.Root, AgentId = "claude" },
                    new() { Id = Guid.NewGuid(), Revision = 1, Name = "codex 任务", Project = Store.Root, AgentId = "codex" }
                ]
            };
            config = new Configuration { Theme = "dark" };
            config.Draft.Project = Store.Root;
            config.Draft.Slots =
            [
                new() { AgentId = "omp", Enabled = true, Note = "保留项" },
                new() { AgentId = "omp" },
                new() { AgentId = "codex" },
                new() { AgentId = "omp", Directory = Path.Combine(folder, "keep"), Note = "已有目录" }
            ];
            readOnly = false; busy = false; closeRequested = false; ApplyTheme(); Render();
            var actions = BuildQuickActions();
            check("UX13 installed-agent action is available in quick actions", actions.Any(action => action.Id == "installed-agents" && action.Enabled && action.Title.Contains("补齐")));
            var surface = ReviewFind<Button>(Rail).Single(button => button.Name == "InstalledAgentsButton");
            check("UX13 one-click fill button is visible in the rail",
                surface.Visibility == Visibility.Visible && surface.IsEnabled && surface.Content as string == "补齐本机 Agent"
                && AutomationProperties.GetName(surface) == "补齐本机 Agent");
            var fill = actions.Single(action => action.Id == "installed-agents");
            var blankIds = config.Draft.Slots.Where(IsBlankSlot).Select(slot => slot.Id).ToArray();
            await fill.Run();
            check("UX13 injected CLI directory is scanned by the quick action",
                discoveredInstalls.TryGetValue("claude", out var claudePaths)
                && claudePaths.Any(path => Path.GetFullPath(path).StartsWith(binDir + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase)));
            var filledByFrequency = blankIds.Select(id => config.Draft.Slots.Single(slot => slot.Id == id).AgentId).ToArray();
            check("UX13 task usage frequency ranks the installed-agent fill order",
                filledByFrequency.SequenceEqual(["claude", "codex"]));
            var ids = config.Draft.Slots.Select(slot => slot.AgentId).ToArray();
            check("UX13 detected agents fill blank slots without touching configured slots",
                ids.Contains("claude") && ids.Contains("codex")
                && config.Draft.Slots.Single(slot => slot.Note == "保留项").AgentId == "omp"
                && config.Draft.Slots.Single(slot => slot.Note == "已有目录").AgentId == "omp"
                && config.Draft.Slots.Single(slot => slot.Note == "已有目录").Directory == Path.Combine(folder, "keep"));
            check("UX13 discovery status names the agents that were added",
                DiscoveryStatus.Text.Contains("Claude", System.StringComparison.OrdinalIgnoreCase)
                && DiscoveryStatus.Text.Contains("Codex", System.StringComparison.OrdinalIgnoreCase)
                && DiscoveryStatus.Text.Contains("Omp", System.StringComparison.OrdinalIgnoreCase));
            var revision = config.Draft.Revision;
            var slotsAfterFill = config.Draft.Slots.Select(slot => (slot.Id, slot.AgentId, slot.Enabled)).ToArray();
            await fill.Run();
            check("UX13 repeating the action leaves the draft unchanged",
                config.Draft.Revision == revision
                && config.Draft.Slots.Select(slot => (slot.Id, slot.AgentId, slot.Enabled)).SequenceEqual(slotsAfterFill));
            readOnly = true;
            await fill.Run();
            check("UX13 read-only configuration refuses installed-agent changes", config.Draft.Revision == revision);
            readOnly = false;

            // 默认配置自带未勾选且完全空白的 codex/claude 槽位；补齐必须复用它们，
            // 不能因为旧逻辑只认 omp 槽位而新建同 Agent 的第二份配置。
            config = new Configuration { Theme = "dark" };
            config.Draft.Project = Store.Root;
            ApplyTheme(); Render();
            var codexSlotId = config.Draft.Slots.Single(slot => slot.AgentId == "codex").Id;
            var claudeSlotId = config.Draft.Slots.Single(slot => slot.AgentId == "claude").Id;
            await fill.Run();
            check("UX13 default codex and claude slots are reused instead of duplicated",
                config.Draft.Slots.Count(slot => slot.AgentId == "codex") == 1
                && config.Draft.Slots.Count(slot => slot.AgentId == "claude") == 1
                && config.Draft.Slots.Single(slot => slot.Id == codexSlotId).Enabled
                && config.Draft.Slots.Single(slot => slot.Id == claudeSlotId).Enabled);
        }
        finally
        {
            readOnly = originalReadOnly; busy = originalBusy; closeRequested = originalClose;
            await saveTask;
            config = originalConfig; discoveredInstalls = originalInstalls; taskBook = originalBook;
            DiscoveryStatus.Text = originalDiscoveryStatus; ProgressText.Text = originalProgress;
            Environment.SetEnvironmentVariable("PATH", originalPath);
            if (diskBefore is not null) File.WriteAllBytes(configPath, diskBefore);
            else if (File.Exists(configPath)) File.Delete(configPath);
            ApplyTheme(); Render();
            if (wasPolling) timer.Start(); if (wasPumping) taskTimer.Start();
        }
    }
}
#endif
