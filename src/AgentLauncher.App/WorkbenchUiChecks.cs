#if INTEGRATION_TESTS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
        var previousTimer = timer.IsEnabled;
        timer.Stop();
        try
        {
            windowMode = MainWindowMode.StartPage;
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
                StartPagePanel.Visibility == Visibility.Visible
                && Rail.Visibility == Visibility.Collapsed
                && PreviewCard.Visibility == Visibility.Collapsed
                && RefreshLaunchButton.Visibility == Visibility.Collapsed
                && (LaunchOptionsExpander.Visibility == Visibility.Collapsed || !LaunchOptionsExpander.IsVisible));
            check("start page has a single launch button with the approved label",
                LaunchButton.IsVisible
                && Equals(LaunchButton.Content, StartPageSurface.LaunchButtonText(CountReadySelected(), false))
                && !LaunchHint.Text.Contains("预览不是")
                && !LaunchHint.Text.Contains("启动授权"));
            check("agent chips live on the start page", AgentChipPanel.IsVisible && ReviewFind<ToggleButton>(AgentChipPanel).Any());

            var selected = CountReadySelected();
            TrySelectOverCapReadyAgent();
            check("seventh ready agent stays unselected and explains the cap",
                CountReadySelected() == Math.Min(selected, WorkbenchLayout.MaxPanes)
                && (CountReadySelected() < WorkbenchLayout.MaxPanes || LaunchHint.Text.Contains(StartPageSurface.CapDenied)));
        }
        finally
        {
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