#if INTEGRATION_TESTS
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunQuickActionsChecksAsync(string folder, Action<string, bool> check, Func<double, double, Task> resize)
    {
        var originalConfig = config; var originalStatus = status; var originalBook = taskBook;
        var originalSlot = selectedSlotId; var originalWidth = Width; var originalHeight = Height;
        var originalReadOnly = readOnly; var originalQueue = queueEnabled; var originalScope = queueScopeTaskIds;
        var originalSessionExpanded = SessionExpander.IsExpanded; var originalTaskExpanded = TaskExpander.IsExpanded;
        var originalEmbeddedVisibility = EmbeddedPanel.Visibility; var originalEmbeddedTab = EmbeddedTabs.SelectedItem;
        var terminalId = Guid.NewGuid();
        var wasPolling = timer.IsEnabled; var wasPumping = taskTimer.IsEnabled;
        timer.Stop(); taskTimer.Stop();
        await saveTask;
        var diskBefore = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        try
        {
            var initialWindowState = WindowState; var initiallyActive = IsActive; var initiallyVisible = IsVisible;
            var pendingAtEntry = new { Launch = !launchTask.IsCompleted, Queue = !queuePumpTask.IsCompleted, QuickQueue = !quickQueueTask.IsCompleted, Cancel = !cancelTask.IsCompleted, queuePumping, config.MinimizeAfterLaunch };
            // RaiseEvent bypasses the native visibility/activation prerequisites of a real rail click.
            // Establish them before exercising navigation; never repair focus after the click under test.
            Show(); WindowState = WindowState.Normal; Activate();
            var activation = System.Diagnostics.Stopwatch.StartNew();
            var readyForClick = false;
            while (activation.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                readyForClick = IsVisible && IsActive && WindowState == WindowState.Normal && windowHandle != IntPtr.Zero && !WindowPlacementNative.IsIconic(windowHandle);
                if (readyForClick) break;
                await Task.Delay(25);
            }
            Store.Save(Path.Combine(folder, "quick-actions-entry.json"), new { InitialWindowState = initialWindowState, InitiallyActive = initiallyActive, InitiallyVisible = initiallyVisible, Pending = pendingAtEntry, ReadyForClick = readyForClick, WindowState, IsActive, IsVisible, WaitMs = activation.ElapsedMilliseconds });
            check("UX10 navigation fixture establishes a visible active window before clicks", readyForClick);
            config = new Configuration(); config.Draft.Project = Store.Root;
            config.Draft.Slots = [new() { AgentId = "omp", Enabled = true }, new() { AgentId = "codex", Enabled = false }];
            config.Draft.Slots[0].Profiles["omp"] = new() { Arguments = ["SECRET-QUICK-ACTION-ARG"] };
            config.RecentProjects = [Store.Root]; config.Theme = "dark";
            var attemptId = Guid.NewGuid(); var queueId = Guid.NewGuid(); var taskId = Guid.NewGuid();
            status = new(true, "navigation fixture", [new() { Plan = new(attemptId, Guid.NewGuid(), 1, "接口服务", Store.Root, PhysicalDirectory.Workspace(Store.Root), false, new("cmd.exe", ["SECRET-COMMAND-ARG"], "exe"), ResourcePolicy.AgentBudget), State = "Started" }]);
            taskBook = new() { Tasks = [new() { Id = taskId, Name = "界面检查", Project = Store.Root }], Queue = [new() { Id = queueId, TaskId = taskId, Project = Store.Root, Sequence = 1 }] };
            queueEnabled = false; queueScopeTaskIds = null;
            ErrorText.Text = ""; ApplyTheme(); Render(); RenderTasks(); RenderMonitorStatus();
            var actions = BuildQuickActions();
            check("UX10 quick actions include stable identities without command arguments", actions.Select(a => a.Id).Distinct().Count() == actions.Length && actions.Any(a => a.Id == "session:" + attemptId) && actions.Any(a => a.Id == "queue:" + queueId) && actions.All(a => !a.Detail.Contains("SECRET") && !a.Title.Contains("SECRET")));
            var configuredProject = config.Draft.Project;
            ProjectPath.Text = Path.Combine(Store.Root, "unconfirmed-path");
            NavigateToSlot(config.Draft.Slots[1].Id);
            check("UX10 switching Agent preserves pending project input and selection flags", selectedSlotId == config.Draft.Slots[1].Id && ProjectPath.Text.EndsWith("unconfirmed-path") && config.Draft.Project == configuredProject && !config.Draft.Slots[1].Enabled);
            rendering = true; ProjectPath.Text = configuredProject; rendering = false;
            var revision = config.Draft.Revision;
            var terminalCard = new TerminalCard();
            var terminalTab = new TabItem { Tag = terminalId, Header = "导航焦点检查", Content = terminalCard };
            embeddedViews.Add(terminalId, new PaneView { Tab = terminalTab, Card = terminalCard, Status = new TextBlock() });
            EmbeddedPanel.Visibility = Visibility.Visible;
            EmbeddedTabs.Items.Add(terminalTab); EmbeddedTabs.SelectedItem = terminalTab;
            var sessionButton = RailSessions.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "查看会话 接口服务");
            sessionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
            var focusedElement = Keyboard.FocusedElement as FrameworkElement;
            var sessionRecord = ActiveSessions.Children.OfType<FrameworkElement>().FirstOrDefault(item => Equals(item.Tag, attemptId));
            var moreHeaders = ((FindLogical(this).OfType<Button>().First(b => Equals(b.Content, "更多") && b.IsVisible).ContextMenu as ContextMenu)?.Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray()) ?? [];
            var sessionOpened = sessionRecord != null && (ActiveNow.Visibility == Visibility.Visible || SessionExpander.IsExpanded) && moreHeaders.Contains("运行历史与导出");
            Store.Save(Path.Combine(folder, "quick-session-focus.json"), new { IsActive, IsVisible, WindowState, Expanded = SessionExpander.IsExpanded, ActiveNow = ActiveNow.Visibility, ExpectedId = attemptId, FocusedType = focusedElement?.GetType().Name, FocusedName = focusedElement?.Name, FocusedId = focusedElement?.Tag is Guid tag ? (Guid?)tag : null, FocusedLoaded = focusedElement?.IsLoaded, FocusedVisible = focusedElement?.IsVisible, LogicalFocusType = FocusManager.GetFocusedElement(this)?.GetType().Name, RecordPresent = sessionRecord != null, MoreHistory = moreHeaders.Contains("运行历史与导出") });
            if (!sessionOpened) CaptureQuickActions(this, Path.Combine(folder, "quick-session-focus-failure.png"));
            check("UX10 rail session opens and focuses the exact record", sessionOpened);
            check("UX10 session navigation reveals the record without closing the terminal", EmbeddedPanel.Visibility == Visibility.Collapsed && embeddedViews.ContainsKey(terminalId) && !embeddedViews[terminalId].Stop.IsCancellationRequested);
            CaptureQuickActions(this, Path.Combine(folder, "quick-session-navigation.png"));
            var queueButton = RailQueue.Children.OfType<Button>().Single(b => AutomationProperties.GetName(b) == "查看等待项 界面检查");
            check("UX10 paused queue rail does not claim a resource shortage", queueButton.Content is TextBlock text && text.Text.Contains("已暂停") && !text.Text.Contains("等待资源"));
            ShowEmbeddedClick(this, new());
            EmbeddedTabs.SelectedItem = null; EmbeddedTabs.SelectedItem = terminalTab;
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
            check("UX10 rail queue navigation never dispatches or edits the queue", TaskExpander.IsExpanded && QueueList.Children.OfType<FrameworkElement>().Any(item => Equals(item.Tag, queueId)) && !queueEnabled && taskBook.Queue.Count == 1 && config.Draft.Revision == revision);
            check("UX10 queue navigation reveals the record without closing the terminal", EmbeddedPanel.Visibility == Visibility.Collapsed && embeddedViews.ContainsKey(terminalId) && !embeddedViews[terminalId].Stop.IsCancellationRequested);
            ShowEmbeddedClick(this, new());
            NavigateToSlot(config.Draft.Slots[1].Id);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
            check("UX10 Agent navigation returns from the terminal without closing it", EmbeddedPanel.Visibility == Visibility.Collapsed && selectedSlotId == config.Draft.Slots[1].Id && embeddedViews.ContainsKey(terminalId) && !embeddedViews[terminalId].Stop.IsCancellationRequested);
            ShowEmbeddedClick(this, new());
            JumpTo(SessionExpander);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
            check("UX10 section navigation returns from the terminal without closing it", EmbeddedPanel.Visibility == Visibility.Collapsed && SessionExpander.IsExpanded && embeddedViews.ContainsKey(terminalId) && !embeddedViews[terminalId].Stop.IsCancellationRequested);
            readOnly = true;
            check("UX10 command availability rechecks read-only state after snapshot", !actions.Single(a => a.Id == "project").Enabled && !actions.Single(a => a.Id.StartsWith("recent:", StringComparison.Ordinal)).Enabled);
            readOnly = false;
            var recentBefore = config.RecentProjects.ToArray(); var missingRejected = false;
            try { await SwitchRecentProjectAsync(Path.Combine(Store.Root, "missing-" + Guid.NewGuid().ToString("N"))); }
            catch (IOException) { missingRejected = true; }
            check("UX10 inaccessible recent project leaves current draft and history unchanged", missingRejected && config.Draft.Project == configuredProject && config.Draft.Revision == revision && config.RecentProjects.SequenceEqual(recentBefore));

            void ExerciseDialog(QuickActionsWindow dialog, Action<QuickActionsWindow> action)
            {
                Exception? failure = null;
                dialog.Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { action(dialog); }
                    catch (Exception ex) { failure = ex; }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                }), DispatcherPriority.ApplicationIdle);
                dialog.ShowDialog();
                if (failure is not null) throw failure;
            }
            var executed = 0;
            var examples = new[]
            {
                new QuickAction("a", "查看运行记录", "接口服务 · CODEX", () => { executed++; return Task.CompletedTask; }),
                new QuickAction("b", "查看运行记录", "前端界面 · Omp", () => { executed++; return Task.CompletedTask; })
            };
            var cancelDialog = new QuickActionsWindow(this, examples);
            ExerciseDialog(cancelDialog, dialog =>
            {
                check("UX10 quick actions focus search on opening", dialog.SearchBox.IsKeyboardFocused);
                dialog.SearchBox.Text = "运行 codex";
                check("UX10 quick actions match multiple words case-insensitively", dialog.ActionList.Items.Count == 1 && dialog.ActionList.SelectedItem is QuickAction { Id: "a" });
                dialog.SearchBox.Text = "不存在的操作";
                check("UX10 unmatched search clears selection and disables activation", dialog.EmptyText.IsVisible && dialog.ActionList.SelectedIndex == -1 && !dialog.OpenButton.IsEnabled);
                dialog.SearchBox.Text = "";
                dialog.SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                check("UX10 arrow navigation keeps typing focus in search", dialog.ActionList.SelectedIndex == 1 && dialog.SearchBox.IsKeyboardFocused);
            });
            check("UX10 cancel and searching never execute actions", cancelDialog.Chosen is null && executed == 0);
            var enterDialog = new QuickActionsWindow(this, examples);
            ExerciseDialog(enterDialog, dialog => dialog.SearchBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }));
            check("UX10 Enter selects one action without executing inside modal dialog", enterDialog.Chosen?.Id == "a" && executed == 0);
            await enterDialog.Chosen!.Run();
            check("UX10 chosen action executes once after dialog closes", executed == 1);

            foreach (var scale in new[] { 100, 150 })
            {
                config.TextScalePercent = scale; ApplyTheme(); Render();
                await resize(650, 700);
                var choosePoint = ChooseProjectButton.TranslatePoint(new Point(), this);
                var moreButton = FindLogical(this).OfType<Button>().First(b => Equals(b.Content, "更多") && b.IsVisible);
                var morePoint = moreButton.TranslatePoint(new Point(), this);
                var settingsButton = FindLogical(this).OfType<Button>().First(b => Equals(b.Content, "设置") && b.IsVisible);
                var settingsPoint = settingsButton.TranslatePoint(new Point(), this);
                check($"UX10 project switching and quick actions remain reachable at {scale}% narrow layout", ChooseProjectButton.IsVisible && moreButton.IsVisible && settingsButton.IsVisible && LaunchButton.IsVisible && choosePoint.X >= -0.5 && morePoint.X >= -0.5 && settingsPoint.X >= -0.5 && choosePoint.X + ChooseProjectButton.ActualWidth <= ActualWidth + 0.5 && morePoint.X + moreButton.ActualWidth <= ActualWidth + 0.5 && settingsPoint.X + settingsButton.ActualWidth <= ActualWidth + 0.5 && ChooseProjectButton.ActualWidth > 0);
            }
            var palette = new QuickActionsWindow(this, BuildQuickActions());
            ExerciseDialog(palette, dialog =>
            {
                dialog.SearchBox.Text = "接口"; dialog.UpdateLayout();
                check("UX10 large text palette keeps search and open button visible", dialog.SearchBox.IsVisible && dialog.OpenButton.IsVisible && dialog.ActionList.ActualHeight > 80);
                check("UX10 quick actions retain dark theme and readable list foreground", dialog.Background is SolidColorBrush background && background.Color == ((SolidColorBrush)FindResource("Page")).Color && dialog.ActionList.Background is SolidColorBrush surface && surface.Color == ((SolidColorBrush)FindResource("Surface")).Color && dialog.ActionList.Foreground is SolidColorBrush foreground && foreground.Color == ((SolidColorBrush)FindResource("Ink")).Color);
                CaptureQuickActions(dialog, Path.Combine(folder, "quick-actions-150.png"));
            });
            ContentScroll.ScrollToTop(); UpdateLayout();
            CaptureQuickActions(this, Path.Combine(folder, "quick-navigation-150.png"));
            check("UX10 navigation and command search preserve persisted configuration", diskBefore is null ? !File.Exists(configPath) : File.ReadAllBytes(configPath).SequenceEqual(diskBefore));

            // A real project switch must refresh the recent-project controls in the same transaction.
            var secondProject = Path.Combine(Store.Root, "recent-navigation"); Directory.CreateDirectory(secondProject);
            await SwitchRecentProjectAsync(secondProject);
            await SwitchRecentProjectAsync(secondProject.ToUpperInvariant());
            await saveTask;
            check("UX10 recent switch immediately updates controls without casing duplicates", config.RecentProjects.Count(p => string.Equals(p, secondProject, StringComparison.OrdinalIgnoreCase)) == 1 && Recent.Items.Count == config.RecentProjects.Count && ProjectSwitchText.Text.Equals("recent-navigation", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RemoveEmbedded(terminalId);
            EmbeddedTabs.SelectedItem = originalEmbeddedTab; EmbeddedPanel.Visibility = originalEmbeddedVisibility;
            await saveTask;
            config = originalConfig; status = originalStatus; taskBook = originalBook; selectedSlotId = originalSlot;
            readOnly = originalReadOnly; queueEnabled = originalQueue; queueScopeTaskIds = originalScope;
            if (diskBefore is not null) File.WriteAllBytes(configPath, diskBefore);
            else if (File.Exists(configPath)) File.Delete(configPath);
            ApplyTheme(); Render(); RenderTasks(); RenderMonitorStatus();
            SessionExpander.IsExpanded = originalSessionExpanded; TaskExpander.IsExpanded = originalTaskExpanded;
            await resize(originalWidth, originalHeight);
            if (wasPolling) timer.Start(); if (wasPumping) taskTimer.Start();
        }
    }
    private static void CaptureQuickActions(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
#endif
