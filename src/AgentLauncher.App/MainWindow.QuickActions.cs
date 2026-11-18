using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private bool quickActionsOpen;
    private long recentSwitchGeneration;
    private Task recentProjectTask = Task.CompletedTask;
    private bool CanChangeProject => !loading && !busy && !readOnly && !closeRequested;

    private async void QuickActionsClick(object sender, RoutedEventArgs e) => await GuardAsync(OpenQuickActionsAsync);
    private async Task OpenQuickActionsAsync()
    {
        if (loading || closeRequested || quickActionsOpen) return;
        var previousFocus = Keyboard.FocusedElement as UIElement;
        QuickAction? chosen = null;
        quickActionsOpen = true;
        try
        {
            var dialog = new QuickActionsWindow(this, BuildQuickActions());
            if (dialog.ShowDialog() == true) chosen = dialog.Chosen;
        }
        finally { quickActionsOpen = false; }
        if (chosen is not null && !closeRequested)
        {
            if (chosen.Enabled) await chosen.Run();
            else ErrorText.Text = "当前操作不可用，请等待正在进行的操作完成。";
        }
        else if (previousFocus is { IsVisible: true, IsEnabled: true }) previousFocus.Focus();
    }

    private QuickAction[] BuildQuickActions()
    {
        var actions = new List<QuickAction>();
        void Add(string id, string title, string detail, Action run, Func<bool>? available = null) =>
            actions.Add(new(id, title, detail, () => { run(); return Task.CompletedTask; }, available));
        Add("project", "选择项目文件夹", "Ctrl+O · 更换默认项目，单独指定的目录保持原值", () => ChooseClick(this, new()), () => CanChangeProject);
        Add("sessions", "查看运行记录", "查看活动会话、退出信息与恢复入口", () => JumpTo(SessionExpander));
        Add("tasks", "查找任务", "任务名称、Agent、备注或项目路径", () => { JumpTo(TaskExpander); TaskSearch.Focus(); TaskSearch.SelectAll(); });
        Add("background", "后台运行", "隐藏到通知区域，继续队列和会话通知；双击托盘图标返回", EnterBackground, () => !closeRequested);
        actions.Add(new("refresh", "重新检查启动条件", "F5 · 刷新程序路径和电脑资源", RefreshLaunchChecksAsync, () => !busy && !checkingLaunch && !closeRequested));
        Add("workspaces", "工作目录", "查看分支、打开目录或核对清理条件", () => WorkspacesClick(this, new()), () => !busy);
        Add("history", "全部历史与导出", "搜索保留的任务尝试与诊断", () => TaskHistoryClick(this, new()));
        Add("recovery", "恢复与诊断", "核对任务备份、等待项和未知尝试", () => TaskRecoveryClick(this, new()), () => !busy);
        Add("settings", "设置", "主题、文字大小、Terminal 与通知", () => SettingsClick(this, new()), () => !busy);
        Add("resources", "资源设置", "准入模式、并发资源与单项上限", () => ResourceClick(this, new()), () => !busy);
        Add("combinations", "组合与批量设置", "恢复组合、保存模板、规模预设和 Agent 筛选", () => JumpTo(ConfigurationToolsExpander));
        Add("add-agent", "添加 Agent", "添加后直接编辑新项；勾选后加入本次启动", AddAgentSlot, () => CanAddSlot);
        if (selectedSlotId is { } selected && config.Draft.Slots.FirstOrDefault(s => s.Id == selected) is { } current)
        {
            Add("duplicate-agent", "复制当前 Agent 配置", current.DisplayName + " · 保留程序、参数、目录和备注，副本不勾选", () => DuplicateSlot(selected), () => CanAddSlot && CanEditSlot(selected));
            Add("rename-agent", "重命名当前 Agent", current.DisplayName + " · 用分工名称区分同类 Agent", () => RenameSlot(selected), () => CanEditSlot(selected));
        }
        foreach (var slot in config.Draft.Slots)
        {
            var position = config.Draft.Slots.IndexOf(slot) + 1;
            Add("slot:" + slot.Id, $"切换到 {position:00} · {slot.DisplayName}", $"{Agents.Get(slot.AgentId).Name} 配置 · {(slot.Enabled ? "已勾选" : "未勾选")}" + (position <= 9 ? $" · Alt+{position}" : ""), () => NavigateToSlot(slot.Id));
        }
        foreach (var entry in (status?.Entries ?? []).Where(e => e.Plan.Kind == "agent" && !Store.IsTerminal(e)).OrderByDescending(e => e.Created))
        {
            Add("session:" + entry.Plan.Id, "运行记录 · " + entry.Plan.Name, RailSessionState(entry) + " · " + entry.Plan.Cwd, () => NavigateToSession(entry.Plan.Id));
            if (entry.State is "Started" or "Running" or "Exited")
                Add("terminal:" + entry.Plan.Id, "聚焦终端 · " + entry.Plan.Name, "定位对应批次和窗格", () => { NavigateToSession(entry.Plan.Id); FocusTerminalWindow(entry.Plan.BatchId,entry.Plan.Id); });
        }
        if (taskBook is { } book)
            foreach (var entry in TaskUsability.OrderedQueue(book))
            {
                var task = book.Tasks.FirstOrDefault(t => t.Id == entry.TaskId);
                if (task is not null) Add("queue:" + entry.Id, "等待项 · " + task.Name, (entry.Deferred ? "已暂缓" : "等待调度") + " · " + task.Project, () => NavigateToQueueEntry(entry.Id));
            }
        foreach (var path in config.RecentProjects.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            actions.Add(new("recent:" + path, "最近项目 · " + ProjectName(path), path, () => SwitchRecentProjectAsync(path), () => CanChangeProject));
        return actions.ToArray();
    }

    private void ProjectSwitchClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var choose = new MenuItem { Header = "选择项目文件夹…", InputGestureText = "Ctrl+O", IsEnabled = CanChangeProject };
        choose.Click += (_, _) => ChooseClick(this, new()); menu.Items.Add(choose);
        if (config.RecentProjects.Count > 0) menu.Items.Add(new Separator());
        foreach (var path in config.RecentProjects.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            var item = new MenuItem { Header = new TextBlock { Text = ProjectName(path) }, ToolTip = path, IsEnabled = CanChangeProject, IsChecked = string.Equals(path, config.Draft.Project, StringComparison.OrdinalIgnoreCase) };
            AutomationProperties.SetName(item, "最近项目 " + path);
            item.Click += async (_, _) => await GuardAsync(() => SwitchRecentProjectAsync(path)); menu.Items.Add(item);
        }
        ProjectSwitchButton.ContextMenu = menu; menu.PlacementTarget = ProjectSwitchButton; menu.IsOpen = true;
    }
    private static string ProjectName(string path) => Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
    private async Task SwitchRecentProjectAsync(string path)
    {
        if (!CanChangeProject) return;
        var generation = ++recentSwitchGeneration;
        var draft = config.Draft; var inputRevision = projectInputRevision;
        bool StillCurrent() => generation == recentSwitchGeneration && ReferenceEquals(draft, config.Draft) && inputRevision == projectInputRevision && CanChangeProject;
        // Validate before replacing the current draft: stale recent paths must leave the working project intact.
        try { await Task.Run(() => PhysicalDirectory.Resolve(path)); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException)
        { if (StillCurrent()) throw new IOException("最近项目无法访问，当前项目保持不变。请选择移动后的文件夹。\n" + ex.Message, ex); return; }
        if (StillCurrent()) SelectProject(path);
    }
    private void RenderRecentProjects()
    {
        var wasRendering = rendering; rendering = true;
        try { Recent.ItemsSource = config.RecentProjects.ToArray(); Recent.SelectedIndex = -1; Recent.Visibility = Visibility.Collapsed; }
        finally { rendering = wasRendering; }
    }
    private void NavigateToSlot(Guid id)
    {
        if (loading || rendering || closeRequested || config.Draft.Slots.FirstOrDefault(s => s.Id == id) is not { } slot) return;
        selectedSlotId = id; rendering = true;
        try
        {
            // Navigation rebuilds only the selected editor, preserving uncommitted project text and rail focus.
            SlotList.Children.Clear(); commandLabels.Clear(); AddSlot(slot);
            DetailTitle.Text = $"{config.Draft.Slots.IndexOf(slot) + 1:00} · {slot.DisplayName}";
            foreach (var button in FindLogical(RailSlots).OfType<Button>())
                if (Equals(button.Tag, id)) button.SetResourceReference(Button.BackgroundProperty, "Selected");
                else button.Background = System.Windows.Media.Brushes.Transparent;
        }
        finally { rendering = false; }
        JumpTo(ConfigurationPanel); _ = GuardAsync(CheckCommandsAsync);
    }
    private void NavigateToSession(Guid id)
    {
        RenderSessions();
        if (ActiveSessions.Children.OfType<FrameworkElement>().Any(item => Equals(item.Tag, id)))
            JumpToRecord(ActiveNow, ActiveSessions, id, "这条会话已移出当前列表，可在全部历史中查找。");
        else
            JumpToRecord(SessionExpander, Sessions, id, "这条会话已移出当前列表，可在全部历史中查找。");
    }
    private void NavigateToQueueEntry(Guid id)
    {
        RenderTasks(); JumpToRecord(TaskExpander, QueueList, id, "这条等待项已离开队列，请查看运行记录或任务卡片。");
    }
    private void JumpToRecord(FrameworkElement section, Panel list, Guid id, string missing)
    {
        if (section is Expander expander) expander.IsExpanded = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var row = list.Children.OfType<FrameworkElement>().FirstOrDefault(item => Equals(item.Tag, id));
            if (row is null) { ErrorText.Text = missing; section.BringIntoView(); return; }
            row.Focus(); row.BringIntoView(new Rect(0, 0, row.ActualWidth, Math.Min(40, row.ActualHeight)));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void MainKeyDown(object sender, KeyEventArgs e)
    {
        if (loading || closeRequested || !IsEnabled || quickActionsOpen) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && key == Key.K)
        { e.Handled = true; if (!e.IsRepeat) QuickActionsClick(this, new()); }
        else if (modifiers == ModifierKeys.Control && key == Key.O)
        { e.Handled = true; if (!e.IsRepeat) ChooseClick(this, new()); }
        else if (modifiers == ModifierKeys.Control && key == Key.Enter)
        { e.Handled = true; if (!e.IsRepeat && LaunchButton.IsEnabled) LaunchClick(this, new()); }
        else if (modifiers == ModifierKeys.None && key == Key.F5)
        { e.Handled = true; if (!e.IsRepeat && RefreshLaunchButton.IsEnabled) RefreshLaunchClick(this, new()); }
        else if (modifiers == ModifierKeys.Alt && key >= Key.D1 && key <= Key.D9)
        {
            e.Handled = true;
            var index = (int)key - (int)Key.D1;
            if (!e.IsRepeat && index < config.Draft.Slots.Count) NavigateToSlot(config.Draft.Slots[index].Id);
        }
    }
}
