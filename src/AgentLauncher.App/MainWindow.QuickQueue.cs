using System.IO;
using System.Windows;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private bool quickQueueImporting;
    private Task quickQueueTask = Task.CompletedTask;

    private bool CanQueueQuickLaunch() => !loading && !closeRequested && !busy && !quickQueueImporting && !queuePumping
        && !launchFinalizing && launchCancellation is null && cancelTask.IsCompleted && !readOnly && !commandsChecking
        && taskBook is not null && SaveHealthy && submittedAttempts.Length == 0 && submittedRevision != config.Draft.Revision
        && validationError.Length == 0 && string.Equals(ProjectPath.Text.Trim(), config.Draft.Project, StringComparison.Ordinal)
        && config.Draft.Slots.Any(slot => slot.Enabled)
        && config.Draft.Slots.Where(slot => slot.Enabled).All(slot => (!slot.RequiresDirectory || !string.IsNullOrWhiteSpace(slot.Directory))
            && (!string.IsNullOrWhiteSpace(slot.Directory) || !string.IsNullOrWhiteSpace(config.Draft.Project)));

    private void UpdateQuickQueueButton()
    {
        QueueQuickButton.IsEnabled = CanQueueQuickLaunch();
        QueueQuickButton.Content = quickQueueImporting ? "正在加入队列…" : "按资源依次启动";
    }

    private async void QueueQuickClick(object sender, RoutedEventArgs e)
    {
        if (!CanQueueQuickLaunch()) return;
        quickQueueTask = QueueQuickLaunchAsync();
        await GuardAsync(() => quickQueueTask);
    }

    private async Task QueueQuickLaunchAsync()
    {
        var snapshot = config.Draft.Copy();
        var worktreeRoot = config.WorktreeDirectory;
        quickQueueImporting = true;
        SetBusy(true);
        ErrorText.Text = "";
        var startQueue = false;
        try
        {
            await saveTask;
            if (!SaveHealthy || closeRequested) return;
            var (items, destination) = await Task.Run(() => CaptureQuickQueue(snapshot, worktreeRoot));
            if (closeRequested) return;
            var pending = taskBook?.Queue.Count ?? 0;
            var names = string.Join(Environment.NewLine, items.Select(item =>
            {
                var slot = snapshot.Slots.Single(candidate => candidate.Id == item.SlotId);
                return $"{snapshot.Slots.IndexOf(slot) + 1:00} · {slot.DisplayName} · {item.Project}";
            }));
            var confirmation = new FormWindow(this, "按资源依次启动", [],
                $"将勾选的 {items.Count} 项保存为独立任务，并启动任务队列。\n\n{names}\n\n"
                + $"整个任务队列并发上限设为 1；已有的 {pending} 个等待项也会按原优先级继续。已运行任务不会被终止。\n\n"
                + "每项的程序、独立参数、目录和资源配置固定为当前快照。资源不足时等待，不提前创建 Agent 或工作目录。\n\n"
                + "这些队列任务改用独立内存额度，单项上限、规划与系统保护不变；原快启配置和活动池不变。"
                + "同仓库的独立目录项仍各自创建 worktree，届时若原仓库有未提交修改将等待人工处理，不自动确认。\n\n"
                + "确认后可暂停队列或移除等待项；关闭窗口会暂停后续派发，重新打开不会自动恢复。应用即授权上述队列执行。");
            if (confirmation.ShowDialog() != true || closeRequested) return;
            if (!await UpdateTasksAsync(book => TaskBookOps.EnqueueQuickLaunch(book, snapshot, items, destination), "快速启动入队失败")) return;
            TaskExpander.IsExpanded = true;
            startQueue = !closeRequested;
        }
        finally
        {
            quickQueueImporting = false;
            SetBusy(false);
        }
        if (!startQueue) return;
        queueEnabled = true;
        taskTimer.Start();
        RenderTasks();
        QueueStatusText.Text = "已按并发 1 加入队列；资源不足时保留等待，不预先占用 Agent 进程。";
        await PumpQueueAsync();
    }

    private static (IReadOnlyList<QuickLaunchItem> Items, string Destination) CaptureQuickQueue(Draft snapshot, string worktreeRoot)
    {
        snapshot.Validate();
        var items = new List<QuickLaunchItem>();
        var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in snapshot.Slots.Where(slot => slot.Enabled))
        {
            if (slot.RequiresDirectory && string.IsNullOrWhiteSpace(slot.Directory))
                throw new IOException("通用组合中有需要单独指定的目录，请先在详情中选择文件夹。");
            var path = string.IsNullOrWhiteSpace(slot.Directory) ? snapshot.Project : slot.Directory;
            if (!Path.IsPathFullyQualified(path)) throw new IOException("队列任务需要存在的本地绝对目录。");
            var project = PhysicalDirectory.Resolve(path).Path;
            var workspace = PhysicalDirectory.Workspace(project);
            var command = Commands.Resolve(slot.AgentId, slot.Current);
            if (!fingerprints.TryGetValue(command.Executable, out var fingerprint))
                fingerprints[command.Executable] = fingerprint = AgentProbe.Fingerprint(command.Executable);
            items.Add(new(slot.Id, project, workspace, command, fingerprint));
        }
        var destination = string.IsNullOrWhiteSpace(worktreeRoot) ? "" : PhysicalDirectory.Resolve(worktreeRoot).Path;
        return (items, destination);
    }
}
