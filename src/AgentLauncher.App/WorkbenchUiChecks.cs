#if INTEGRATION_TESTS
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
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

            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var chipBottom = AgentChipCard.TranslatePoint(new Point(0, AgentChipCard.ActualHeight), this).Y;
            var buttonAt = LaunchButton.TranslatePoint(new Point(), this);
            var nearChips = buttonAt.Y - chipBottom is >= -8 and <= 96;
            var pinnedToWindowCorner = ActualHeight - (buttonAt.Y + LaunchButton.ActualHeight) < 48 && ActualWidth - (buttonAt.X + LaunchButton.ActualWidth) < 48;
            check("start-page content is in a composed column",
                StartPageColumn.MaxWidth is >= 760 and <= 880
                && LaunchButton.IsVisible
                && nearChips
                && !pinnedToWindowCorner);

            Color? ChipFill(ToggleButton chip)
            {
                chip.ApplyTemplate();
                if (chip.Template?.FindName("Chrome", chip) is Border chrome && chrome.Background is SolidColorBrush direct) return direct.Color;
                Border? Walk(DependencyObject node)
                {
                    if (node is Border { Name: "Chrome" } border) return border;
                    for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                        if (Walk(VisualTreeHelper.GetChild(node, i)) is { } found) return found;
                    return null;
                }
                return Walk(chip)?.Background is SolidColorBrush brush ? brush.Color : null;
            }
            bool PaleFill(Color color) => 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 180;
            var selectedChip = ReviewFind<ToggleButton>(AgentChipPanel).FirstOrDefault(chip => chip.IsChecked == true);
            var unreadyChipFill = ReviewFind<ToggleButton>(AgentChipPanel).FirstOrDefault(chip => chip.IsChecked != true);
            var selectedFill = selectedChip is null ? null : ChipFill(selectedChip);
            var idleFill = unreadyChipFill is null ? null : ChipFill(unreadyChipFill);
            check("selected chips are not light fills on the dark page",
                SystemParameters.HighContrast
                || (selectedFill is { } selectedColor && !PaleFill(selectedColor)
                    && idleFill is { } idleColor && !PaleFill(idleColor)));

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

            ErrorText.Text = "";
            UpdateLaunch();
            CompleteLaunchSurface([], CountReadySelected(), "零启动原因：没有启动任何 Agent");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("zero started stays on the start page with the reason visible and the button restored",
                windowMode == MainWindowMode.StartPage
                && StartPagePanel.Visibility == Visibility.Visible
                && WorkbenchPanel.Visibility == Visibility.Collapsed
                && Equals(LaunchButton.Content, StartPageSurface.LaunchButtonText(CountReadySelected(), false))
                && ErrorBanner.IsVisible
                && ErrorText.IsVisible
                && ErrorText.Text.Contains("零启动原因")
                && LaunchHint.Text.Contains("零启动原因"));

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

            config.EmbeddedTerminal = false;
            config.TerminalPath = System.IO.Path.Combine(Store.Root, "missing-wt.exe");
            var resolved = await ResolveLaunchTerminalAsync();
            ResetWorkbenchForChecks();
            EnterWorkbench([FakePlan(1, "仍走嵌入")]);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("saved external Terminal setting still opens the embedded workbench",
                resolved.Length == 0
                && windowMode == MainWindowMode.Workbench
                && WorkbenchPanel.IsVisible
                && !StartPagePanel.IsVisible
                && !EmbeddedPanel.IsVisible
                && ReviewFind<TerminalCard>(WorkbenchHost).Count() == 1
                && PreviewCard.Visibility != Visibility.Visible);

            ResetWorkbenchForChecks();
            var reopen = FakePlan(3, "重开会话");
            OpenEmbedded(reopen, false);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var hiddenBefore = windowMode == MainWindowMode.StartPage && !ReviewFind<TerminalCard>(WorkbenchHost).Any();
            var shown = await TryShowEmbeddedAsync(reopen);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var reopenCard = ReviewFind<TerminalCard>(WorkbenchHost).FirstOrDefault(card => AutomationProperties.GetName(card) == "重开会话 终端");
            check("reopening an embedded session from the start page shows a workbench terminal",
                hiddenBefore
                && shown
                && windowMode == MainWindowMode.Workbench
                && WorkbenchPanel.Visibility == Visibility.Visible
                && StartPagePanel.Visibility == Visibility.Collapsed
                && reopenCard is { IsVisible: true }
                && !EmbeddedPanel.IsVisible);

            ShowPartialLaunchNotice(1, 2, "内存不足");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("partial admission explains the shortfall on the workbench",
                WorkbenchNotice.IsVisible
                && WorkbenchNotice.Text == StartPageSurface.PartialLaunchLine(1, 1, "内存不足"));

            FrameworkElement? PaneRoot(string name)
            {
                var card = ReviewFind<TerminalCard>(WorkbenchHost).FirstOrDefault(item => AutomationProperties.GetName(item) == name + " 终端");
                DependencyObject? node = card;
                while (node is not null)
                {
                    if (node is FrameworkElement element && WorkbenchHost.Children.Contains(element)) return element;
                    node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
                }
                return null;
            }
            bool HasButton(FrameworkElement? root, string content) =>
                root is not null && ReviewFind<Button>(root).Any(button => Equals(button.Content, content) && button.IsVisible);
            void DoubleClick(FrameworkElement target)
            {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = Control.MouseDoubleClickEvent,
                    Source = target
                };
                target.RaiseEvent(args);
            }
            int VisibleHostTiles() =>
                WorkbenchHost.Children.OfType<FrameworkElement>().Count(child => child.IsVisible);

            ResetWorkbenchForChecks();
            var stopA = FakePlan(1, "停止甲");
            var stopB = FakePlan(2, "停止乙");
            EnterWorkbench([stopA, stopB]);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var stopRootA = PaneRoot("停止甲");
            var stopButton = stopRootA is null ? null : ReviewFind<Button>(stopRootA).FirstOrDefault(button => Equals(button.Content, "停止"));
            if (stopButton is not null) stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else SetWorkbenchPaneStatusForChecks(stopA.Id, WorkbenchPaneStatus.Exited, "退出码 1");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            stopRootA = PaneRoot("停止甲");
            check("after stopping one pane the workbench stays open and that pane offers restart",
                windowMode == MainWindowMode.Workbench
                && WorkbenchPanel.Visibility == Visibility.Visible
                && StartPagePanel.Visibility == Visibility.Collapsed
                && VisibleHostTiles() == 2
                && HasButton(stopRootA, StartPageSurface.RestartPane));

            SetWorkbenchPaneStatusForChecks(stopB.Id, WorkbenchPaneStatus.Exited, "已退出");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("after all panes end the window stays on the workbench and offers a return to the start page",
                windowMode == MainWindowMode.Workbench
                && WorkbenchLayout.AfterAllPanesExited() == MainWindowMode.Workbench
                && WorkbenchPanel.Visibility == Visibility.Visible
                && StartPagePanel.Visibility == Visibility.Collapsed
                && BackToStartButton.IsVisible
                && Equals(BackToStartButton.Content, StartPageSurface.BackToStart));

            BackToStartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("clicking back to start returns to the start page",
                windowMode == MainWindowMode.StartPage
                && StartPagePanel.Visibility == Visibility.Visible
                && WorkbenchPanel.Visibility == Visibility.Collapsed);

            ResetWorkbenchForChecks();
            var waitPane = FakePlan(1, "等待甲");
            var pressurePane = FakePlan(2, "压力乙");
            EnterWorkbench([waitPane, pressurePane]);
            SetWorkbenchPaneStatusForChecks(waitPane.Id, WorkbenchPaneStatus.Waiting);
            SetWorkbenchPaneStatusForChecks(pressurePane.Id, WorkbenchPaneStatus.Exited, "已退出");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("Waiting and Pressure panes stay active so the return button stays hidden",
                windowMode == MainWindowMode.Workbench
                && !BackToStartButton.IsVisible
                && HasButton(PaneRoot("压力乙"), StartPageSurface.RestartPane)
                && !HasButton(PaneRoot("等待甲"), StartPageSurface.RestartPane));

            ResetWorkbenchForChecks();
            var capPlans = Enumerable.Range(1, WorkbenchLayout.MaxPanes).Select(index => FakePlan(index, "满格" + index)).ToArray();
            EnterWorkbench(capPlans);
            SetWorkbenchPaneStatusForChecks(capPlans[0].Id, WorkbenchPaneStatus.Exited, "已退出");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("AddPaneButton is disabled when six panes including an exited unclosed tile are present",
                VisibleHostTiles() == WorkbenchLayout.MaxPanes
                && !AddPaneButton.IsEnabled
                && ((AddPaneButton.ToolTip as string) ?? "").Contains(StartPageSurface.CapDenied));

            ResetWorkbenchForChecks();
            var maxA = FakePlan(1, "铺满甲");
            var maxB = FakePlan(2, "铺满乙");
            EnterWorkbench([maxA, maxB]);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var maxRoot = PaneRoot("铺满甲");
            var title = maxRoot is null ? null : ReviewFind<FrameworkElement>(maxRoot).FirstOrDefault(item => AutomationProperties.GetName(item) == "铺满甲 标题");
            if (title is not null) DoubleClick(title);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("double-click maximize shows only one tile on WorkbenchHost",
                title is not null
                && windowMode == MainWindowMode.Workbench
                && VisibleHostTiles() == 1
                && PaneRoot("铺满甲") is { IsVisible: true }
                && PaneRoot("铺满乙") is not { IsVisible: true });

            if (title is not null) DoubleClick(title);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("double-click again restores the grid",
                windowMode == MainWindowMode.Workbench
                && VisibleHostTiles() == 2
                && PaneRoot("铺满甲") is { IsVisible: true }
                && PaneRoot("铺满乙") is { IsVisible: true });
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
