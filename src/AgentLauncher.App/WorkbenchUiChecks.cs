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
        }
        finally
        {
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
