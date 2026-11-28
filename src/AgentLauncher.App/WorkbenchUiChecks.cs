#if INTEGRATION_TESTS
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunWorkbenchUiChecksAsync(Action<string, bool> check, Func<double, double, Task> resize, string folder)
    {
        await saveTask;
        await commandCheckTask;
        var previous = config;
        var previousStatus = status;
        var previousReadOnly = readOnly;
        var previousError = ErrorText.Text;
        var previousTimer = timer.IsEnabled;
        var previousWidth = Width;
        var previousHeight = Height;
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
                        AgentId = "codex",
                        Enabled = index < 6,
                        Profiles = new() { ["codex"] = new() { Executable = System.IO.Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe") } }
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

            var chips = ReviewFind<ToggleButton>(AgentChipPanel).ToArray();
            var firstChip = chips.FirstOrDefault();
            check("start page tab order is project, chips, launch, then settings",
                ChooseProjectButton.TabIndex == 10
                && firstChip is not null
                && KeyboardNavigation.GetTabIndex(firstChip) == 20
                && KeyboardNavigation.GetTabIndex(chips[^1]) == 20 + chips.Length - 1
                && LaunchButton.TabIndex == 80
                && LaunchButton.IsDefault
                && StartPageSettingsButton.TabIndex == 90
                && ChooseProjectButton.TabIndex < KeyboardNavigation.GetTabIndex(firstChip)
                && KeyboardNavigation.GetTabIndex(chips[^1]) < LaunchButton.TabIndex
                && LaunchButton.TabIndex < StartPageSettingsButton.TabIndex);

            var toggleId = config.Draft.Slots.First(slot => slot.Enabled && IsSlotReady(slot)).Id;
            var seventhId = config.Draft.Slots[6].Id;
            ToggleButton ChipFor(Guid id) => ReviewFind<ToggleButton>(AgentChipPanel).Single(button => Equals(button.Tag, id));
            Slot SlotFor(Guid id) => config.Draft.Slots.Single(slot => slot.Id == id);
            await FocusElementForChecksAsync(ChipFor(toggleId));
            var spaceOff = RaiseWindowPreviewKeyDownForChecks(Key.Space, ModifierKeys.None);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var toggledOff = spaceOff.Handled && !SlotFor(toggleId).Enabled && ChipFor(toggleId).IsChecked != true;
            await FocusElementForChecksAsync(ChipFor(toggleId));
            var spaceOn = RaiseWindowPreviewKeyDownForChecks(Key.Space, ModifierKeys.None);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("window PreviewKeyDown Space toggles the current agent chip when selection is allowed",
                spaceOn.Handled && toggledOff && SlotFor(toggleId).Enabled && ChipFor(toggleId).IsChecked == true);

            await FocusElementForChecksAsync(ChipFor(seventhId));
            var spaceSeventh = RaiseWindowPreviewKeyDownForChecks(Key.Space, ModifierKeys.None);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("window PreviewKeyDown Space does not select the seventh ready agent",
                spaceSeventh.Handled && !SlotFor(seventhId).Enabled && ChipFor(seventhId).IsChecked != true && CountReadySelected() == WorkbenchLayout.MaxPanes);

            await commandCheckTask;
            await saveTask;
            UpdateLaunch();
            check("start-page launch button is enabled and still the default button", LaunchButton.IsEnabled && LaunchButton.IsDefault);
            suppressLaunchForChecks = true;
            startPageLaunchClicksForChecks = 0;
            try
            {
                await FocusElementForChecksAsync(LaunchButton);
                var enterStart = RaiseWindowPreviewKeyDownForChecks(Key.Enter, ModifierKeys.None);
                check("window PreviewKeyDown Enter on the start page calls launch and stays on the start page",
                    enterStart.Handled
                    && startPageLaunchClicksForChecks == 1
                    && windowMode == MainWindowMode.StartPage
                    && LaunchButton.IsDefault);
            }
            finally
            {
                suppressLaunchForChecks = false;
            }

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

            var again = FakePlan(1, "再次启动");
            EnterWorkbench([again]);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("returning to the start page and launching again shows only the new tiles",
                windowMode == MainWindowMode.Workbench
                && VisibleHostTiles() == 1
                && PaneRoot("再次启动") is { IsVisible: true }
                && PaneRoot("停止甲") is null
                && PaneRoot("停止乙") is null);

            ResetWorkbenchForChecks();
            var waitPane = FakePlan(1, "等待甲");
            var pressurePane = FakePlan(2, "压力乙");
            var endedPane = FakePlan(3, "退出丙");
            EnterWorkbench([waitPane, pressurePane, endedPane]);
            SetWorkbenchPaneStatusForChecks(waitPane.Id, WorkbenchPaneStatus.Waiting);
            SetWorkbenchPaneStatusForChecks(pressurePane.Id, WorkbenchPaneStatus.Pressure);
            SetWorkbenchPaneStatusForChecks(endedPane.Id, WorkbenchPaneStatus.Exited, "已退出");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("Waiting and Pressure panes stay active so the return button stays hidden",
                windowMode == MainWindowMode.Workbench
                && !BackToStartButton.IsVisible
                && HasButton(PaneRoot("退出丙"), StartPageSurface.RestartPane)
                && !HasButton(PaneRoot("等待甲"), StartPageSurface.RestartPane)
                && !HasButton(PaneRoot("压力乙"), StartPageSurface.RestartPane));

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

            ResetWorkbenchForChecks();
            var live = FakePlan(1, "停后同步");
            EnterWorkbench([live]);
            var livePane = workbenchPanes.Single(item => item.Id == live.Id);
            StopWorkbenchPane(livePane);
            status = new Response(true, "workbench-ui", [new ControlEntry { Plan = live, State = "Started", Detail = "已启动进程" }], status?.Memory);
            SyncWorkbenchPanesFromStatus();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("a user-stopped pane stays exited when the monitor still reports it running",
                windowMode == MainWindowMode.Workbench
                && livePane.Status == WorkbenchPaneStatus.Exited
                && HasButton(PaneRoot("停后同步"), StartPageSurface.RestartPane)
                && !HasButton(PaneRoot("停后同步"), "停止"));

            ResetWorkbenchForChecks();
            var original = FakePlan(1, "原格");
            EnterWorkbench([original]);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var originalPane = workbenchPanes.Single(item => item.Id == original.Id);
            var originalRoot = originalPane.Root;
            var replacement = FakePlan(2, "新格");
            var oneRestartThrew = false;
            try
            {
                OpenEmbeddedForSubmit(replacement, originalPane, connect: false);
                PresentSubmittedPanes([replacement], originalPane);
                RelayoutWorkbench();
            }
            catch (ArgumentOutOfRangeException) { oneRestartThrew = true; }
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("restarting a pane replaces the same tile",
                !oneRestartThrew
                && windowMode == MainWindowMode.Workbench
                && VisibleHostTiles() == 1
                && workbenchPanes.Count == 1
                && workbenchPanes.Count(item => item.Id == replacement.Id) == 1
                && ReferenceEquals(workbenchPanes[0].Root, originalRoot)
                && workbenchPanes[0].Id == replacement.Id
                && workbenchPanes[0].Plan.Name == "新格");

            ResetWorkbenchForChecks();
            var fullPlans = Enumerable.Range(1, WorkbenchLayout.MaxPanes).Select(index => FakePlan(index, "重开满" + index)).ToArray();
            EnterWorkbench(fullPlans);
            SetWorkbenchPaneStatusForChecks(fullPlans[0].Id, WorkbenchPaneStatus.Exited, "已退出");
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            var fullTarget = workbenchPanes.Single(item => item.Id == fullPlans[0].Id);
            var fullRoot = fullTarget.Root;
            var fullReplacement = FakePlan(10, "重开满新");
            var sixRestartThrew = false;
            try
            {
                OpenEmbeddedForSubmit(fullReplacement, fullTarget, connect: false);
                PresentSubmittedPanes([fullReplacement], fullTarget);
                RelayoutWorkbench();
            }
            catch (ArgumentOutOfRangeException) { sixRestartThrew = true; }
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("restarting one of six panes keeps six tiles and does not overflow the layout",
                !sixRestartThrew
                && windowMode == MainWindowMode.Workbench
                && workbenchPanes.Count == WorkbenchLayout.MaxPanes
                && VisibleHostTiles() == WorkbenchLayout.MaxPanes
                && workbenchPanes.Count(item => item.Id == fullReplacement.Id) == 1
                && ReferenceEquals(workbenchPanes.Single(item => item.Id == fullReplacement.Id).Root, fullRoot)
                && PaneRoot("重开满2") is { IsVisible: true });

            ResetWorkbenchForChecks();
            var firstAgent = FakePlan(1, "同剂甲") with { SourceSlotId = Guid.NewGuid(), AgentId = "omp", Cwd = System.IO.Path.Combine(Store.Root, "one") };
            var secondAgent = FakePlan(2, "同剂乙") with { SourceSlotId = Guid.NewGuid(), AgentId = "omp", Cwd = System.IO.Path.Combine(Store.Root, "two") };
            EnterWorkbench([firstAgent, secondAgent]);
            SetWorkbenchPaneStatusForChecks(firstAgent.Id, WorkbenchPaneStatus.Exited, "已退出");
            SetWorkbenchPaneStatusForChecks(secondAgent.Id, WorkbenchPaneStatus.Exited, "已退出");
            var secondSlot = new Slot { Id = secondAgent.SourceSlotId!.Value, AgentId = "omp", Directory = secondAgent.Cwd };
            check("add-or-restart prefers SourceSlotId over a shared AgentId",
                FindExitedWorkbenchPane(secondSlot)?.Id == secondAgent.Id);

            ResetWorkbenchForChecks();
            var baseDirectory = System.IO.Path.Combine(Store.Root, "mapped-root");
            var remapped = FakePlan(1, "映射甲") with { SourceSlotId = Guid.NewGuid(), AgentId = "pi", Cwd = System.IO.Path.Combine(baseDirectory, "worktree") };
            var otherMapped = FakePlan(2, "映射乙") with { SourceSlotId = Guid.NewGuid(), AgentId = "pi", Cwd = System.IO.Path.Combine(Store.Root, "elsewhere") };
            EnterWorkbench([remapped, otherMapped]);
            SetWorkbenchPaneStatusForChecks(remapped.Id, WorkbenchPaneStatus.Exited, "已退出");
            SetWorkbenchPaneStatusForChecks(otherMapped.Id, WorkbenchPaneStatus.Exited, "已退出");
            var mappedSlot = new Slot { Id = Guid.NewGuid(), AgentId = "pi", Directory = baseDirectory };
            check("add-or-restart AgentId fallback matches a remapped Cwd under the slot directory",
                FindExitedWorkbenchPane(mappedSlot)?.Id == remapped.Id);

            workbenchLaunchFailureForChecks = "内存不足";
            await LaunchSlotsOnWorkbenchAsync([config.Draft.Slots[0]]);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("an add-pane admission failure stays on the workbench",
                windowMode == MainWindowMode.Workbench
                && WorkbenchPanel.Visibility == Visibility.Visible
                && StartPagePanel.Visibility == Visibility.Collapsed
                && ErrorText.Text.Contains("内存不足"));

            foreach (var count in new[] { 1, 2, 4, WorkbenchLayout.MaxPanes })
            {
                ResetWorkbenchForChecks();
                var sized = Enumerable.Range(1, count).Select(index => FakePlan(index, "规格" + count + "-" + index)).ToArray();
                EnterWorkbench(sized);
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                check("workbench shows " + count + " real panes instead of a preview",
                    windowMode == MainWindowMode.Workbench
                    && VisibleHostTiles() == count
                    && ReviewFind<TerminalCard>(WorkbenchHost).Count() == count
                    && PreviewCard.Visibility != Visibility.Visible
                    && !StartPagePanel.IsVisible);
            }

            ResetWorkbenchForChecks();
            var focusA = FakePlan(1, "焦点甲");
            var focusB = FakePlan(2, "焦点乙");
            EnterWorkbench([focusA, focusB]);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("entering the workbench clears LaunchButton.IsDefault", !LaunchButton.IsDefault);
            var first = workbenchPanes[0];
            var second = workbenchPanes[1];
            FocusWorkbenchPane(first);
            await FocusElementForChecksAsync(first.Card);
            check("Ctrl+Tab is raised with a TerminalCard focused", Keyboard.FocusedElement is TerminalCard);
            var tab = RaiseWindowPreviewKeyDownForChecks(Key.Tab, ModifierKeys.Control);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("window PreviewKeyDown Ctrl+Tab moves focus from the first pane to the second",
                tab.Handled
                && focusedPaneId == second.Id
                && ReferenceEquals(Keyboard.FocusedElement, second.Card)
                && second.Root.BorderThickness == new Thickness(2)
                && first.Root.BorderThickness == new Thickness(1));
            var shiftTab = RaiseWindowPreviewKeyDownForChecks(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("window PreviewKeyDown Ctrl+Shift+Tab moves focus back to the first pane",
                shiftTab.Handled
                && focusedPaneId == first.Id
                && ReferenceEquals(Keyboard.FocusedElement, first.Card)
                && first.Root.BorderThickness == new Thickness(2)
                && second.Root.BorderThickness == new Thickness(1));
            startPageLaunchClicksForChecks = 0;
            var enterWorkbench = RaiseWindowPreviewKeyDownForChecks(Key.Enter, ModifierKeys.None);
            check("Enter on the workbench does not launch and IsDefault stays false",
                !enterWorkbench.Handled
                && windowMode == MainWindowMode.Workbench
                && startPageLaunchClicksForChecks == 0
                && !LaunchButton.IsDefault);
            var focusTitle = ReviewFind<FrameworkElement>(first.Root).FirstOrDefault(item => AutomationProperties.GetName(item) == "焦点甲 标题");
            if (focusTitle is not null) DoubleClick(focusTitle);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("double-click maximize hides the unfocused sibling",
                focusTitle is not null
                && first.Root.Visibility == Visibility.Visible
                && second.Root.Visibility == Visibility.Collapsed);
            await FocusElementForChecksAsync(first.Card);
            var maximizedTab = RaiseWindowPreviewKeyDownForChecks(Key.Tab, ModifierKeys.Control);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("Ctrl+Tab while maximized does not move focus to a collapsed sibling",
                maximizedTab.Handled
                && focusedPaneId == first.Id
                && ReferenceEquals(Keyboard.FocusedElement, first.Card)
                && second.Root.Visibility == Visibility.Collapsed);
            if (focusTitle is not null) DoubleClick(focusTitle);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);

            ResetWorkbenchForChecks();
            var narrowPlans = Enumerable.Range(1, 3).Select(index => FakePlan(index, "窄列" + index)).ToArray();
            EnterWorkbench(narrowPlans);
            Width = 700;
            Height = 560;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            var settled = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                RelayoutWorkbench();
                var ready = Math.Abs(ActualWidth - Width) < 1 && Math.Abs(ActualHeight - Height) < 1
                    && WorkbenchPanel.IsMeasureValid && WorkbenchScroll.IsMeasureValid;
                settled = ready ? settled + 1 : 0;
                if (settled >= 2) break;
                await Task.Delay(25);
            }
            var viewportWidth = WorkbenchScroll.ViewportWidth > 0 ? WorkbenchScroll.ViewportWidth : WorkbenchHost.ActualWidth;
            var viewportHeight = WorkbenchScroll.ViewportHeight > 0 ? WorkbenchScroll.ViewportHeight : WorkbenchHost.ActualHeight;
            var narrowLayout = WorkbenchLayout.Layout(viewportWidth, viewportHeight, 3);
            var narrowRoot = PaneRoot("窄列1");
            var stopVisible = narrowRoot is not null && ReviewFind<Button>(narrowRoot).Any(button => Equals(button.Content, "停止") && button.IsVisible && button.ActualHeight > 0);
            var titleVisible = narrowRoot is not null && ReviewFind<FrameworkElement>(narrowRoot).Any(item => AutomationProperties.GetName(item) == "窄列1 标题" && item.IsVisible && item.ActualHeight > 0);
            check("a narrow workbench stacks three panes in a scrollable single column",
                WorkbenchLayout.UseSingleColumn(viewportWidth, 3)
                && narrowLayout.SingleColumn
                && VisibleHostTiles() == 3
                && WorkbenchHost.Height > WorkbenchScroll.ViewportHeight
                && WorkbenchScroll.ExtentHeight > WorkbenchScroll.ViewportHeight
                && WorkbenchScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
                && stopVisible
                && titleVisible);
            check("narrow tiles all sit at X=0 with increasing Y",
                narrowLayout.Tiles.Count >= 3
                && narrowLayout.Tiles.All(tile => tile.X == 0)
                && narrowLayout.Tiles[0].Y < narrowLayout.Tiles[1].Y
                && narrowLayout.Tiles[1].Y < narrowLayout.Tiles[2].Y
                && workbenchPanes.Take(3).All(pane => Canvas.GetLeft(pane.Root) == 0)
                && Canvas.GetTop(workbenchPanes[0].Root) < Canvas.GetTop(workbenchPanes[1].Root)
                && Canvas.GetTop(workbenchPanes[1].Root) < Canvas.GetTop(workbenchPanes[2].Root));
            if (!string.IsNullOrWhiteSpace(folder))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var bitmap = new RenderTargetBitmap((int)(ActualWidth * dpi.DpiScaleX), (int)(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(folder, "native-narrow-workbench.png"));
                encoder.Save(output);
            }
            var narrowTitle = narrowRoot is null ? null : ReviewFind<FrameworkElement>(narrowRoot).FirstOrDefault(item => AutomationProperties.GetName(item) == "窄列1 标题");
            if (narrowTitle is not null)
            {
                narrowTitle.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                    Source = narrowTitle
                });
            }
            var narrowStopButton = narrowRoot is null ? null : ReviewFind<Button>(narrowRoot).FirstOrDefault(button => Equals(button.Content, "停止") && button.IsVisible);
            if (narrowStopButton is not null) narrowStopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("narrow column title and Stop remain clickable without leaving the workbench",
                narrowTitle is not null
                && narrowStopButton is not null
                && windowMode == MainWindowMode.Workbench
                && focusedPaneId == workbenchPanes[0].Id
                && HasButton(PaneRoot("窄列1"), StartPageSurface.RestartPane)
                && !HasButton(PaneRoot("窄列1"), "停止"));
            ResetWorkbenchForChecks();
            var slashRoot = System.IO.Path.Combine(Store.Root, "slash-root");
            var slashPlan = FakePlan(1, "斜杠甲") with { SourceSlotId = Guid.NewGuid(), AgentId = "slash", Cwd = slashRoot };
            EnterWorkbench([slashPlan]);
            SetWorkbenchPaneStatusForChecks(slashPlan.Id, WorkbenchPaneStatus.Exited, "已退出");
            var slashSlot = new Slot { Id = Guid.NewGuid(), AgentId = "slash", Directory = slashRoot + System.IO.Path.DirectorySeparatorChar };
            check("AgentId fallback treats a trailing slash as the same directory",
                FindExitedWorkbenchPane(slashSlot)?.Id == slashPlan.Id);
        }
        finally
        {
            ResetWorkbenchForChecks();
            Width = previousWidth;
            Height = previousHeight;
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
