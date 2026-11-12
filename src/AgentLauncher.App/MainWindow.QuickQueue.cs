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
        QueueQuickButton.ToolTip = QueueQuickButton.IsEnabled ? "将勾选项保存为任务；默认只执行本批次，按并发 1 等待资源。" : taskBook is null ? "任务文件无法读取，请打开「恢复与诊断」。" : LaunchHint.Text;
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
        var includeExisting = false;
        IReadOnlyList<Guid> imported = [];
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
            var confirmation = new FormWindow(this, "按资源依次启动", [("scope", "本次执行范围", "batch", false)],
                $"本次勾选 {items.Count} 项：\n{names}\n\n"
                + $"默认仅执行这 {items.Count} 项，本批次并发为 1；已有 {pending} 个等待项不会由本窗口派发，原并发设置保留。\n\n"
                + "选择「整个队列」才会同时执行已有等待项，并把全局并发改成 1。\n"
                + "资源不足会等待。关闭窗口会暂停后续派发，重开后需点「继续执行」。\n"
                + "任务说明需复制到对应终端；程序、参数、目录与资源按当前设置保存。同仓库独立目录仍会创建 worktree。",
                choices: new() { ["scope"] = [("batch", $"仅本批次 · {items.Count} 项（推荐）"), ("all", $"整个队列 · 新增 {items.Count} 项 + 已有 {pending} 项；全局并发改为 1")] },
                applyLabel: "确认范围并启动");
            if (confirmation.ShowDialog() != true || closeRequested) return;
            includeExisting = confirmation.Values["scope"] == "all";
            if (!await UpdateTasksAsync(book =>
            {
                imported = TaskBookOps.EnqueueQuickLaunch(book, snapshot, items, destination, resetConcurrency: includeExisting);
                book.LastDispatchTaskIds = includeExisting ? null : imported.ToList();
            }, "快速启动入队失败")) return;
            TaskExpander.IsExpanded = true;
            startQueue = !closeRequested;
        }
        finally
        {
            quickQueueImporting = false;
            SetBusy(false);
        }
        if (!startQueue) return;
        queueScopeTaskIds = includeExisting ? null : imported.ToHashSet();
        queueEnabled = true;
        taskTimer.Start();
        RenderTasks();
        QueueStatusText.Text = includeExisting ? "已启动整个队列，全局并发为 1。" : $"仅执行本批次 {imported.Count} 项，并发 1；其他等待项与原并发设置保留。";
        await PumpQueueAsync();
    }

    private static (IReadOnlyList<QuickLaunchItem> Items, string Destination) CaptureQuickQueue(Draft snapshot, string worktreeRoot)
    {
        snapshot.Validate();
        var items = new List<QuickLaunchItem>();
        var fingerprints = new Dictionary<(string Runtime, string Adapter, string Entry), string>();
        foreach (var slot in snapshot.Slots.Where(slot => slot.Enabled))
        {
            if (slot.RequiresDirectory && string.IsNullOrWhiteSpace(slot.Directory))
                throw new IOException("通用组合中有需要单独指定的目录，请先在详情中选择文件夹。");
            var path = string.IsNullOrWhiteSpace(slot.Directory) ? snapshot.Project : slot.Directory;
            if (!Path.IsPathFullyQualified(path)) throw new IOException("队列任务需要存在的本地绝对目录。");
            var project = PhysicalDirectory.Resolve(path).Path;
            var workspace = PhysicalDirectory.Workspace(project);
            var command = Commands.Resolve(slot.AgentId, slot.Current);
            var identity = (command.Executable.ToUpperInvariant(), command.Adapter,
                command.Adapter == "node-shim" ? command.Arguments[0].ToUpperInvariant() : "");
            if (!fingerprints.TryGetValue(identity, out var fingerprint))
                fingerprints[identity] = fingerprint = AgentProbe.Fingerprint(command);
            items.Add(new(slot.Id, project, workspace, command, fingerprint));
        }
        var destination = string.IsNullOrWhiteSpace(worktreeRoot) ? "" : PhysicalDirectory.Resolve(worktreeRoot).Path;
        return (items, destination);
    }
}
