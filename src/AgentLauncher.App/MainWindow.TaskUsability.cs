using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private bool InQueueScope(Guid taskId) => queueScopeTaskIds is null || queueScopeTaskIds.Contains(taskId);

    private void TaskSearchChanged(object sender, TextChangedEventArgs e) { if (!loading && TaskList is not null) RenderTasks(); }
    private void TaskFilterChanged(object sender, SelectionChangedEventArgs e) { if (!loading && TaskList is not null) RenderTasks(); }
    private async void QueueContinueClick(object sender, RoutedEventArgs e) => await GuardAsync(ContinueQueueAsync);

    private async Task ContinueQueueAsync()
    {
        if (queueEnabled || queueEditing || quickQueueImporting || closeRequested || readOnly || taskBook is null) return;
        queueEditing = true;
        try
        {
            await LoadTasksAsync();
            if (taskBook is null) return;
            queueScopeTaskIds = taskBook.LastDispatchTaskIds?.ToHashSet();
            if (!taskBook.Queue.Any(entry => InQueueScope(entry.TaskId))) return;
            queueEnabled = true;
            taskTimer.Start();
        }
        finally { queueEditing = false; RenderTasks(); }
        await PumpQueueAsync();
    }

    private void RenderQueueEntries(TaskBook book)
    {
        QueueList.Children.Clear();
        var rank = 0;
        foreach (var entry in TaskUsability.OrderedQueue(book))
        {
            var task = book.Tasks.Single(candidate => candidate.Id == entry.TaskId);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var position = entry.Deferred ? "暂缓" : !InQueueScope(task.Id) ? "范围外" : $"第 {++rank} 项";
            var scope = !InQueueScope(task.Id) ? " · 不在本批次内" : "";
            row.Children.Add(new TextBlock { Text = $"{position} · {task.Name} · 优先级 {entry.Priority}{scope}", TextWrapping = TextWrapping.Wrap });
            var reason = entry.Deferred ? "已暂缓，不阻挡后续任务；恢复后保留原来的排队顺序。" : entry.Detail.Length > 0 ? entry.Detail : "等待调度检查";
            row.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            var actions = new WrapPanel();
            var defer = TaskButton((entry.Deferred ? "恢复排队 " : "暂缓排队 ") + task.Name, entry.Deferred ? "恢复排队" : "暂缓此项");
            defer.IsEnabled = !closeRequested && !queuePumping;
            defer.Click += async (_, _) => await GuardAsync(() => SetQueueDeferredAsync(entry.Id, !entry.Deferred));
            actions.Children.Add(defer);
            var recovery = TaskButton("恢复指引 " + task.Name, "原因与恢复…");
            recovery.Click += async (_, _) => await GuardAsync(() => ShowTaskRecoveryAsync(task.Id));
            actions.Children.Add(recovery);
            row.Children.Add(actions);
            QueueList.Children.Add(row);
        }
        if (book.Queue.Count == 0) QueueList.Children.Add(new TextBlock { Text = "没有等待项。", FontSize = 12 });
    }

    private async Task SetQueueDeferredAsync(Guid entryId, bool deferred)
    {
        if (queuePumping || closeRequested) throw new IOException("正在核对启动，请稍后再调整排队项。");
        using var gate = taskStore.TryAcquireQueueGate() ?? throw new IOException("另一个窗口正在派发，请稍后重试。");
        var fresh = await Task.Run(taskStore.Load);
        var entry = fresh.Queue.SingleOrDefault(candidate => candidate.Id == entryId) ?? throw new IOException("排队项已经变化，请刷新后重试。");
        var evidence = await Task.Run(() => TaskRecoveryReader.Inspect(entry.TaskId, entry.Id));
        if (evidence.Failure.Length > 0 || evidence.Claim is not null) throw new IOException("此项已被领取或无法核对，请使用「原因与恢复」；当前不会暂缓或重新派发。 " + evidence.Failure);
        taskBook = fresh;
        if (await UpdateTasksAsync(book => TaskBookOps.SetDeferred(book, entryId, deferred), "调整排队失败"))
            QueueStatusText.Text = deferred ? "已暂缓此项，其他独立任务可继续执行。" : "已恢复排队，保留原来的顺序。";
    }

    private void AddTaskUtilityActions(StackPanel stack, TaskCard task)
    {
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var brief = TaskButton("复制任务说明 " + task.Name, "复制任务说明…");
        brief.Click += (_, _) => ShowTaskBrief(task);
        actions.Children.Add(brief);
        var history = TaskButton("任务历史 " + task.Name, "历史与诊断…");
        history.Click += async (_, _) => await GuardAsync(() => ShowTaskHistoryAsync(task.Id));
        actions.Children.Add(history);
        if (task.Archived || taskBook is { } book && !book.Queue.Any(entry => entry.TaskId == task.Id) && !book.Attempts.Any(run => run.TaskId == task.Id && !run.Terminal))
        {
            var archive = TaskButton((task.Archived ? "恢复归档 " : "归档任务 ") + task.Name, task.Archived ? "恢复到活动任务" : "归档");
            archive.Click += async (_, _) => await GuardAsync(() => SetTaskArchivedAsync(task.Id, !task.Archived));
            actions.Children.Add(archive);
        }
        stack.Children.Add(actions);
    }

    private async Task SetTaskArchivedAsync(Guid taskId, bool archived)
    {
        if (queuePumping || closeRequested) throw new IOException("正在核对启动，请稍后再归档。");
        using var gate = taskStore.TryAcquireQueueGate() ?? throw new IOException("另一个窗口正在派发，请稍后重试。");
        var ledger = await Task.Run(TaskRecoveryReader.ReadLedger);
        if (ledger?.Entries.Any(entry => entry.Plan.SourceSlotId == taskId && !Store.IsTerminal(entry)) == true)
            throw new IOException("仍有执行中或结果未知的启动记录，请先使用「恢复与诊断」核对。");
        taskBook = await Task.Run(taskStore.Load);
        if (await UpdateTasksAsync(book => TaskBookOps.SetArchived(book, taskId, archived), "任务归档失败"))
            QueueStatusText.Text = archived ? "任务已归档，活动名额已释放；可在「已归档」中恢复，历史与文件保留。" : "已恢复到活动任务，尚未加入队列。";
    }

    private void ShowTaskBrief(TaskCard task)
    {
        var window = new Window { Owner = this, Title = "复制任务说明 · " + task.Name, Width = Math.Min(680, SystemParameters.WorkArea.Width - 32), Height = Math.Min(580, SystemParameters.WorkArea.Height - 32), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(20) };
        var note = new TextBlock { Text = "启动器尚未把这些内容发送给 Agent。复制后，请粘贴到本任务对应的终端，并确认工作目录。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(note, Dock.Top); root.Children.Add(note);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var text = new TextBox { Text = TaskUsability.Brief(task), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(text, "任务说明与验收项，复制前预览");
        var copy = new Button { Content = "复制说明与验收项", Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(text.Text); note.Text = "已复制。请粘贴到本任务对应的 Agent 终端。"; } catch (System.Runtime.InteropServices.COMException) { note.Text = "剪贴板正忙，请稍后重试或选择文字复制。"; } };
        var close = new Button { Content = "关闭", IsCancel = true }; close.Click += (_, _) => window.Close();
        actions.Children.Add(copy); actions.Children.Add(close); root.Children.Add(text);
        window.Content = root; UiScale.Apply(window); window.Show();
    }

    private async void TaskHistoryClick(object sender, RoutedEventArgs e) => await GuardAsync(() => ShowTaskHistoryAsync(null));
    private async Task ShowTaskHistoryAsync(Guid? taskId)
    {
        var snapshot = await Task.Run(() => TaskHistoryReader.Load(taskStore, taskId));
        var window = new TaskHistoryWindow(this, snapshot);
        window.Show();
    }

    private async void TaskRecoveryClick(object sender, RoutedEventArgs e) => await GuardAsync(() => ShowTaskRecoveryAsync(null));
}
