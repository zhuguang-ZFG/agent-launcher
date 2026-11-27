using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media.Animation;
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
    }

    private MainWindowMode windowMode = MainWindowMode.StartPage;
    private readonly Dictionary<Guid, (bool Ready, string Reason)> chipStatus = [];
    private readonly List<WorkbenchPaneView> workbenchPanes = [];
    private bool capDeniedSticky;

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

    private void EnterWorkbench(IReadOnlyList<ControlEntry> entries) =>
        EnterWorkbench(entries.Select(entry => entry.Plan).ToList());

    private void EnterWorkbench(IReadOnlyList<LaunchItem> started)
    {
        var fromStart = windowMode == MainWindowMode.StartPage;
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

    private void AttachWorkbenchPane(LaunchItem plan)
    {
        if (workbenchPanes.Any(pane => pane.Id == plan.Id)) return;
        if (!embeddedViews.ContainsKey(plan.Id)) OpenEmbedded(plan, false);
        if (!embeddedViews.TryGetValue(plan.Id, out var view)) return;
        var content = view.Tab.Content as UIElement;
        view.Tab.Content = null;
        var root = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            ClipToBounds = true
        };
        root.SetResourceReference(Border.BackgroundProperty, "Surface");
        root.SetResourceReference(Border.BorderBrushProperty, "Line");
        WorkbenchHost.Children.Add(root);
        workbenchPanes.Add(new WorkbenchPaneView { Id = plan.Id, Plan = plan, Root = root, Card = view.Card });
        EmbeddedPanel.Visibility = Visibility.Collapsed;
        ShowTerminalsButton.Visibility = Visibility.Collapsed;
    }

    private void DetachWorkbenchPane(Guid id)
    {
        var pane = workbenchPanes.FirstOrDefault(item => item.Id == id);
        if (pane is null) return;
        workbenchPanes.Remove(pane);
        WorkbenchHost.Children.Remove(pane.Root);
        if (windowMode == MainWindowMode.Workbench) RelayoutWorkbench();
        UpdateWorkbenchChrome();
    }

    private void RelayoutWorkbench()
    {
        if (WorkbenchHost is null || windowMode != MainWindowMode.Workbench) return;
        var width = Math.Max(WorkbenchScroll?.ViewportWidth > 0 ? WorkbenchScroll.ViewportWidth : WorkbenchHost.ActualWidth, 1);
        var height = Math.Max(WorkbenchScroll?.ViewportHeight > 0 ? WorkbenchScroll.ViewportHeight : WorkbenchHost.ActualHeight, 1);
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
        WorkbenchCapacity.Text = workbenchPanes.Count + " / 最多 " + WorkbenchLayout.MaxPanes;
        WorkbenchResource.Text = status?.Memory is { } sample
            ? $"{RailResourceMode(config.Draft.Resource?.Mode)} · 可用 {GiB(sample.Available)} GiB"
            : "资源：未读取";
        BackToStartButton.Content = StartPageSurface.BackToStart;
        BackToStartButton.Visibility = WorkbenchLayout.ShowBackToStart(windowMode, workbenchPanes.Count) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPartialLaunchNotice(int started, int requested, string reason)
    {
        if (started < 1 || started >= requested) return;
        WorkbenchNotice.Text = StartPageSurface.PartialLaunchLine(started, requested - started, reason);
        WorkbenchNotice.SetResourceReference(TextBlock.ForegroundProperty, "WarningInk");
    }

#if INTEGRATION_TESTS
    private void ResetWorkbenchForChecks()
    {
        foreach (var id in workbenchPanes.Select(pane => pane.Id).Concat(embeddedViews.Keys).Distinct().ToArray())
            RemoveEmbedded(id);
        workbenchPanes.Clear();
        WorkbenchHost.Children.Clear();
        if (WorkbenchNotice is not null) WorkbenchNotice.Text = "";
        windowMode = MainWindowMode.StartPage;
        WorkbenchPanel.BeginAnimation(OpacityProperty, null);
        WorkbenchPanel.Opacity = 1;
        ApplyWindowMode();
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
            foreach (var slot in config.Draft.Slots)
            {
                var ready = IsSlotReady(slot);
                var reason = SlotChipReason(slot);
                var place = slot.RemoteHostId is null ? "本机" : (PlaceLabel(slot) ?? "远程");
                var chip = new ToggleButton
                {
                    Tag = slot.Id,
                    Margin = new Thickness(0, 0, 12, 12),
                    Padding = new Thickness(12, 10, 12, 10),
                    MinWidth = 168,
                    MinHeight = 72,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Top,
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
                body.Children.Add(new TextBlock { Text = slot.DisplayName, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                body.Children.Add(new TextBlock { Text = place, Margin = new Thickness(0, 4, 0, 0), Style = (Style)FindResource("Caption") });
                var state = new TextBlock { Text = ready ? "就绪" : "未就绪 · " + reason, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
                state.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Muted" : "WarningInk");
                body.Children.Add(state);
                chip.Content = body;
                chip.ToolTip = ready ? place : reason;
                var id = slot.Id;
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