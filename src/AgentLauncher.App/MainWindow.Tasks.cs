using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private const int TaskPollSeconds = 15;
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);
    private readonly TaskStore taskStore = new(Store.FilePath("tasks.json"));
    private readonly DispatcherTimer taskTimer = new() { Interval = TimeSpan.FromSeconds(TaskPollSeconds) };
    private TaskBook? taskBook;
    private string taskLoadError = "";
    private bool queueEnabled;
    private bool queuePumping;
    private bool queuePausedByRecovery;
    private bool renderingTasks;
    private Task queuePumpTask = Task.CompletedTask;
    private CancellationTokenSource? queueCancellation;

    private async Task LoadTasksAsync()
    {
        try
        {
            taskBook = await Task.Run(taskStore.Load);
            taskLoadError = "";
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            taskBook = null;
            taskLoadError = e.Message;
            ErrorText.Text = "任务文件无法读取，队列已停用，不会覆盖原文件。\n" + e.Message;
        }
        RenderTasks();
    }

    private async Task<bool> UpdateTasksAsync(Action<TaskBook> change, string failure)
    {
        if (taskBook is null) { ErrorText.Text = taskLoadError; return false; }
        var expected = taskBook.Revision;
        try
        {
            taskBook = await Task.Run(() => taskStore.Update(expected, change));
            ErrorText.Text = "";
            RenderTasks();
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException)
        {
            ErrorText.Text = failure + "：" + e.Message;
            try { taskBook = await Task.Run(taskStore.Load); } catch (Exception reload) when (reload is IOException or JsonException or InvalidDataException) { taskLoadError = reload.Message; }
            RenderTasks();
            return false;
        }
    }

    private void RenderTasks()
    {
        renderingTasks = true;
        try { RenderTaskCards(); } finally { renderingTasks = false; UpdateQuickQueueButton(); }
    }

    private void RenderTaskCards()
    {
        TaskList.Children.Clear();
        if (taskBook is null)
        {
            TaskHeading.Text = "任务队列";
            TaskEmptyText.Text = "任务文件无法读取：" + taskLoadError;
            TaskEmptyText.Visibility = Visibility.Visible;
            QueueStatusText.Text = "队列已停用";
            QueueToggleButton.Content = "执行队列";
            QueueToggleButton.IsEnabled = false;
            TaskConcurrency.IsEnabled = false;
            return;
        }
        var book = taskBook;
        TaskHeading.Text = $"任务队列 · {book.Tasks.Count} 张 · 待执行 {book.Queue.Count}";
        TaskEmptyText.Text = "还没有任务。新建任务后加入队列，启动器会按优先级依次交给 Terminal。";
        TaskEmptyText.Visibility = book.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hold = book.Queue.OrderByDescending(entry => entry.Priority).ThenBy(entry => entry.Sequence).FirstOrDefault(entry => entry.Detail.Length > 0);
        QueueStatusText.Text = queueEnabled ? hold is null ? $"队列运行中 · 每 {TaskPollSeconds} 秒检查一次" : $"队列等待：{hold.Detail}" : "队列已暂停";
        QueueToggleButton.Content = queueEnabled ? "暂停队列" : "执行队列";
        QueueToggleButton.IsEnabled = !closeRequested && !quickQueueImporting;
        TaskConcurrency.SelectedIndex = Math.Clamp(book.MaxRunning - 1, 0, 3);
        TaskConcurrency.IsEnabled = !closeRequested && !quickQueueImporting;
        foreach (var task in book.Tasks.OrderByDescending(task => task.Priority).ThenBy(task => task.UpdatedAt))
            TaskList.Children.Add(TaskCardView(book, task));
    }

    private Border TaskCardView(TaskBook book, TaskCard task)
    {
        var last = TaskBookOps.LastRun(book, task.Id);
        var queued = book.Queue.FirstOrDefault(entry => entry.TaskId == task.Id);
        var state = queued is not null ? "排队中" : last is null ? "未开始" : last.State switch
        {
            "Running" => "运行中",
            "Review" => "等待验收",
            "Completed" => "已完成",
            "Failed" => "失败",
            "Cancelled" => "已取消",
            _ => last.State
        };
        var card = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("Surface"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("Line"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8)
        };
        AutomationProperties.SetName(card, $"任务卡片 {task.Name}");
        var stack = new StackPanel();
        var header = new DockPanel { LastChildFill = false };
        var title = new TextBlock { Text = $"{task.Name} · {Agents.Get(task.AgentId).Name} · {state}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(title, Dock.Left); header.Children.Add(title);
        var priority = new TextBlock { Text = $"优先级 {task.Priority} · 修订 {task.Revision}", Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(priority, Dock.Right); header.Children.Add(priority);
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Text = task.Project, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        if (queued is not null)
            stack.Children.Add(new TextBlock { Text = queued.Detail.Length > 0 ? $"队列：{queued.Detail}" : "队列：等待执行", FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        if (last is not null)
            stack.Children.Add(new TextBlock { Text = $"第 {book.Attempts.Count(run => run.TaskId == task.Id)} 次尝试 · {StateText(last.State)}" + (last.Detail.Length > 0 ? " · " + last.Detail : ""), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        foreach (var item in task.Acceptance)
        {
            var check = new CheckBox { Content = item.Text, IsChecked = item.Checked, Margin = new Thickness(0, 6, 0, 0) };
            AutomationProperties.SetName(check, $"验收 {item.Text}");
            var itemId = item.Id;
            // Read the control on the UI thread: the store update runs on a worker and must not touch WPF objects.
            check.Click += async (_, _) => { var value = check.IsChecked == true; await GuardAsync(() => UpdateTasksAsync(editor => TaskBookOps.SetAcceptance(editor, task.Id, itemId, value), "验收项保存失败")); };
            stack.Children.Add(check);
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var edit = TaskButton("编辑任务 " + task.Name, "编辑");
        edit.Click += async (_, _) => await GuardAsync(() => EditTaskAsync(task.Id));
        actions.Children.Add(edit);
        if (queued is null)
        {
            var enqueue = TaskButton("加入队列 " + task.Name, "加入队列");
            enqueue.Click += async (_, _) => await GuardAsync(() => EnqueueTaskAsync(task.Id));
            actions.Children.Add(enqueue);
        }
        else
        {
            var dequeue = TaskButton("移出队列 " + task.Name, "移出队列");
            dequeue.Click += async (_, _) => await GuardAsync(async () => { if (await UpdateTasksAsync(editor => TaskBookOps.Dequeue(editor, task.Id), "移出队列失败")) QueueStatusText.Text = "已移出队列。"; });
            actions.Children.Add(dequeue);
        }
        if (last is { State: "Review" })
        {
            var complete = TaskButton("完成验收 " + task.Name, "标记完成");
            complete.Click += async (_, _) => await GuardAsync(async () => { if (await UpdateTasksAsync(editor => TaskBookOps.Complete(editor, task.Id), "完成失败")) QueueStatusText.Text = "已标记完成；队列继续执行下一项。"; });
            actions.Children.Add(complete);
        }
        if (last is { Terminal: true } settled)
        {
            var register = TaskButton("登记会话 " + task.Name, settled.SessionRef is { Length: > 0 } ? "修改会话" : "登记会话");
            register.Click += async (_, _) => await GuardAsync(() => RegisterSessionAsync(task.Id));
            actions.Children.Add(register);
        }
        if (last is { } previous && previous.Terminal && previous.SessionRef is { Length: > 0 })
        {
            var resume = TaskButton("续接会话 " + task.Name, "续接会话");
            resume.Click += async (_, _) => await GuardAsync(() => ResumeTaskAsync(task.Id));
            actions.Children.Add(resume);
        }
        if (last is { } reviewed)
        {
            var review = TaskButton("审查改动 " + task.Name, $"审查改动 {task.Name}");
            // The window prefers these polled monitor entries and falls back to the on-disk ledger; it never starts a task to resolve the workspace.
            review.Click += async (_, _) => await GuardAsync(() => { new TaskReviewWindow(this, task, reviewed, status?.Entries).Show(); return Task.CompletedTask; });
            actions.Children.Add(review);
        }
        stack.Children.Add(actions);
        card.Child = stack;
        return card;
    }

    private static string StateText(string state) => state switch
    {
        "Running" => "运行中",
        "Review" => "等待人工验收",
        "Completed" => "已完成",
        "Failed" => "失败",
        "Cancelled" => "已取消",
        _ => state
    };

    private static Button TaskButton(string name, string content)
    {
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 8, 4), MinWidth = 88 };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private async void NewTaskClick(object sender, RoutedEventArgs e) => await GuardAsync(NewTaskAsync);
    private async void TaskConcurrencyChanged(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (renderingTasks || taskBook is null) return;
        var value = TaskConcurrency.SelectedIndex + 1;
        if (value < 1 || value > 4 || value == taskBook.MaxRunning) return;
        if (await UpdateTasksAsync(book => book.MaxRunning = value, "并发设置保存失败"))
            QueueStatusText.Text = $"并发上限已设为 {value}。";
        else RenderTasks();
    });
    private async void QueueToggleClick(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (closeRequested || quickQueueImporting) return;
        queueEnabled = !queueEnabled;
        if (queueEnabled) taskTimer.Start(); else { taskTimer.Stop(); queueCancellation?.Cancel(); }
        RenderTasks();
        if (queueEnabled) await PumpQueueAsync();
    });

    private async Task NewTaskAsync()
    {
        if (taskBook is null) { ErrorText.Text = taskLoadError; return; }
        if (taskBook.Tasks.Count >= TaskSchema.MaxTasks) { ErrorText.Text = "任务数量已达上限，请先完成或删除旧任务。"; return; }
        var dialog = new FormWindow(this, "新建任务",
            [("name", "任务名称", "", false), ("project", "项目目录", config.Draft.Project, false), ("agent", "Agent", "omp", false),
             ("priority", "优先级 0–100", "0", false), ("mode", "目录策略", "isolated", false),
             ("description", "任务说明", "", true), ("acceptance", "验收项（每行一项）", "", true)],
            "任务把项目、Agent、参数与验收项固定到独立修订。加入队列后按优先级执行；完成需要人工验收，不自动判定。",
            choices: new()
            {
                ["agent"] = [.. AgentChoices().Select(choice => (choice.Id, choice.Label))],
                ["mode"] = [("isolated", "独立工作目录（可并行）"), ("shared", "共享项目目录")]
            },
            suggestions: new() { ["project"] = [.. config.RecentProjects] });
        if (dialog.ShowDialog() != true) return;
        var project = dialog.Values["project"].Trim();
        if (!Path.IsPathFullyQualified(project) || !Directory.Exists(project)) { ErrorText.Text = "任务项目必须是存在的本地绝对目录。"; return; }
        var priority = int.TryParse(dialog.Values["priority"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 0, 100) : 0;
        var acceptance = dialog.Values["acceptance"].Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var name = dialog.Values["name"];
        var agent = dialog.Values["agent"];
        var description = dialog.Values["description"];
        var mode = dialog.Values["mode"];
        var resource = config.Draft.Resource;
        TaskCard? created = null;
        if (!await UpdateTasksAsync(book => created = TaskBookOps.Create(book, name, project, agent, new AgentProfile(), description, acceptance, mode, resource, priority), "任务保存失败")) return;
        QueueStatusText.Text = created is null ? "任务已保存。" : $"已创建任务：{created.Name}。加入队列后才会启动。";
    }

    private async Task EditTaskAsync(Guid id)
    {
        if (taskBook is null) return;
        var task = taskBook.Tasks.SingleOrDefault(candidate => candidate.Id == id);
        if (task is null) return;
        var dialog = new FormWindow(this, "任务 · " + task.Name,
            [("name", "任务名称", task.Name, false), ("project", "项目目录", task.Project, false), ("agent", "Agent", task.AgentId, false),
             ("priority", "优先级 0–100", task.Priority.ToString(CultureInfo.InvariantCulture), false), ("mode", "目录策略", task.DirectoryMode, false),
             ("description", "任务说明", task.Description, true), ("acceptance", "验收项（每行一项）", string.Join(Environment.NewLine, task.Acceptance.Select(item => item.Text)), true),
             ("args", "Agent 参数", "", false)],
            "修改项目、Agent、目录策略、优先级或参数会移除该任务的排队项；验收勾选不会。切换 Agent 会重置为该 Agent 的默认参数。",
            choices: new()
            {
                ["agent"] = [.. AgentChoices().Select(choice => (choice.Id, choice.Label))],
                ["mode"] = [("isolated", "独立工作目录（可并行）"), ("shared", "共享项目目录")]
            },
            suggestions: new() { ["project"] = [.. config.RecentProjects] },
            argumentLists: new() { ["args"] = task.Profile.Arguments });
        if (dialog.ShowDialog() != true) return;
        var project = dialog.Values["project"].Trim();
        if (!Path.IsPathFullyQualified(project) || !Directory.Exists(project)) { ErrorText.Text = "任务项目必须是存在的本地绝对目录。"; return; }
        var priority = int.TryParse(dialog.Values["priority"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, 0, 100) : task.Priority;
        var acceptance = dialog.Values["acceptance"].Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var arguments = dialog.ArgumentValues["args"];
        var agent = dialog.Values["agent"];
        var name = dialog.Values["name"].Trim();
        var mode = dialog.Values["mode"];
        var description = dialog.Values["description"];
        await UpdateTasksAsync(book =>
        {
            var previous = book.Tasks.Single(candidate => candidate.Id == id);
            var checkedTexts = previous.Acceptance.Where(item => item.Checked).Select(item => item.Text).ToHashSet(StringComparer.Ordinal);
            TaskBookOps.Edit(book, id, draft =>
            {
                draft.Name = name;
                draft.Project = Path.GetFullPath(project);
                if (agent != draft.AgentId) { draft.AgentId = agent; draft.Profile = new AgentProfile(); }
                else draft.Profile.Arguments = [.. arguments];
                draft.Priority = priority;
                draft.DirectoryMode = mode;
                draft.Description = description;
                draft.Acceptance = acceptance.Select(text => new AcceptanceItem { Id = Guid.NewGuid().ToString("N"), Text = text, Checked = checkedTexts.Contains(text) }).ToList();
            });
        }, "任务保存失败");
    }

    private async Task EnqueueTaskAsync(Guid id)
    {
        if (taskBook is null) return;
        var task = taskBook.Tasks.SingleOrDefault(candidate => candidate.Id == id);
        if (task is null) return;
        if (!Directory.Exists(task.Project)) { ErrorText.Text = "任务项目目录不存在：" + task.Project; return; }
        CommandSpec command;
        string fingerprint;
        try
        {
            (command, fingerprint) = await Task.Run(() =>
            {
                var spec = Commands.Resolve(task.AgentId, task.Profile);
                return (spec, AgentProbe.Fingerprint(spec.Executable));
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            ErrorText.Text = "无法准备任务命令：" + e.Message;
            return;
        }
        if (!await UpdateTasksAsync(book => TaskBookOps.Enqueue(book, id, command, fingerprint), "入队失败")) return;
        QueueStatusText.Text = "已加入队列。点「执行队列」后开始；关闭窗口不会结束已启动的进程。";
        if (queueEnabled) await PumpQueueAsync();
    }

    private Task PumpQueueAsync()
    {
        if (!queueEnabled || closeRequested || taskBook is null || taskLoadError.Length > 0 || queuePumping || readOnly) return Task.CompletedTask;
        if (busy || launchFinalizing || launchCancellation is not null || !cancelTask.IsCompleted || submittedAttempts.Length > 0) return Task.CompletedTask;
        if (PauseEmptyQueue()) return Task.CompletedTask;
        queuePumping = true;
        queueCancellation = new();
        queuePausedByRecovery = false;
        UpdateQuickQueueButton();
        return queuePumpTask = RunQueuePumpAsync(queueCancellation);
    }

    private bool PauseEmptyQueue()
    {
        if (taskBook is null || taskBook.Queue.Count != 0 || taskBook.Attempts.Any(run => !run.Terminal)) return false;
        queueEnabled = false;
        taskTimer.Stop();
        RenderTasks();
        QueueStatusText.Text = "队列已空，已停止轮询；新增任务后需明确执行。";
        return true;
    }

    private async Task RunQueuePumpAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await ReconcileTaskRunsAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (PauseEmptyQueue()) return;
            using var gate = taskStore.TryAcquireQueueGate();
            if (gate is null) { QueueStatusText.Text = "另一个窗口正在执行队列，本窗口等待。"; return; }
            await DispatchNextAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!queuePausedByRecovery && queueEnabled) QueueStatusText.Text = "队列已暂停；已运行任务不受影响。";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException or TimeoutException or JsonException)
        {
            QueueStatusText.Text = "队列暂停：" + e.Message;
            ErrorText.Text = "任务队列：" + e.Message;
        }
        finally
        {
            queuePumping = false;
            queueCancellation = null;
            cancellation.Dispose();
            UpdateQuickQueueButton();
        }
    }

    private async Task ReconcileTaskRunsAsync()
    {
        if (taskBook is null || !taskBook.Attempts.Any(run => !run.Terminal)) return;
        await RefreshAsync();
        foreach (var run in taskBook.Attempts.Where(candidate => !candidate.Terminal).ToArray())
        {
            var entry = status?.Entries?.FirstOrDefault(candidate => candidate.Plan.Id == run.AttemptId);
            if(entry is null)
            {
                var result=await Ipc.SendAsync(new("attempt",Guid.NewGuid(),Attempt:run.AttemptId));
                if(!result.Ok||result.Entries is not [var retained])continue;
                entry=retained;
            }
            if(!AttemptIdentity.Matches(run,entry))throw new InvalidDataException("任务尝试身份与监控结果不一致，已保留运行状态，请人工核对。");
            if (entry!.State is "FailedBeforeStart" or "Revoked")
            {
                await UpdateTasksAsync(book => TaskBookOps.FinishRun(book, run.AttemptId, "Failed", "未启动：" + entry.Detail), "任务状态更新失败");
                continue;
            }
            if (entry.State == "Settled")
                await UpdateTasksAsync(book => TaskBookOps.FinishRun(book, run.AttemptId, "Review", "进程已结束，等待人工验收"), "任务状态更新失败");
            else if (entry.State == "Pending" && DateTimeOffset.UtcNow - entry.Created > ClaimTimeout)
                await RevokeStaleAsync(run.AttemptId);
            else if (run.Detail != entry.Detail)
                await UpdateTasksAsync(book => TaskBookOps.SetRunDetail(book, run.AttemptId, entry.Detail), "任务状态更新失败");
        }
    }

    private async Task RevokeStaleAsync(Guid attemptId)
    {
        Response revoke;
        try { revoke = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: attemptId)); }
        catch (Exception e) when (e is IOException or TimeoutException) { return; }
        if (!revoke.Ok) return;
        await UpdateTasksAsync(book => TaskBookOps.FinishRun(book, attemptId, "Failed", "超过 5 分钟未被终端领取，已撤销，可重新入队"), "任务状态更新失败");
    }

    private async Task DispatchNextAsync(CancellationToken cancellationToken = default)
    {
        var book = taskBook!;
        if (book.Queue.Count == 0) { QueueStatusText.Text = "队列为空。"; return; }
        await RefreshAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (status?.Ok != true || status.Memory is null) { QueueStatusText.Text = "监控状态不可用，队列暂不启动。"; return; }
        if (status.AdmissionBlockedReason is { Length: > 0 } blocked) { QueueStatusText.Text = "队列暂停：" + blocked; return; }
        if (book.Queue.Count == 0)
        {
            QueueStatusText.Text = "队列为空。";
            return;
        }
        var candidates = book.Queue.Select(entry => new QueueCandidate(entry.Id, entry.TaskId, entry.TaskRevision, entry.Priority, entry.Sequence, entry.EnqueuedAt, [.. entry.DependsOn])).ToArray();
        var decision = QueuePolicy.Decide(candidates, book.Attempts.Count(run => !run.Terminal), book.MaxRunning,
            id => book.Attempts.Any(run => run.TaskId == id && run.State == "Completed"));
        if (decision.Next is null)
        {
            QueueStatusText.Text = decision.Waiting.Count > 0 ? "队列等待：" + decision.Waiting[0].Reason : "队列为空。";
            return;
        }
        var entry = book.Queue.Single(candidate => candidate.Id == decision.Next.EntryId);
        var task = book.Tasks.Single(candidate => candidate.Id == entry.TaskId);
        if (task.Revision != entry.TaskRevision) { await HoldEntryAsync(entry, "任务已被修改，请重新入队。"); return; }

        // A durable monitor claim is execution authority for this queue item. A claimed item with no recorded task run
        // must never be prepared again; this is a read-only inspection of the existing ledger, not a new authorization.
        var claim = await Task.Run(() => InspectQueueClaim(entry.Id), cancellationToken);
        if (claim.Failure.Length > 0 || claim.Claim is not null) { PauseQueueForClaim(entry, claim); return; }

        var profile = task.Profile.Copy();
        var resume = entry.ResumeOf is not null || !string.IsNullOrWhiteSpace(entry.SessionRef);
        if (resume && (entry.ResumeOf is null || string.IsNullOrWhiteSpace(entry.SessionRef)))
        {
            await HoldEntryAsync(entry, "续接项缺少上次尝试或会话标识，请重新入队。");
            return;
        }
        if (resume && profile.Arguments.Any(IsResumeArgument))
        {
            await HoldEntryAsync(entry, "任务参数已包含续接/继续参数，请先移除后再续接。");
            return;
        }
        CommandSpec resolved;
        try { resolved = await Task.Run(() => Commands.Resolve(task.AgentId, profile)); }
        catch (Exception e) when (e is IOException or FileNotFoundException or UnauthorizedAccessException) { await HoldEntryAsync(entry, "命令无法解析：" + e.Message); return; }
        var fingerprint = await Task.Run(() => AgentProbe.Fingerprint(resolved.Executable));
        var workspace = PhysicalDirectory.Workspace(PhysicalDirectory.Resolve(Path.GetFullPath(entry.Project)).Path);
        if (entry.QuickLaunch is { } source && !string.Equals(source.WorkspaceKey, workspace.Key, StringComparison.Ordinal))
        {
            await HoldEntryAsync(entry, "工作目录物理身份已改变，请核对后重新入队。");
            return;
        }
        var (dispatchAllowed, dispatchReason) = QueuePolicy.CanDispatchResume(book, entry, workspace.Key, fingerprint);
        if (!dispatchAllowed) { await HoldEntryAsync(entry, dispatchReason); return; }
        if (resume)
        {
            AgentCapability capability;
            try { capability = await AgentProbe.ProbeCapabilityAsync(task.AgentId, profile, entry.Project, TimeSpan.FromSeconds(8)); }
            catch (Exception e) when (e is not OutOfMemoryException) { await HoldEntryAsync(entry, "无法核对 Agent 版本，不能续接：" + e.Message); return; }
            var preset = ResumePreset(capability, task.AgentId);
            if (preset is null) { await HoldEntryAsync(entry, $"当前版本（{capability.Version ?? "未知"}）没有已验证的续接参数。"); return; }
            try
            {
                profile.Arguments = [.. profile.Arguments, .. preset.Fill(entry.SessionRef).Arguments];
                resolved = await Task.Run(() => Commands.Resolve(task.AgentId, profile));
                fingerprint = await Task.Run(() => AgentProbe.Fingerprint(resolved.Executable));
            }
            catch (Exception e) when (e is not OutOfMemoryException) { await HoldEntryAsync(entry, "无法准备续接命令：" + e.Message); return; }
        }
        if (fingerprint != entry.ProgramFingerprint || !SameCommand(resolved, entry.Command))
        {
            await HoldEntryAsync(entry, "程序或参数已变化，请重新入队。");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ResourcePolicySnapshot policy;
        try
        {
            policy = ResourcePolicy.SnapshotFor(entry.Resource, status.Memory.Total, status.Memory.CommitLimit);
            ResourcePolicy.ValidateSettings(entry.Resource, status.Memory.Total, status.Memory.CommitLimit);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException)
        {
            await HoldEntryAsync(entry, "任务资源设置无效：" + e.Message);
            return;
        }
        var forceIsolated = entry.QuickLaunch?.IsolateWorkspace == true;
        var pendingBudget = new BudgetItem(entry.Id, "agent", policy.PlanningBytes, policy.JobMemoryLimit,
            policy.PoolMode == ResourcePolicy.PoolShared ? entry.Id : null,
            policy.PoolMode == ResourcePolicy.PoolShared ? policy.PoolMemoryLimit : null);
        BudgetItem[] requested = forceIsolated
            ? [pendingBudget, new(Guid.Empty, "git", policy.GitPlanningBytes, policy.GitJobMemoryLimit)] : [pendingBudget];
        var reserved = status.Entries?.Where(candidate => !Store.IsTerminal(candidate)).Select(candidate => ResourcePolicy.BudgetOf(candidate.Plan, candidate.PoolMemoryLimit)).ToArray() ?? [];
        var admission = ResourcePolicy.Evaluate(status.Memory, policy, reserved, requested);
        if (admission.Kind == AdmissionKind.Deny) { await HoldEntryAsync(entry, "资源不足：" + admission.Reason); return; }
        if (admission.Kind == AdmissionKind.RequireWaiver) { await HoldEntryAsync(entry, "需要一次性资源确认；队列不会自动确认，请暂停并人工核对。"); return; }

        var draft = new Draft
        {
            SchemaVersion = config.Draft.SchemaVersion,
            Revision = config.Draft.Revision,
            Project = entry.Project,
            DirectoryMode = entry.DirectoryMode,
            Layout = "vertical",
            Resource = entry.Resource.Copy(),
            Slots = [new Slot { Id = task.Id, Enabled = true, AgentId = task.AgentId, Profiles = new() { [task.AgentId] = profile } }]
        };

        LaunchItem[] planned;
        try
        {
            var preparer = new WorkspacePreparer(entry.QuickLaunch?.WorktreeRoot ?? config.WorktreeDirectory);
            var activeWorkspaces = status.Entries?.Where(candidate => !Store.IsTerminal(candidate) && candidate.Plan.Kind == "agent").Select(candidate => candidate.Plan.Workspace).ToArray() ?? [];
            planned = (await Task.Run(() => preparer.PrepareAsync(draft, text => Dispatcher.Invoke(() => QueueStatusText.Text = text),
                _ => Task.FromResult(false), cancellationToken, activeWorkspaces, null, forceIsolated, entry.QuickLaunch?.WorkspaceKey))).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidDataException)
        {
            await HoldEntryAsync(entry, "准备失败：" + e.Message);
            return;
        }
        if (planned.Length != 1) { await HoldEntryAsync(entry, "任务只允许一个启动项。"); return; }
        var plan = planned[0];
        if ((!forceIsolated && !string.Equals(plan.Workspace.Key, workspace.Key, StringComparison.Ordinal)) || !SameCommand(plan.Command, entry.Command))
        {
            await HoldEntryAsync(entry, "准备结果与冻结的工作目录或命令不一致，请重新入队。");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RefreshAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (status?.Ok != true || status.Memory is null || status.AdmissionBlockedReason is { Length: > 0 })
        { await HoldEntryAsync(entry, status?.AdmissionBlockedReason ?? "准备后监控状态不可用，等待重新核验。"); return; }
        reserved = status.Entries?.Where(candidate => !Store.IsTerminal(candidate)).Select(candidate => ResourcePolicy.BudgetOf(candidate.Plan, candidate.PoolMemoryLimit)).ToArray() ?? [];
        admission = ResourcePolicy.Evaluate(status.Memory, policy, reserved, [ResourcePolicy.BudgetOf(plan, plan.PoolId is null ? null : policy.PoolMemoryLimit)]);
        if (admission.Kind == AdmissionKind.Deny) { await HoldEntryAsync(entry, "资源不足：" + admission.Reason); return; }
        if (admission.Kind == AdmissionKind.RequireWaiver) { await HoldEntryAsync(entry, "需要一次性资源确认；队列不会自动确认，请暂停并人工核对。"); return; }

        Response reserve;
        try { reserve = await Ipc.ReserveWithCleanupAsync([plan], cancellationToken, null, policy, null, ResourcePolicy.PolicyHash(policy), ResourcePolicy.PlanDigest([plan]), null, null, entry.Id); }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            queueEnabled = false;
            taskTimer.Stop();
            await HoldEntryAsync(entry, "预约结果未确认，队列已暂停；先核对运行记录，不会自动重放：" + e.Message);
            return;
        }
        if (!reserve.Ok)
        {
            // The claim may have appeared after the precheck. Re-inspect the durable ledger before the next tick prepares again.
            var refused = await Task.Run(() => InspectQueueClaim(entry.Id), cancellationToken);
            if (refused.Failure.Length > 0 || refused.Claim is not null) { PauseQueueForClaim(entry, refused); return; }
            await HoldEntryAsync(entry, reserve.Message);
            return;
        }

        var recorded = false;
        var submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            recorded = await UpdateTasksAsync(editor =>
            {
                TaskBookOps.StartRun(editor, entry, plan.Id, plan.BatchId, plan.Workspace.Key, entry.ResumeOf, entry.SessionRef);
                if (forceIsolated)
                {
                    var bound = editor.Tasks.Single(candidate => candidate.Id == entry.TaskId);
                    bound.Project = plan.Cwd;
                    bound.DirectoryMode = "prepared";
                    bound.QuickLaunch = entry.QuickLaunch! with { WorkspaceKey = plan.Workspace.Key, IsolateWorkspace = false };
                }
            }, "启动记录失败");
            if (!recorded) return;
            cancellationToken.ThrowIfCancellationRequested();
            QueueStatusText.Text = $"正在提交任务：{task.Name}";
            var terminalPath = config.TerminalPath;
            var terminal = await Task.Run(() => Commands.Terminal(terminalPath));
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => Commands.SubmitTerminal(terminal, [plan], "vertical"));
            submitted = true;
            QueueStatusText.Text = $"已提交任务：{task.Name}。进程出现不代表 Agent 已就绪。";
            if (!closeRequested && config.MinimizeAfterLaunch) WindowState = WindowState.Minimized;
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or UnauthorizedAccessException)
        {
            ErrorText.Text = "Terminal 提交结果未确认，请核对运行记录：" + e.Message;
        }
        finally
        {
            if (!submitted) await RevokeQueueReservationAsync(plan.Id, recorded, cancellationToken.IsCancellationRequested);
        }
    }

    private async Task RevokeQueueReservationAsync(Guid attemptId, bool recorded, bool cancelled)
    {
        try
        {
            var reply = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: attemptId));
            var revoked = reply.Entries?.FirstOrDefault(entry => entry.Plan.Id == attemptId && entry.State == "Revoked");
            if (reply.Ok && revoked is not null)
            {
                if (recorded)
                {
                    await UpdateTasksAsync(book => TaskBookOps.FinishRun(book, attemptId, cancelled ? "Cancelled" : "Failed", "未启动预约已撤销"), "撤销记录保存失败");
                    return;
                }
                // The reservation is gone but the durable claim for the queue item survives. Never leave the queue enabled
                // to prepare the same claimed entry again; require the user to inspect the original attempt and re-enqueue explicitly.
                queueEnabled = false;
                taskTimer.Stop();
                queuePausedByRecovery = true;
                RenderTasks();
                var attempt = attemptId.ToString("N")[..8];
                ErrorText.Text = $"队列已暂停：尝试 {attempt} 的预约已撤销（{revoked.Detail}），但排队项的持久领取记录已保留，未写入启动记录。请先确认它从未启动，然后在任务卡片点「移出队列」，再点「加入队列」明确重新排队；不会自动重放。";
                QueueStatusText.Text = $"队列已暂停：尝试 {attempt} 未写入启动记录；确认后点「移出队列」，再点「加入队列」。";
                return;
            }
            QueueStatusText.Text = "预约已领取或撤销结果未确认，队列已暂停；请核对运行记录，不会自动重放。";
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            QueueStatusText.Text = "撤销结果未确认，队列已暂停；请核对运行记录：" + e.Message;
        }
        queueEnabled = false;
        taskTimer.Stop();
        RenderTasks();
    }

    private sealed record QueueClaimObservation(TaskReservationClaim? Claim, ControlEntry? Original, string Failure);

    private QueueClaimObservation InspectQueueClaim(Guid entryId)
    {
        var path = Store.FilePath("control.json");
        try
        {
            var ledger = Store.Read<Ledger>(path);
            if (ledger is null)
                return File.Exists(path + ".bak")
                    ? new QueueClaimObservation(null, null, "控制账本缺失但存在备份，无法核对领取记录。")
                    : new QueueClaimObservation(null, null, "");
            var accepted = LedgerMigration.Accept(ledger);
            var claim = accepted.TaskClaims.FirstOrDefault(candidate => candidate.EntryId == entryId);
            return new QueueClaimObservation(claim, claim is null ? null : accepted.Entries.FirstOrDefault(candidate => candidate.Plan.Id == claim.AttemptId), "");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return new QueueClaimObservation(null, null, e.Message);
        }
    }

    private void PauseQueueForClaim(QueueEntry entry, QueueClaimObservation observation)
    {
        queueEnabled = false;
        taskTimer.Stop();
        queuePausedByRecovery = true;
        RenderTasks();
        var task = taskBook?.Tasks.FirstOrDefault(candidate => candidate.Id == entry.TaskId);
        var name = task?.Name ?? "未知任务";
        if (observation.Failure.Length > 0 || observation.Claim is null)
        {
            ErrorText.Text = $"队列已暂停：无法核对任务「{name}」的持久领取记录，未准备命令或工作目录，也不会自动重放。请先核对 {Store.FilePath("control.json")}（必要时核对 .bak 备份）后再执行队列：{observation.Failure}";
            QueueStatusText.Text = "队列已暂停：无法核对队列领取记录，未准备或启动；请核对 control.json 与备份。";
            return;
        }
        var claim = observation.Claim!;
        var run = taskBook?.Attempts.FirstOrDefault(candidate => candidate.AttemptId == claim.AttemptId);
        var state = run?.State ?? observation.Original?.State ?? "未知或记录已裁剪";
        var attempt = claim.AttemptId.ToString("N")[..8];
        var evidence = observation.Original?.Detail is { Length: > 0 } detail ? $"最近记录：{detail}。" : "";
        if (run is null && observation.Original is null)
        {
            ErrorText.Text = $"队列已暂停：任务「{name}」的排队项已有持久领取记录（尝试 {attempt}），但原尝试的状态与回执历史已裁剪或未知。不能仅凭「移出队列」再「加入队列」视为安全；请先找到该尝试的回执或运行记录，确认它从未启动或已经结束，再明确移出并重新入队。不会删除领取记录，也不会自动重放。";
            QueueStatusText.Text = $"队列已暂停：尝试 {attempt} 的状态与历史已裁剪或未知；先核对原尝试回执与运行记录，确认后再移出并重新入队。";
            return;
        }
        ErrorText.Text = $"队列已暂停：任务「{name}」的排队项已有持久领取记录（尝试 {attempt}，状态 {state}），但没有对应的启动记录。{evidence}请先确认该尝试从未启动或已经结束，然后在任务卡片点「移出队列」，再点「加入队列」明确重新排队。不会删除领取记录，也不会自动重放。";
        QueueStatusText.Text = $"队列已暂停：尝试 {attempt}（{state}）已领取该排队项但没有启动记录；确认原尝试后点「移出队列」，再点「加入队列」。";
    }

    private Task HoldEntryAsync(QueueEntry entry, string detail)
    {
        detail = TaskBookOps.ClampDetail(detail);
        if (entry.Detail == detail) { QueueStatusText.Text = detail; return Task.CompletedTask; }
        return UpdateTasksAsync(book =>
        {
            var target = book.Queue.SingleOrDefault(candidate => candidate.Id == entry.Id);
            if (target is not null) target.Detail = detail;
        }, "队列状态保存失败");
    }

    /// <summary>Registers or updates the session of the last terminal attempt; the dialog value is read on the UI thread.</summary>
    private async Task RegisterSessionAsync(Guid id)
    {
        if (taskBook is null) return;
        var task = taskBook.Tasks.SingleOrDefault(candidate => candidate.Id == id);
        var run = TaskBookOps.LastRun(taskBook, id);
        if (task is null || run is null) { ErrorText.Text = "任务还没有可登记会话的尝试。"; return; }
        if (!run.Terminal) { ErrorText.Text = "上一次尝试仍在进行或结果未确认，结束后才能登记会话。"; return; }
        var profile = task.Profile.Copy();
        if (profile.Arguments.Any(IsResumeArgument)) { ErrorText.Text = "任务参数已包含续接/继续参数，请先在任务编辑中移除后再登记会话。"; return; }
        string fingerprint;
        WorkspaceIdentity workspace;
        try
        {
            (_, fingerprint, workspace) = await Task.Run(() =>
            {
                var spec = Commands.Resolve(task.AgentId, profile);
                return (spec, AgentProbe.Fingerprint(spec.Executable), PhysicalDirectory.Workspace(PhysicalDirectory.Resolve(Path.GetFullPath(task.Project)).Path));
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法核对任务命令：" + e.Message; return; }
        if (!string.Equals(run.WorkspaceKey, workspace.Key, StringComparison.Ordinal)) { ErrorText.Text = "上次尝试的工作目录与当前任务不一致，不能登记续接。"; return; }
        if (!string.Equals(run.ProgramFingerprint, fingerprint, StringComparison.Ordinal)) { ErrorText.Text = "Agent 程序已升级或更换，不能登记旧能力。"; return; }
        AgentCapability capability;
        try { capability = await AgentProbe.ProbeCapabilityAsync(task.AgentId, profile, task.Project, TimeSpan.FromSeconds(8)); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法核对 Agent 版本，不能登记会话：" + e.Message; return; }
        var preset = ResumePreset(capability, task.AgentId);
        if (preset is null) { ErrorText.Text = $"当前 {Agents.Get(task.AgentId).Name} 版本（{capability.Version ?? "未知"}）没有已验证的续接参数。"; return; }
        var ask = new FormWindow(this, "登记会话", [("session", preset.ValueLabel ?? "会话标识", run.SessionRef ?? "", false)],
            $"会话登记到第 {run.AttemptId.ToString("N")[..8]} 次尝试；工作目录、程序指纹与已验证版本 {capability.Version} 会在续接派发时再次核对。取消不改变任何任务。");
        if (ask.ShowDialog() != true) return;
        var session = ask.Values["session"].Trim();
        if (SessionProblem(session) is { Length: > 0 } problem) { ErrorText.Text = problem; return; }
        if (await UpdateTasksAsync(book => TaskBookOps.RegisterSession(book, run.AttemptId, session, workspace.Key, fingerprint), "会话登记失败"))
            QueueStatusText.Text = "已登记会话；续接会核对工作目录、程序指纹和已验证版本。";
    }

    private async Task ResumeTaskAsync(Guid id)
    {
        if (taskBook is null) return;
        var task = taskBook.Tasks.SingleOrDefault(candidate => candidate.Id == id);
        var previous = TaskBookOps.LastRun(taskBook, id);
        if (task is null || previous is null) { ErrorText.Text = "任务还没有可续接的尝试。"; return; }
        if (!previous.Terminal) { ErrorText.Text = "上一次尝试仍在进行或结果未确认。"; return; }
        if (previous.SessionRef is not { Length: > 0 } registered) { ErrorText.Text = "该尝试还没有登记会话，请先使用「登记会话」。"; return; }
        var profile = task.Profile.Copy();
        if (profile.Arguments.Any(IsResumeArgument)) { ErrorText.Text = "任务参数已包含续接/继续参数，请先在任务编辑中移除后再续接。"; return; }
        string fingerprint;
        WorkspaceIdentity workspace;
        try { (_, fingerprint, workspace) = await Task.Run(() => { var spec = Commands.Resolve(task.AgentId, profile); return (spec, AgentProbe.Fingerprint(spec.Executable), PhysicalDirectory.Workspace(PhysicalDirectory.Resolve(Path.GetFullPath(task.Project)).Path)); }); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法准备续接命令：" + e.Message; return; }
        var (allowed, reason) = QueuePolicy.CanResume(previous, workspace.Key, fingerprint, taskBook.Attempts);
        if (!allowed) { ErrorText.Text = reason; return; }
        AgentCapability capability;
        try { capability = await AgentProbe.ProbeCapabilityAsync(task.AgentId, profile, task.Project, TimeSpan.FromSeconds(8)); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法核对 Agent 版本，不能续接：" + e.Message; return; }
        var preset = ResumePreset(capability, task.AgentId);
        if (preset is null) { ErrorText.Text = $"当前 {Agents.Get(task.AgentId).Name} 版本（{capability.Version ?? "未知"}）没有已验证的续接参数。"; return; }
        var ask = new FormWindow(this, "续接会话", [("session", preset.ValueLabel ?? "会话标识", registered, false)],
            $"只续接同一次尝试的工作目录与程序指纹；已验证版本 {capability.Version}。修改标识会先把该尝试重新登记，再与入队在同一事务保存；取消不改变任何任务。");
        if (ask.ShowDialog() != true) return;
        var session = ask.Values["session"].Trim();
        if (SessionProblem(session) is { Length: > 0 } problem) { ErrorText.Text = problem; return; }
        try { profile.Arguments = [.. profile.Arguments, .. preset.Fill(session).Arguments]; }
        catch (InvalidDataException e) { ErrorText.Text = e.Message; return; }
        CommandSpec resumeCommand;
        try { resumeCommand = await Task.Run(() => Commands.Resolve(task.AgentId, profile)); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法准备续接命令：" + e.Message; return; }
        var resumeFingerprint = await Task.Run(() => AgentProbe.Fingerprint(resumeCommand.Executable));
        if (!string.Equals(resumeFingerprint, fingerprint, StringComparison.Ordinal)) { ErrorText.Text = "准备续接命令期间 Agent 程序已变化，请稍后重试。"; return; }
        if (!await UpdateTasksAsync(book =>
        {
            TaskBookOps.RegisterSession(book, previous.AttemptId, session, workspace.Key, resumeFingerprint);
            TaskBookOps.Enqueue(book, id, resumeCommand, resumeFingerprint, previous.AttemptId, session);
        }, "续接入队失败")) return;
        if (!queueEnabled) { queueEnabled = true; taskTimer.Start(); RenderTasks(); }
        QueueStatusText.Text = "续接已入队；执行队列会重新核对登记会话、工作目录与程序指纹。";
        await PumpQueueAsync();
    }

    private static ArgumentPreset? ResumePreset(AgentCapability capability, string agentId) =>
        AgentPresets.For(agentId, capability.Version).FirstOrDefault(candidate => candidate.Semantic == "resume-session" && candidate.NeedsValue);

    private static string SessionProblem(string session) =>
        session.Length == 0 ? "会话标识不能为空。"
        : session.Length > 200 ? "会话标识不能超过 200 个字符。"
        : session.Contains('\0') ? "会话标识不能包含 NUL。"
        : "";

    private static bool IsResumeArgument(string argument)
    {
        var token = argument.TrimStart('-');
        return token.Equals("resume", StringComparison.OrdinalIgnoreCase) || token.Equals("continue", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("resume=", StringComparison.OrdinalIgnoreCase) || token.StartsWith("continue=", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameCommand(CommandSpec left, CommandSpec right) =>
        string.Equals(left.Executable, right.Executable, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Adapter, right.Adapter, StringComparison.Ordinal)
        && left.Arguments.SequenceEqual(right.Arguments);
}
