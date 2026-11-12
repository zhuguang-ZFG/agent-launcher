using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private sealed record RecoveryView(string Text, QueueEntry? Entry, TaskRecoveryEvidence? Evidence, string? BackupHash);

    private RecoveryView ReadRecoveryView(Guid? taskId)
    {
        var text = new StringBuilder();
        TaskBook? book = null;
        string? backupHash = null;
        try { book = taskStore.Load(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            text.AppendLine("任务文件暂时不可读取，队列保持停用。").AppendLine(e.Message).AppendLine();
            try
            {
                backupHash = taskStore.BackupFingerprint();
                var backup = taskStore.ReadBackup();
                text.AppendLine($"可恢复的任务备份：活动 {backup.Tasks.Count(task => !task.Archived)} 张，归档 {backup.Tasks.Count(task => task.Archived)} 张，等待 {backup.Queue.Count} 项。");
                text.AppendLine("恢复后仍需核对执行记录；原文件与备份均会保留，队列保持暂停。");
            }
            catch (Exception backupError) when (backupError is not OutOfMemoryException) { backupHash = null; text.AppendLine("任务备份不可用：" + backupError.Message); }
        }
        QueueEntry? queue = null;
        TaskRecoveryEvidence? evidence = null;
        if (taskId is { } id && book?.Tasks.FirstOrDefault(task => task.Id == id) is { } task)
        {
            text.AppendLine("任务：" + task.Name);
            queue = book.Queue.FirstOrDefault(entry => entry.TaskId == id);
            if (queue is not null)
            {
                evidence = TaskRecoveryReader.Inspect(id, queue.Id);
                text.AppendLine("当前等待原因：" + (queue.Detail.Length > 0 ? queue.Detail : queue.Deferred ? "已暂缓" : "等待检查"));
                if (evidence.Failure.Length > 0) text.AppendLine("证据暂不可读取：" + evidence.Failure);
                else if (evidence.Claim is null) text.AppendLine("尚无领取记录。可核对新的程序与目录后重新入队；也可暂缓此项，让其他独立任务先执行。");
                else
                {
                    text.AppendLine("原尝试编号：" + evidence.Claim.AttemptId);
                    text.AppendLine("原启动状态：" + (evidence.Original?.State ?? "未知，身份记录未保留"));
                    text.AppendLine("最近说明：" + (evidence.Original?.Detail ?? "没有可验证的结果归档"));
                    text.AppendLine("回执阶段：" + (evidence.Receipt?.Phase ?? "未找到"));
                    text.AppendLine(evidence.CanReplaceIntent ? "已核对到原尝试的终态，可以生成新的排队意图。原领取与历史继续保留。"
                        : "尚不能证明原尝试已结束，因此不能重新执行。请先查看原终端；若存在陈旧占用，可点「核对占用」。缺少全部进程退出证据时，需要维护者核对或系统重启后再检查。");
                }
                if (book.Attempts.Any(run => run.TaskId == id && !run.Terminal)) text.AppendLine("任务文件仍标记有执行中的尝试；重新入队时会再次核对其身份和终态。");
                if (queue.ResumeOf is not null) text.AppendLine("这是续接项。核对原尝试已结束后，可先移出队列，再用任务卡的「续接会话」重新核对会话；此页不会把续接转换成新会话。");
            }
            else text.AppendLine("任务当前不在等待队列。可从「历史与诊断」查看本任务所有已保留尝试。");
        }
        else if (book is not null) text.AppendLine($"任务文件可读取：活动 {book.Tasks.Count(task => !task.Archived)} 张，等待 {book.Queue.Count} 项。可在任务卡或等待项的「原因与恢复」中核对具体任务。");
        try
        {
            var ledger = TaskRecoveryReader.ReadLedger();
            text.AppendLine().AppendLine($"已保留的运行记录：{ledger?.Entries.Count ?? 0} 条；领取记录：{ledger?.TaskClaims.Count ?? 0} 条。");
            text.AppendLine("「核对占用」只会释放有完整退出证据的陈旧占用。未知记录保持占用，直到证据满足或重启后可核对。");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { text.AppendLine().AppendLine("控制记录不可读取：" + e.Message).AppendLine("请复制此页诊断交给维护者核对原文件和备份。当前保留占用和历史。"); }
        return new(text.ToString(), queue, evidence, backupHash);
    }

    private async Task ShowTaskRecoveryAsync(Guid? taskId)
    {
        var window = new Window { Owner = this, Title = "原因与恢复", Width = Math.Min(780, SystemParameters.WorkArea.Width - 32), Height = Math.Min(660, SystemParameters.WorkArea.Height - 32), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(20) };
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(message, Dock.Top); root.Children.Add(message);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AutomationProperties.SetName(text, "恢复证据与建议操作"); root.Children.Add(text);
        RecoveryView? view = null;
        Button? restore = null, requeue = null;
        async Task RefreshView()
        {
            view = await Task.Run(() => ReadRecoveryView(taskId)); text.Text = view.Text;
            if (restore is not null) restore.IsEnabled = view.BackupHash is not null;
            if (requeue is not null) requeue.IsEnabled = view.Entry is { ResumeOf: null } && view.Evidence?.CanReplaceIntent == true;
        }
        void Add(string title, Func<Task> action, out Button button)
        {
            var control = new Button { Content = title, Margin = new Thickness(0, 0, 8, 4) }; button = control; actions.Children.Add(control);
            control.Click += async (_, _) =>
            {
                actions.IsEnabled = false;
                try { await action(); }
                catch (Exception e) when (e is not OutOfMemoryException) { message.Text = e.Message; }
                finally { actions.IsEnabled = true; }
            };
        }
        Add("刷新证据", RefreshView, out _);
        Add("复制诊断", () => { Clipboard.SetText(text.Text); message.Text = "已复制当前恢复诊断。"; return Task.CompletedTask; }, out _);
        Add("打开记录目录", () => { Directory.CreateDirectory(Store.Root); Process.Start(new ProcessStartInfo(Store.Root) { UseShellExecute = true }); return Task.CompletedTask; }, out _);
        Add("恢复任务备份…", async () =>
        {
            if (view?.BackupHash is not { } hash) return;
            if (MessageBox.Show(window, text.Text + "\n\n恢复以上任务备份？恢复后仍需核对记录，再明确执行队列。", "恢复任务备份", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (queuePumping) throw new IOException("当前正在派发，请先暂停队列后重试。");
            using var gate = taskStore.TryAcquireQueueGate() ?? throw new IOException("另一个窗口正在派发，请稍后重试。");
            queueEnabled = false; taskTimer.Stop();
            taskBook = await Task.Run(() => taskStore.RestoreBackup(hash)); taskLoadError = ""; queueScopeTaskIds = taskBook.LastDispatchTaskIds?.ToHashSet();
            RenderTasks(); message.Text = "任务备份已恢复，原文件已保留；请核对历史后再继续执行。"; await RefreshView();
        }, out restore);
        Add("核对占用", async () =>
        {
            if (MessageBox.Show(window, "将核对全部已结束的进程证据。证明不足的记录会保持占用。现在核对？", "核对占用", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var response = await Ipc.EnsureAsync(new("reconcile", Guid.NewGuid()));
            message.Text = response.Message; await RefreshAsync(); await RefreshView();
        }, out _);
        Add("核对后重新入队…", async () =>
        {
            if (view?.Entry is not { } entry) return;
            if (MessageBox.Show(window, "将重新核对原尝试、当前程序和目录，再生成新的排队项。原顺序、领取记录与历史保留；随后需明确执行队列。继续？", "核对后重新入队", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await ReplaceQueueIntentAsync(entry.Id); message.Text = "已生成新的排队项，原顺序与历史保留。请回到队列明确继续执行。"; await RefreshView();
        }, out requeue);
        var close = new Button { Content = "关闭", IsCancel = true }; close.Click += (_, _) => window.Close(); actions.Children.Add(close);
        window.Content = root; UiScale.Apply(window); await RefreshView(); window.Show();
    }

    private async Task ReplaceQueueIntentAsync(Guid entryId)
    {
        if (queuePumping || closeRequested) throw new IOException("正在派发，请先暂停队列后重试。");
        using var gate = taskStore.TryAcquireQueueGate() ?? throw new IOException("另一个窗口正在派发，请稍后重试。");
        var fresh = await Task.Run(taskStore.Load);
        var old = fresh.Queue.SingleOrDefault(entry => entry.Id == entryId) ?? throw new IOException("排队项已变化，请刷新后重试。");
        var task = fresh.Tasks.Single(candidate => candidate.Id == old.TaskId);
        if (old.ResumeOf is not null) throw new IOException("这是续接项，请先移出队列，再从任务卡的「续接会话」核对原会话。");
        var evidence = await Task.Run(() => TaskRecoveryReader.Inspect(task.Id, old.Id));
        if (!evidence.CanReplaceIntent) throw new IOException("原尝试尚不能证明已结束，保持原排队项与占用。 " + evidence.Failure);
        var command = await Task.Run(() => Commands.Resolve(task.AgentId, task.Profile));
        var fingerprint = await Task.Run(() => AgentProbe.Fingerprint(command));
        var workspace = await Task.Run(() => PhysicalDirectory.Workspace(PhysicalDirectory.Resolve(task.Project).Path));
        if (old.QuickLaunch is { } source && source.WorkspaceKey != workspace.Key) throw new IOException("项目物理身份已改变，请先编辑并核对任务目录。");
        queueEnabled = false; taskTimer.Stop(); taskBook = fresh;
        if (!await UpdateTasksAsync(book =>
        {
            foreach (var run in book.Attempts.Where(run => run.TaskId == task.Id && !run.Terminal).ToArray())
            {
                if (evidence.Original is null || !AttemptIdentity.Matches(run, evidence.Original) || !Store.IsTerminal(evidence.Original)) throw new InvalidDataException("仍有无法核对的执行中尝试，保留原记录。");
                TaskBookOps.FinishRun(book, run.AttemptId, evidence.Original.State == "Settled" ? "Review" : "Failed", "恢复页已核对原尝试终态：" + evidence.Original.State);
            }
            TaskBookOps.Dequeue(book, task.Id);
            var replacement = TaskBookOps.Enqueue(book, task.Id, command, fingerprint);
            replacement.Sequence = old.Sequence;
        }, "重新入队失败")) throw new IOException(ErrorText.Text);
    }
}
