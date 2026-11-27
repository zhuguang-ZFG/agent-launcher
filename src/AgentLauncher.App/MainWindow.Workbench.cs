using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private sealed class WorkbenchPaneView
    {
        public required Guid Id;
        public required LaunchItem Plan;
        public required Border Root;
        public required TerminalCard Card;
        public WorkbenchPaneStatus Status = WorkbenchPaneStatus.Starting;
        public string ExitReason = "";
        public required FrameworkElement TitleBar;
        public required TextBlock TitleText;
        public required TextBlock PlaceText;
        public required Ellipse StatusDot;
        public required Button StopButton;
        public required Button MenuButton;
        public required Button RestartButton;
        public required TextBlock ExitReasonText;
        public required FrameworkElement ExitOverlay;
        public required Grid Body;
        public UIElement? TerminalContent;
    }

    private MainWindowMode windowMode = MainWindowMode.StartPage;
    private readonly Dictionary<Guid, (bool Ready, string Reason)> chipStatus = [];
    private readonly List<WorkbenchPaneView> workbenchPanes = [];
    private bool capDeniedSticky;
    private Guid? maximizedPaneId;
    private Guid? focusedPaneId;
    private readonly HashSet<Guid> userStoppedPaneIds = [];

    private void ApplyWindowMode()
    {
        var workbench = windowMode == MainWindowMode.Workbench;
        StartPagePanel.Visibility = workbench ? Visibility.Collapsed : Visibility.Visible;
        WorkbenchPanel.Visibility = workbench ? Visibility.Visible : Visibility.Collapsed;
        LegacyWorkspace.Visibility = Visibility.Collapsed;
        Rail.Visibility = Visibility.Collapsed;
        PreviewCard.Visibility = Visibility.Collapsed;
        Preview.Visibility = Visibility.Collapsed;
        RefreshLaunchButton.Visibility = Visibility.Collapsed;
        LaunchOptionsExpander.Visibility = Visibility.Collapsed;
        ConfigurationToolsExpander.Visibility = Visibility.Collapsed;
        SessionExpander.Visibility = Visibility.Collapsed;
        QueueQuickButton.Visibility = Visibility.Collapsed;
        EmbeddedPanel.Visibility = Visibility.Collapsed;
        ShowTerminalsButton.Visibility = Visibility.Collapsed;
        if (!workbench) ChooseProjectButton.Visibility = Visibility.Visible;
        if (LaunchButton is not null) LaunchButton.IsDefault = !workbench;
        UpdateWorkbenchChrome();
        if (workbench) RelayoutWorkbench();
    }

    private void PresentStartedPlans(IReadOnlyList<LaunchItem> started)
    {
        if (started.Count == 0) return;
        if (windowMode == MainWindowMode.StartPage) EnterWorkbench(started);
        else
        {
            foreach (var plan in started)
            {
                if (!embeddedViews.ContainsKey(plan.Id))
                {
                    var marker = Store.FilePath(System.IO.Path.Combine("attempts", plan.Id.ToString("N"), "embedded.json"));
                    OpenEmbedded(plan, EmbeddedHostAlive(marker));
                }
                AttachWorkbenchPane(plan);
            }
            RelayoutWorkbench();
            UpdateWorkbenchChrome();
        }
    }

    private void CompleteLaunchSurface(IReadOnlyList<LaunchItem> started, int requested, string? reason = null)
    {
        if (started.Count >= 1)
        {
            PresentStartedPlans(started);
            ShowPartialLaunchNotice(started.Count, Math.Max(requested, started.Count), string.IsNullOrWhiteSpace(reason) ? "未能全部启动" : reason);
            return;
        }
        ErrorText.Text = string.IsNullOrWhiteSpace(reason) ? "启动失败：没有启动任何 Agent" : reason;
        UpdateLaunch();
    }

    private void EnterWorkbench(IReadOnlyList<ControlEntry> entries)
    {
        EnterWorkbench(entries.Select(entry => entry.Plan).ToList());
        foreach (var entry in entries)
        {
            var pane = workbenchPanes.FirstOrDefault(item => item.Id == entry.Plan.Id);
            if (pane is not null) SetWorkbenchPaneStatus(pane, MapControlEntryStatus(entry), entry.Detail);
        }
    }

    private void EnterWorkbench(IReadOnlyList<LaunchItem> started)
    {
        var fromStart = windowMode == MainWindowMode.StartPage;
        if (fromStart) DetachStaleWorkbenchTiles(started);
        windowMode = WorkbenchLayout.AfterLaunchAttempt(started.Count);
        ApplyWindowMode();
        if (windowMode != MainWindowMode.Workbench) return;
        if (WorkbenchNotice is not null) WorkbenchNotice.Text = "";
        if (fromStart) FadeIntoWorkbench();
        foreach (var plan in started)
        {
            var marker = Store.FilePath(System.IO.Path.Combine("attempts", plan.Id.ToString("N"), "embedded.json"));
            OpenEmbedded(plan, EmbeddedHostAlive(marker));
            AttachWorkbenchPane(plan);
        }
        RelayoutWorkbench();
        UpdateWorkbenchChrome();
    }

    private void AttachWorkbenchPane(LaunchItem plan, WorkbenchPaneStatus status = WorkbenchPaneStatus.Starting)
    {
        if (workbenchPanes.Any(pane => pane.Id == plan.Id)) return;
        if (!embeddedViews.ContainsKey(plan.Id)) OpenEmbedded(plan, false);
        if (!embeddedViews.TryGetValue(plan.Id, out var view)) return;
        var pane = CreateWorkbenchPane(plan, view, status);
        WorkbenchHost.Children.Add(pane.Root);
        workbenchPanes.Add(pane);
        EmbeddedPanel.Visibility = Visibility.Collapsed;
        ShowTerminalsButton.Visibility = Visibility.Collapsed;
        RefreshPaneChrome();
        ApplyPaneFocusBorders();
    }

    private void ReplaceWorkbenchPane(WorkbenchPaneView pane, LaunchItem plan)
    {
        userStoppedPaneIds.Remove(pane.Id);
        if (pane.Id != plan.Id) RemoveEmbedded(pane.Id, detachPane: false);
        if (!embeddedViews.ContainsKey(plan.Id))
        {
            var marker = Store.FilePath(System.IO.Path.Combine("attempts", plan.Id.ToString("N"), "embedded.json"));
            OpenEmbedded(plan, EmbeddedHostAlive(marker), attachPane: false);
        }
        if (!embeddedViews.TryGetValue(plan.Id, out var view)) return;
        if (pane.TerminalContent is not null) pane.Body.Children.Remove(pane.TerminalContent);
        pane.Id = plan.Id;
        pane.Plan = plan;
        pane.Card = view.Card;
        pane.TerminalContent = TakeEmbeddedContent(view);
        if (pane.TerminalContent is not null) pane.Body.Children.Insert(0, pane.TerminalContent);
        AutomationProperties.SetName(pane.Root, plan.Name + " 窗格");
        AutomationProperties.SetName(pane.TitleBar, plan.Name + " 标题");
        SetWorkbenchPaneStatus(pane, WorkbenchPaneStatus.Starting, "");
        RefreshPaneChrome();
        RelayoutWorkbench();
        UpdateWorkbenchChrome();
    }

    private UIElement? TakeEmbeddedContent(PaneView view)
    {
        var content = view.Tab.Content as UIElement;
        view.Tab.Content = null;
        return content;
    }

    private WorkbenchPaneView CreateWorkbenchPane(LaunchItem plan, PaneView view, WorkbenchPaneStatus status)
    {
        var titleText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0)
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var placeText = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        placeText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var statusDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var menuButton = new Button { Content = "⋯", Style = (Style)FindResource("QuietButton"), Padding = new Thickness(6, 0, 6, 0), MinHeight = 22, MinWidth = 24, FontSize = 12, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(menuButton, plan.Name + " 菜单");
        var stopButton = new Button { Content = "停止", Style = (Style)FindResource("QuietButton"), Padding = new Thickness(6, 0, 6, 0), MinHeight = 22, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(stopButton, plan.Name + " 停止");
        var titleDock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(stopButton, Dock.Right);
        DockPanel.SetDock(menuButton, Dock.Right);
        DockPanel.SetDock(statusDot, Dock.Right);
        DockPanel.SetDock(placeText, Dock.Right);
        titleDock.Children.Add(stopButton);
        titleDock.Children.Add(menuButton);
        titleDock.Children.Add(statusDot);
        titleDock.Children.Add(placeText);
        titleDock.Children.Add(titleText);
        var titleBar = new ContentControl { Height = 30, Content = titleDock, Focusable = false };
        AutomationProperties.SetName(titleBar, plan.Name + " 标题");
        titleBar.SetResourceReference(Control.BackgroundProperty, "HeaderSurface");
        var exitReason = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        exitReason.SetResourceReference(TextBlock.ForegroundProperty, "WarningInk");
        var restartButton = new Button { Content = StartPageSurface.RestartPane, Style = (Style)FindResource("QuietButton"), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(restartButton, plan.Name + " 重新启动");
        var overlay = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16),
            Visibility = Visibility.Collapsed
        };
        overlay.Children.Add(exitReason);
        overlay.Children.Add(restartButton);
        var body = new Grid();
        var content = TakeEmbeddedContent(view);
        if (content is not null) body.Children.Add(content);
        body.Children.Add(overlay);
        var shell = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        shell.Children.Add(titleBar);
        shell.Children.Add(body);
        var root = new Border
        {
            Child = shell,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            ClipToBounds = true
        };
        AutomationProperties.SetName(root, plan.Name + " 窗格");
        root.SetResourceReference(Border.BackgroundProperty, "Surface");
        root.SetResourceReference(Border.BorderBrushProperty, "Line");
        var pane = new WorkbenchPaneView
        {
            Id = plan.Id,
            Plan = plan,
            Root = root,
            Card = view.Card,
            Status = status,
            TitleBar = titleBar,
            TitleText = titleText,
            PlaceText = placeText,
            StatusDot = statusDot,
            StopButton = stopButton,
            MenuButton = menuButton,
            RestartButton = restartButton,
            ExitReasonText = exitReason,
            ExitOverlay = overlay,
            Body = body,
            TerminalContent = content
        };
        titleBar.MouseDoubleClick += (_, _) => ToggleMaximizeWorkbenchPane(pane);
        titleBar.MouseLeftButtonDown += (_, _) => FocusWorkbenchPane(pane);
        root.PreviewMouseDown += (_, _) => FocusWorkbenchPane(pane);
        stopButton.Click += (_, _) => StopWorkbenchPane(pane);
        menuButton.Click += (_, _) => OpenPaneMenu(pane, menuButton);
        restartButton.Click += (_, _) => _ = GuardAsync(() => RestartWorkbenchPaneAsync(pane));
        SetWorkbenchPaneStatus(pane, status, pane.ExitReason);
        return pane;
    }

    private void DetachWorkbenchPane(Guid id)
    {
        var pane = workbenchPanes.FirstOrDefault(item => item.Id == id);
        if (pane is null) return;
        workbenchPanes.Remove(pane);
        WorkbenchHost.Children.Remove(pane.Root);
        if (maximizedPaneId == id) maximizedPaneId = null;
        if (focusedPaneId == id) focusedPaneId = workbenchPanes.FirstOrDefault()?.Id;
        if (windowMode == MainWindowMode.Workbench) RelayoutWorkbench();
        UpdateWorkbenchChrome();
    }

    private void RelayoutWorkbench()
    {
        if (WorkbenchHost is null || windowMode != MainWindowMode.Workbench) return;
        var width = Math.Max(WorkbenchScroll?.ViewportWidth > 0 ? WorkbenchScroll.ViewportWidth : WorkbenchHost.ActualWidth, 1);
        var height = Math.Max(WorkbenchScroll?.ViewportHeight > 0 ? WorkbenchScroll.ViewportHeight : WorkbenchHost.ActualHeight, 1);
        if (maximizedPaneId is { } maxId && workbenchPanes.FirstOrDefault(item => item.Id == maxId) is { } maximized)
        {
            WorkbenchHost.Width = width;
            WorkbenchHost.Height = height;
            foreach (var view in workbenchPanes)
            {
                if (view.Id == maxId)
                {
                    view.Root.Visibility = Visibility.Visible;
                    Canvas.SetLeft(view.Root, 0);
                    Canvas.SetTop(view.Root, 0);
                    view.Root.Width = width;
                    view.Root.Height = height;
                }
                else view.Root.Visibility = Visibility.Collapsed;
            }
            return;
        }
        foreach (var view in workbenchPanes) view.Root.Visibility = Visibility.Visible;
        var layout = WorkbenchLayout.Layout(width, height, workbenchPanes.Count);
        var contentHeight = layout.Tiles.Count == 0 ? height : layout.Tiles[^1].Y + layout.Tiles[^1].Height;
        WorkbenchHost.Width = width;
        WorkbenchHost.Height = Math.Max(contentHeight, height);
        for (var i = 0; i < workbenchPanes.Count; i++)
        {
            var tile = layout.Tiles[i];
            var view = workbenchPanes[i];
            Canvas.SetLeft(view.Root, tile.X);
            Canvas.SetTop(view.Root, tile.Y);
            view.Root.Width = tile.Width;
            view.Root.Height = tile.Height;
        }
    }

    private void WorkbenchHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (windowMode == MainWindowMode.Workbench) RelayoutWorkbench();
    }

    private void FadeIntoWorkbench()
    {
        WorkbenchPanel.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => WorkbenchPanel.Opacity = 1;
        WorkbenchPanel.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateWorkbenchChrome()
    {
        if (WorkbenchProjectTitle is null) return;
        var project = config.Draft.Project;
        WorkbenchProjectTitle.Text = string.IsNullOrWhiteSpace(project) ? "未选择项目" : ProjectName(project);
        WorkbenchProjectTitle.ToolTip = string.IsNullOrWhiteSpace(project) ? null : project;
        WorkbenchCapacity.Text = "运行中 " + CountActiveWorkbenchPanes() + " / 最多 " + WorkbenchLayout.MaxPanes;
        WorkbenchResource.Text = status?.Memory is { } sample
            ? $"{RailResourceMode(config.Draft.Resource?.Mode)} · 可用 {GiB(sample.Available)} GiB"
            : "资源：未读取";
        BackToStartButton.Content = StartPageSurface.BackToStart;
        BackToStartButton.Visibility = WorkbenchLayout.ShowBackToStart(windowMode, CountActiveWorkbenchPanes()) ? Visibility.Visible : Visibility.Collapsed;
        var full = workbenchPanes.Count >= WorkbenchLayout.MaxPanes;
        AddPaneButton.IsEnabled = windowMode == MainWindowMode.Workbench && !full;
        AddPaneButton.ToolTip = full ? StartPageSurface.CapDenied : "添加窗格";
        RefreshPaneChrome();
    }

    private int CountActiveWorkbenchPanes() =>
        workbenchPanes.Count(pane => pane.Status is WorkbenchPaneStatus.Starting or WorkbenchPaneStatus.Running or WorkbenchPaneStatus.Waiting or WorkbenchPaneStatus.Pressure);

    private static bool IsActiveWorkbenchStatus(WorkbenchPaneStatus status) =>
        status is WorkbenchPaneStatus.Starting or WorkbenchPaneStatus.Running or WorkbenchPaneStatus.Waiting or WorkbenchPaneStatus.Pressure;

    private void ClearWorkbenchTiles() => DetachStaleWorkbenchTiles([]);

    private void DetachStaleWorkbenchTiles(IReadOnlyList<LaunchItem> keep)
    {
        maximizedPaneId = null;
        focusedPaneId = null;
        var keepIds = keep.Select(plan => plan.Id).ToHashSet();
        userStoppedPaneIds.RemoveWhere(id => !keepIds.Contains(id));
        foreach (var id in workbenchPanes.Select(pane => pane.Id).Concat(embeddedViews.Keys).Distinct().ToArray())
        {
            if (!keepIds.Contains(id)) RemoveEmbedded(id);
        }
        workbenchPanes.RemoveAll(pane => !keepIds.Contains(pane.Id));
        if (WorkbenchHost is not null)
        {
            foreach (var child in WorkbenchHost.Children.OfType<UIElement>().ToArray())
            {
                if (!workbenchPanes.Any(pane => ReferenceEquals(pane.Root, child)))
                    WorkbenchHost.Children.Remove(child);
            }
        }
        if (WorkbenchNotice is not null) WorkbenchNotice.Text = "";
    }

    private void BackToStartClick(object sender, RoutedEventArgs e)
    {
        workbenchLaunchFailureForChecks = null;
        ClearWorkbenchTiles();
        windowMode = MainWindowMode.StartPage;
        ApplyWindowMode();
    }

    private void AddPaneClick(object sender, RoutedEventArgs e)
    {
        if (windowMode != MainWindowMode.Workbench) return;
        if (workbenchPanes.Count >= WorkbenchLayout.MaxPanes)
        {
            AddPaneButton.IsEnabled = false;
            AddPaneButton.ToolTip = StartPageSurface.CapDenied;
            ErrorText.Text = StartPageSurface.CapDenied;
            UpdateWorkbenchChrome();
            return;
        }
        var menu = new ContextMenu();
        foreach (var slot in AddableWorkbenchSlots())
        {
            var item = new MenuItem { Header = slot.DisplayName, Tag = slot.Id };
            item.Click += (_, _) => _ = GuardAsync(() => AddOrRestartWorkbenchSlotAsync(slot));
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            ErrorText.Text = "没有可添加的 Agent。请先在起步页配置可启动的项目 Agent。";
            return;
        }
        menu.PlacementTarget = AddPaneButton;
        menu.Placement = PlacementMode.Bottom;
        AddPaneButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private IEnumerable<Slot> AddableWorkbenchSlots()
    {
        var running = workbenchPanes.Where(pane => IsActiveWorkbenchStatus(pane.Status)).ToArray();
        var runningSlots = running.Select(pane => pane.Plan.SourceSlotId).OfType<Guid>().ToHashSet();
        foreach (var slot in config.Draft.Slots)
        {
            if (!IsSlotReady(slot)) continue;
            if (runningSlots.Contains(slot.Id)) continue;
            var directory = string.IsNullOrWhiteSpace(slot.Directory) ? config.Draft.Project : slot.Directory;
            if (running.Any(pane => string.Equals(pane.Plan.AgentId, slot.AgentId, StringComparison.Ordinal) && MatchesSlotDirectory(pane.Plan, directory))) continue;
            yield return slot;
        }
    }

    private WorkbenchPaneView? FindExitedWorkbenchPane(Slot slot)
    {
        var bySlot = workbenchPanes.Where(pane => !IsActiveWorkbenchStatus(pane.Status) && pane.Plan.SourceSlotId == slot.Id).ToArray();
        if (bySlot.Length == 1) return bySlot[0];
        if (bySlot.Length > 1) return null;
        var directory = string.IsNullOrWhiteSpace(slot.Directory) ? config.Draft.Project : slot.Directory;
        var byAgent = workbenchPanes.Where(pane =>
            !IsActiveWorkbenchStatus(pane.Status)
            && string.Equals(pane.Plan.AgentId, slot.AgentId, StringComparison.Ordinal)
            && MatchesSlotDirectory(pane.Plan, directory)).ToArray();
        return byAgent.Length == 1 ? byAgent[0] : null;
    }

    private static bool MatchesSlotDirectory(LaunchItem plan, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        if (SameOrContainedPath(plan.Cwd, directory) || SameOrContainedPath(plan.Workspace.Path, directory)) return true;
        return false;
    }

    private static bool SameOrContainedPath(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory)) return false;
        var full = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var root = directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        if (root.Length == 0 || full.Length == 0) return false;
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
        if (full.Length <= root.Length) return false;
        return full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AddOrRestartWorkbenchSlotAsync(Slot slot)
    {
        var exited = FindExitedWorkbenchPane(slot);
        if (exited is not null) await RestartWorkbenchPaneAsync(exited, slot);
        else await LaunchSlotsOnWorkbenchAsync([slot]);
    }

    private void ToggleMaximizeWorkbenchPane(WorkbenchPaneView pane)
    {
        maximizedPaneId = maximizedPaneId == pane.Id ? null : pane.Id;
        RelayoutWorkbench();
        RefreshPaneChrome();
    }

    private void RestoreWorkbenchGrid()
    {
        maximizedPaneId = null;
        RelayoutWorkbench();
        RefreshPaneChrome();
    }

    private void FocusWorkbenchPane(WorkbenchPaneView pane)
    {
        focusedPaneId = pane.Id;
        ApplyPaneFocusBorders();
        pane.Card.Focus();
        pane.Root.BringIntoView();
    }

    private void CycleWorkbenchFocus(bool backward)
    {
        if (workbenchPanes.Count == 0) return;
        var index = focusedPaneId is { } id ? workbenchPanes.FindIndex(pane => pane.Id == id) : -1;
        if (index < 0) index = backward ? 0 : workbenchPanes.Count - 1;
        var next = (index + (backward ? -1 : 1) + workbenchPanes.Count) % workbenchPanes.Count;
        FocusWorkbenchPane(workbenchPanes[next]);
    }

    private bool TryHandleWorkbenchFocusKey(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        if (windowMode != MainWindowMode.Workbench) return false;
        if (key != Key.Tab || (modifiers & ModifierKeys.Control) == 0) return false;
        if ((modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift)) != 0) return false;
        if (!isRepeat) CycleWorkbenchFocus((modifiers & ModifierKeys.Shift) != 0);
        return true;
    }

    private bool TryToggleStartPageChip(ToggleButton chip)
    {
        if (chip.Tag is not Guid id) return false;
        TrySetSlotEnabled(id, chip.IsChecked != true, chip);
        return true;
    }

    private bool TryHandleStartPageKey(Key key, ModifierKeys modifiers, bool isRepeat)
    {
        if (windowMode != MainWindowMode.StartPage || modifiers != ModifierKeys.None) return false;
        if (key == Key.Space)
        {
            if (Keyboard.FocusedElement is not ToggleButton chip || AgentChipPanel is null || !AgentChipPanel.Children.Contains(chip)) return false;
            if (!isRepeat) TryToggleStartPageChip(chip);
            return true;
        }
        if (key != Key.Enter) return false;
        if (Keyboard.FocusedElement is TextBox box && !box.IsReadOnly) return false;
        if (!LaunchButton.IsEnabled) return false;
        if (!isRepeat) LaunchClick(this, new());
        return true;
    }

    private void ApplyPaneFocusBorders()
    {
        foreach (var pane in workbenchPanes)
        {
            var focused = pane.Id == focusedPaneId;
            pane.Root.BorderThickness = new Thickness(focused ? 2 : 1);
            pane.Root.SetResourceReference(Border.BorderBrushProperty, focused ? "Accent" : "Line");
        }
    }

    private void SetWorkbenchPaneStatus(WorkbenchPaneView pane, WorkbenchPaneStatus status, string? reason = null)
    {
        pane.Status = status;
        if (reason is not null) pane.ExitReason = reason;
        var exited = status == WorkbenchPaneStatus.Exited;
        pane.StopButton.Visibility = exited ? Visibility.Collapsed : Visibility.Visible;
        pane.ExitOverlay.Visibility = exited ? Visibility.Visible : Visibility.Collapsed;
        pane.ExitReasonText.Text = pane.ExitReason;
        pane.RestartButton.Content = StartPageSurface.RestartPane;
        var brush = status switch
        {
            WorkbenchPaneStatus.Running => "StatusOk",
            WorkbenchPaneStatus.Exited => "StatusBad",
            _ => "StatusWait"
        };
        pane.StatusDot.SetResourceReference(Shape.FillProperty, brush);
        RefreshPaneChrome();
        UpdateWorkbenchChrome();
    }

    private void RefreshPaneChrome()
    {
        for (var i = 0; i < workbenchPanes.Count; i++)
        {
            var pane = workbenchPanes[i];
            var remote = pane.Plan.RemoteExecution is not null;
            pane.TitleText.Text = pane.Plan.Name;
            pane.TitleText.ToolTip = pane.Plan.Name;
            pane.PlaceText.Text = remote ? "远程" : "本机";
            pane.PlaceText.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void StopWorkbenchPane(WorkbenchPaneView pane)
    {
        if (!IsActiveWorkbenchStatus(pane.Status)) return;
        if (embeddedViews.TryGetValue(pane.Id, out var view))
        {
            view.Stop.Cancel();
            view.Client?.Dispose();
        }
        userStoppedPaneIds.Add(pane.Id);
        var reason = string.IsNullOrWhiteSpace(pane.ExitReason) ? "已停止" : pane.ExitReason;
        SetWorkbenchPaneStatus(pane, WorkbenchPaneStatus.Exited, reason);
    }

    private void CloseWorkbenchPane(WorkbenchPaneView pane)
    {
        RemoveEmbedded(pane.Id);
        RelayoutWorkbench();
        UpdateWorkbenchChrome();
    }

    private void OpenPaneMenu(WorkbenchPaneView pane, Button owner)
    {
        var menu = new ContextMenu();
        var command = pane.Plan.Command.Executable + (pane.Plan.Command.Arguments.Length == 0 ? "" : " " + string.Join(" ", pane.Plan.Command.Arguments));
        menu.Items.Add(new MenuItem { Header = "命令：" + command, IsEnabled = false });
        menu.Items.Add(new MenuItem { Header = "路径：" + (string.IsNullOrWhiteSpace(pane.Plan.Cwd) ? pane.Plan.Workspace.Path : pane.Plan.Cwd), IsEnabled = false });
        if (maximizedPaneId == pane.Id)
        {
            var restore = new MenuItem { Header = "回到网格" };
            restore.Click += (_, _) => RestoreWorkbenchGrid();
            menu.Items.Add(restore);
        }
        var close = new MenuItem { Header = "关闭格子" };
        close.Click += (_, _) => CloseWorkbenchPane(pane);
        menu.Items.Add(close);
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        owner.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void SyncWorkbenchPanesFromStatus()
    {
        if (workbenchPanes.Count == 0 || status?.Entries is not { Length: > 0 } entries) return;
        var changed = false;
        foreach (var pane in workbenchPanes)
        {
            var entry = entries.FirstOrDefault(item => item.Plan.Id == pane.Id);
            if (entry is null) continue;
            var mapped = MapControlEntryStatus(entry);
            if (userStoppedPaneIds.Contains(pane.Id))
            {
                if (mapped != WorkbenchPaneStatus.Exited) continue;
                userStoppedPaneIds.Remove(pane.Id);
            }
            var reason = mapped == WorkbenchPaneStatus.Exited
                ? (string.IsNullOrWhiteSpace(entry.Detail) ? (entry.ExitCode is { } code ? "退出码 " + code : "已退出") : entry.Detail)
                : pane.ExitReason;
            if (pane.Status == mapped && pane.ExitReason == reason) continue;
            pane.Status = mapped;
            pane.ExitReason = reason;
            changed = true;
        }
        if (!changed) return;
        foreach (var pane in workbenchPanes) SetWorkbenchPaneStatus(pane, pane.Status, pane.ExitReason);
    }

    private static WorkbenchPaneStatus MapControlEntryStatus(ControlEntry entry) => entry.State switch
    {
        "Pending" or "Creating" => WorkbenchPaneStatus.Starting,
        "Started" or "Running" => WorkbenchPaneStatus.Running,
        "Waiting" => WorkbenchPaneStatus.Waiting,
        "Pressure" => WorkbenchPaneStatus.Pressure,
        _ => WorkbenchPaneStatus.Exited
    };

    private async Task RestartWorkbenchPaneAsync(WorkbenchPaneView pane, Slot? slot = null)
    {
        var target = slot ?? config.Draft.Slots.FirstOrDefault(item => item.Id == pane.Plan.SourceSlotId)
            ?? config.Draft.Slots.FirstOrDefault(item => string.Equals(item.AgentId, pane.Plan.AgentId, StringComparison.Ordinal) && IsSlotReady(item));
        if (target is null)
        {
            ErrorText.Text = "无法重新启动：没有对应的可启动 Agent 配置。";
            return;
        }
        await LaunchSlotsOnWorkbenchAsync([target], pane);
    }

    private async Task LaunchSlotsOnWorkbenchAsync(IReadOnlyList<Slot> slots, WorkbenchPaneView? replace = null)
    {
        if (slots.Count == 0) return;
        if (closeRequested || busy || launchFinalizing || launchCancellation != null || !cancelTask.IsCompleted || readOnly || submittedAttempts.Length > 0)
        {
            ErrorText.Text = "当前不能启动新的窗格，请稍后再试。";
            return;
        }
        if (replace is null && workbenchPanes.Count + slots.Count > WorkbenchLayout.MaxPanes)
        {
            ErrorText.Text = StartPageSurface.CapDenied;
            UpdateWorkbenchChrome();
            return;
        }
        launchCancellation = new();
        SetBusy(true);
        ErrorText.Text = "";
        try
        {
            var draft = config.Draft.Copy();
            var ids = slots.Select(slot => slot.Id).ToHashSet();
            foreach (var item in draft.Slots) item.Enabled = ids.Contains(item.Id);
#if INTEGRATION_TESTS
            if (workbenchLaunchFailureForChecks is { Length: > 0 } failure)
            {
                workbenchLaunchFailureForChecks = null;
                throw new InvalidOperationException(failure);
            }
#endif
            await LaunchPreparedDraftAsync(draft, slots.Count, replace);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PresentWorkbenchLaunchFailure(ex.Message);
            return;
        }
        finally { await FinishLaunchAsync(); }
    }

    private void PresentWorkbenchLaunchFailure(string reason)
    {
        ErrorText.Text = string.IsNullOrWhiteSpace(reason) ? "无法启动新的窗格。" : reason;
        UpdateWorkbenchChrome();
    }

    private void ShowPartialLaunchNotice(int started, int requested, string reason)
    {
        if (started < 1 || started >= requested) return;
        WorkbenchNotice.Text = StartPageSurface.PartialLaunchLine(started, requested - started, reason);
        WorkbenchNotice.SetResourceReference(TextBlock.ForegroundProperty, "WarningInk");
    }

#if INTEGRATION_TESTS
    private string? workbenchLaunchFailureForChecks;

    private bool HandleWorkbenchFocusKeyForChecks(Key key, ModifierKeys modifiers) =>
        TryHandleWorkbenchFocusKey(key, modifiers, false);

    private bool ToggleStartPageChipForChecks(ToggleButton chip) => TryToggleStartPageChip(chip);


    private void ResetWorkbenchForChecks()
    {
        workbenchLaunchFailureForChecks = null;
        ClearWorkbenchTiles();
        windowMode = MainWindowMode.StartPage;
        WorkbenchPanel.BeginAnimation(OpacityProperty, null);
        WorkbenchPanel.Opacity = 1;
        ApplyWindowMode();
    }

    private void SetWorkbenchPaneStatusForChecks(Guid id, WorkbenchPaneStatus status, string? reason = null)
    {
        SetWorkbenchPaneStatus(workbenchPanes.Single(item => item.Id == id), status, reason);
        RelayoutWorkbench();
    }
#endif

    private int CountReadySelected() =>
        config.Draft.Slots.Count(slot => slot.Enabled && IsSlotReady(slot));

    private bool IsSlotReady(Slot slot) =>
        chipStatus.TryGetValue(slot.Id, out var status) && status.Ready;

    private string SlotChipReason(Slot slot) =>
        chipStatus.TryGetValue(slot.Id, out var status) ? status.Reason : (commandsChecking ? "正在检查" : "暂不清楚");

    private int? RemainingCapacity(int alreadySelected)
    {
        if (status?.Memory is not { } memory || !ResourcePolicy.IsSampleValid(memory)) return null;
        var room = Math.Max(0, WorkbenchLayout.MaxPanes - alreadySelected);
        var remaining = 0;
        for (var extra = 1; extra <= room; extra++)
        {
            var draft = config.Draft.Copy();
            var enabled = draft.Slots.Count(slot => slot.Enabled);
            while (draft.Slots.Count(slot => slot.Enabled) < enabled + extra)
                draft.Slots.Add(new Slot { Enabled = true, AgentId = draft.Slots.FirstOrDefault()?.AgentId ?? "custom" });
            var decision = DecideAdmission(draft, memory, status.Entries, budgets: status.Budgets);
            if (!decision.Launchable) break;
            remaining = extra;
        }
        return remaining;
    }

    private void RenderAgentChips()
    {
        if (AgentChipPanel is null) return;
        var previous = rendering;
        rendering = true;
        try
        {
            AgentChipPanel.Children.Clear();
            var chipIndex = 0;
            foreach (var slot in config.Draft.Slots)
            {
                var ready = IsSlotReady(slot);
                var reason = SlotChipReason(slot);
                var place = slot.RemoteHostId is null ? "本机" : (PlaceLabel(slot) ?? "远程");
                var chip = new ToggleButton
                {
                    Tag = slot.Id,
                    Style = (Style)FindResource("AgentChip"),
                    IsChecked = slot.Enabled,
                    IsEnabled = (slot.Enabled || ready) && !readOnly && !busy && !loading && !closeRequested
                };
                AutomationProperties.SetName(chip, slot.DisplayName);
                var menu = new ContextMenu();
                var edit = new MenuItem { Header = "编辑此项" };
                var slotId = slot.Id;
                edit.Click += (_, _) => EditSlot(slotId);
                menu.Items.Add(edit);
                chip.ContextMenu = menu;
                var body = new StackPanel();
                var name = new TextBlock { Text = slot.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
                name.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Ink" : "Muted");
                body.Children.Add(name);
                var placeText = new TextBlock { Text = place, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
                placeText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                body.Children.Add(placeText);
                var state = new TextBlock { Text = ready ? "就绪" : "未就绪 · " + reason, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
                state.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Muted" : "WarningInk");
                body.Children.Add(state);
                chip.Content = body;
                chip.ToolTip = ready ? place : reason;
                var id = slot.Id;
                KeyboardNavigation.SetTabIndex(chip, 20 + chipIndex);
                chipIndex++;
                chip.Checked += (_, _) => { if (!rendering) TrySetSlotEnabled(id, true, chip); };
                chip.Unchecked += (_, _) => { if (!rendering) TrySetSlotEnabled(id, false, chip); };
                AgentChipPanel.Children.Add(chip);
            }
        }
        finally { rendering = previous; }
    }

    private bool TrySetSlotEnabled(Guid id, bool enable, ToggleButton? chip)
    {
        var slot = config.Draft.Slots.FirstOrDefault(item => item.Id == id);
        if (slot is null) return false;
        if (enable)
        {
            if (!IsSlotReady(slot) || !StartPageSurface.CanSelect(config.Draft.Slots.Count(item => item.Enabled && item.Id != id && IsSlotReady(item)), IsSlotReady(slot)))
            {
                if (IsSlotReady(slot)) capDeniedSticky = true;
                if (chip is not null && chip.IsChecked != false)
                {
                    var previous = rendering;
                    rendering = true;
                    chip.IsChecked = false;
                    rendering = previous;
                }
                UpdateLaunch();
                return false;
            }
        }
        capDeniedSticky = false;
        return Edit(draft => draft.Slots.Single(item => item.Id == id).Enabled = enable);
    }

#if INTEGRATION_TESTS
    private void TrySelectOverCapReadyAgent()
    {
        var candidate = config.Draft.Slots.FirstOrDefault(slot => !slot.Enabled && IsSlotReady(slot));
        if (candidate is null) return;
        var chip = FindLogical(AgentChipPanel).OfType<ToggleButton>().FirstOrDefault(button => button.Tag is Guid id && id == candidate.Id);
        if (chip is null) return;
        chip.IsChecked = true;
    }
#endif
}