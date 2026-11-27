#if INTEGRATION_TESTS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunWorkbenchUiChecksAsync(Action<string, bool> check)
    {
        await saveTask;
        await commandCheckTask;
        var previous = config;
        var previousStatus = status;
        var previousReadOnly = readOnly;
        var previousError = ErrorText.Text;
        var previousTimer = timer.IsEnabled;
        timer.Stop();
        try
        {
            windowMode = MainWindowMode.StartPage;
            capDeniedSticky = false;
            config = new()
            {
                Theme = "dark",
                Draft = new()
                {
                    Project = Store.Root,
                    Slots = Enumerable.Range(0, 7).Select(index => new Slot
                    {
                        AgentId = "custom",
                        Enabled = index < 6,
                        Profiles = new() { ["custom"] = new() { Executable = System.IO.Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe") } }
                    }).ToList()
                }
            };
            var sample = new MemorySnapshot(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
            status = new(true, "workbench-ui", [], sample);
            readOnly = false;
            ApplyWindowMode();
            ApplyTheme();
            Render();
            await commandCheckTask;
            UpdateLaunch();
            check("start page hides rail preview and extra launch actions",
                StartPagePanel.IsVisible
                && !Rail.IsVisible
                && !PreviewCard.IsVisible
                && !RefreshLaunchButton.IsVisible
                && !LaunchOptionsExpander.IsVisible);
            check("start page has a single launch button with the approved label",
                LaunchButton.IsVisible
                && Equals(LaunchButton.Content, StartPageSurface.LaunchButtonText(CountReadySelected(), false))
                && !LaunchHint.Text.Contains("预览不是")
                && !LaunchHint.Text.Contains("启动授权"));
            check("agent chips live on the start page", AgentChipPanel.IsVisible && ReviewFind<ToggleButton>(AgentChipPanel).Count() == 7);

            var readyCount = config.Draft.Slots.Count(IsSlotReady);
            check("seven ready chips are available before the cap click", readyCount == 7 && CountReadySelected() == WorkbenchLayout.MaxPanes);
            TrySelectOverCapReadyAgent();
            check("seventh ready agent stays unselected and explains the cap",
                CountReadySelected() == WorkbenchLayout.MaxPanes
                && !config.Draft.Slots[6].Enabled
                && LaunchHint.Text.Contains(StartPageSurface.CapDenied));
            UpdateLaunch();
            check("cap denial stays visible after a later refresh",
                CountReadySelected() == WorkbenchLayout.MaxPanes
                && LaunchHint.Text.Contains(StartPageSurface.CapDenied));

            var selectedUnready = config.Draft.Slots.First(slot => slot.Enabled);
            chipStatus[selectedUnready.Id] = (false, "命令不可用");
            RenderAgentChips();
            var unreadyChip = ReviewFind<ToggleButton>(AgentChipPanel).Single(button => Equals(button.Tag, selectedUnready.Id));
            check("already-selected unready chip can still be turned off", unreadyChip.IsEnabled && unreadyChip.IsChecked == true);
            unreadyChip.IsChecked = false;
            check("already-selected unready chip unchecks", !config.Draft.Slots.Single(slot => slot.Id == selectedUnready.Id).Enabled && unreadyChip.IsChecked == false);

            await GuardAsync(() => throw new System.IO.IOException("启动失败：没有启动任何 Agent"));
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("start-page launch failure is visible",
                StartPagePanel.IsVisible
                && ErrorBanner.IsVisible
                && ErrorText.IsVisible
                && ErrorText.Text.Contains("启动失败")
                && LaunchHint.Text.Contains("启动失败"));

            EnterWorkbench(Array.Empty<ControlEntry>());
            UpdateLaunch();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("zero started stays on the start page with the reason visible and the button restored",
                windowMode == MainWindowMode.StartPage
                && StartPagePanel.Visibility == Visibility.Visible
                && WorkbenchPanel.Visibility == Visibility.Collapsed
                && Equals(LaunchButton.Content, StartPageSurface.LaunchButtonText(CountReadySelected(), false))
                && ErrorBanner.IsVisible
                && ErrorText.IsVisible
                && ErrorText.Text.Contains("没有启动任何 Agent")
                && LaunchHint.Text.Contains("没有启动任何 Agent"));

            ErrorText.Text = "";
            LaunchItem FakePlan(int position, string name) => new(
                Guid.NewGuid(), Guid.NewGuid(), position, name, Store.Root,
                new WorkspaceIdentity(Store.Root, "workbench-" + position), false,
                new CommandSpec("fixture.exe", [], "exe"), ResourcePolicy.AgentBudget);
            var entries = new ControlEntry[]
            {
                new() { Plan = FakePlan(1, "工作台甲"), State = "Started", Detail = "已启动进程" },
                new() { Plan = FakePlan(2, "工作台乙"), State = "Started", Detail = "已启动进程" }
            };
            var startedCount = entries.Length;
            EnterWorkbench(entries);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("launching at least one agent switches the window into the workbench",
                startedCount >= 1
                && windowMode == MainWindowMode.Workbench
                && WorkbenchPanel.Visibility == Visibility.Visible
                && StartPagePanel.Visibility == Visibility.Collapsed);
            check("workbench hosts real terminal cards rather than the static preview",
                ReviewFind<TerminalCard>(WorkbenchHost).Count() == startedCount
                && PreviewCard.Visibility != Visibility.Visible);
        }
        finally
        {
            ResetWorkbenchForChecks();
            ErrorText.Text = previousError;
            capDeniedSticky = false;
            config = previous;
            status = previousStatus;
            readOnly = previousReadOnly;
            if (previousTimer) timer.Start();
            ApplyWindowMode();
            ApplyTheme();
            Render();
        }
    }
}
#endif
