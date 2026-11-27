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
    // Workspaces dialog is open (may run an explicit cleanup). While set, the queue pump must not dispatch:
    // cleanup checks occupancy against the monitor, and a launch landing mid-flow would invalidate them (advisory).
    private bool workspaceCleanupActive;
    private bool queuePausedByRecovery;
    private bool renderingTasks;
    private bool queueEditing;
    private HashSet<Guid>? queueScopeTaskIds;
    private Task queuePumpTask = Task.CompletedTask;
    private CancellationTokenSource? queueCancellation;

    private async Task LoadTasksAsync()
    {
        try
        {
            taskBook = await Task.Run(taskStore.Load);
            if (!queueEnabled) queueScopeTaskIds = taskBook.LastDispatchTaskIds?.ToHashSet();
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
            try { taskBook = await Task.Run(taskStore.Load); taskLoadError = ""; } catch (Exception reload) when (reload is IOException or JsonException or InvalidDataException) { taskBook = null; taskLoadError = reload.Message; }
            RenderTasks();
            return false;
        }
    }

    private void RenderTasks()
    {
        renderingTasks = true;
        try { RenderTaskCards(); } finally { renderingTasks = false; UpdateQuickQueueButton(); RenderSessions(); RenderRail(); }
    }

    private void RenderTaskCards()
    {
        TaskList.Children.Clear();
        QueueList.Children.Clear();
        QueueResumeBanner.Visibility = Visibility.Collapsed;
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
        TaskHeading.Text = $"任务队列 · {book.Tasks.Count(task => !task.Archived)} 张 · 待执行 {book.Queue.Count} · 归档 {book.Tasks.Count(task => task.Archived)}";
        if (book.Attempts.Count >= 900) TaskHeading.Text += $" · 尝试记录 {book.Attempts.Count}/{TaskSchema.MaxAttempts}，可从历史与诊断迁移";
        TaskEmptyText.Text = "还没有任务。新建任务后加入队列，启动器会按优先级依次交给 Terminal。";
        TaskEmptyText.Visibility = book.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hold = TaskUsability.OrderedQueue(book).FirstOrDefault(entry => InQueueScope(entry.TaskId) && !entry.Deferred && entry.Detail.Length > 0);
        QueueStatusText.Text = queueEnabled ? hold is null ? $"队列运行中 · 每 {TaskPollSeconds} 秒检查一次" : $"队列等待：{hold.Detail}" : "队列已暂停";
        QueueToggleButton.Content = queueEnabled ? "暂停队列" : "执行队列";
        QueueToggleButton.IsEnabled = !closeRequested && !quickQueueImporting && !queueEditing;
        TaskConcurrency.SelectedIndex = Math.Clamp(book.MaxRunning - 1, 0, 3);
        TaskConcurrency.IsEnabled = !closeRequested && !quickQueueImporting;
        RenderQueueEntries(book);
        if (!queueEnabled && book.Queue.Count > 0)
        {
            QueueResumeBanner.Visibility = Visibility.Visible;
            var selected = book.Queue.Count(entry => InQueueScope(entry.TaskId));
            QueueResumeText.Text = queueScopeTaskIds is null ? $"上次队列有 {book.Queue.Count} 项等待。继续上次队列会执行这些项；本次勾选项用下方启动按钮。"
                : $"上次批次还有 {selected} 项等待，其他 {book.Queue.Count - selected} 项保持暂停。本次勾选项用下方启动按钮。";
            QueueResumeButton.IsEnabled = selected > 0;
        }
        if (queueEnabled && queueScopeTaskIds is not null) QueueStatusText.Text = "仅执行本批次 · 并发 1；其他等待项保持原状。" + (hold is null ? "" : " " + hold.Detail);
        var search = TaskSearch.Text.Trim();
        var visible = book.Tasks.Where(task => (TaskFilter.SelectedIndex == 2 || task.Archived == (TaskFilter.SelectedIndex == 1))
        && (search.Length == 0 || task.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || task.Project.Contains(search, StringComparison.OrdinalIgnoreCase) || task.Note.Contains(search, StringComparison.OrdinalIgnoreCase) || Agents.Get(task.AgentId).Name.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (visible.Length == 0 && book.Tasks.Count > 0) { TaskEmptyText.Text = "当前范围没有匹配任务，可更换范围或清空搜索。"; TaskEmptyText.Visibility = Visibility.Visible; }
        foreach (var task in visible.OrderByDescending(task => task.Priority).ThenBy(task => task.UpdatedAt))
            TaskList.Children.Add(TaskCardView(book, task));
    }

    private Border TaskCardView(TaskBook book, TaskCard task)
    {
        var last = TaskBookOps.LastRun(book, task.Id);
        var queued = book.Queue.FirstOrDefault(entry => entry.TaskId == task.Id);
        var state = task.Archived ? "已归档" : queued is not null ? queued.Deferred ? "已暂缓" : "排队中" : last is null ? "未开始" : last.State switch
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
        var priority = new TextBlock { Text = $"优先级 {task.Priority}", ToolTip = $"配置修订 {task.Revision}", Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(priority, Dock.Right); header.Children.Add(priority);
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Text = task.Project, ToolTip = task.Project, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 4, 0, 0) });
        if (task.Note.Length > 0) stack.Children.Add(new TextBlock { Text = "备注：" + task.Note, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
        if (task.Archived) { AddTaskUtilityActions(stack, task); card.Child = stack; return card; }
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
        var compare = TaskButton("复制为对比组 " + task.Name, "复制为对比组");
        compare.Click += async (_, _) => await GuardAsync(() => CopyAsCompareGroupAsync(task.Id));
        actions.Children.Add(compare);
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
        if (last is { Terminal: true })
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
        AddTaskUtilityActions(stack, task);
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
    private async void QueueToggleClick(object sender, RoutedEventArgs e) => await GuardAsync(ToggleQueueAsync);

    private async Task ToggleQueueAsync()
    {
        if (closeRequested || quickQueueImporting || queueEditing || readOnly || taskBook is null) return;
        if (!queueEnabled)
        {
            queueEditing = true;
            try
            {
                if (!await UpdateTasksAsync(book => book.LastDispatchTaskIds = null, "执行范围保存失败")) return;
                queueScopeTaskIds = null;
                queueEnabled = true;
            }
            finally { queueEditing = false; }
        }
        else queueEnabled = false;
        if (queueEnabled) taskTimer.Start(); else { taskTimer.Stop(); queueCancellation?.Cancel(); }
        RenderTasks();
        if (queueEnabled) await PumpQueueAsync();
    }

    private AgentProfile ConfiguredTaskProfile(string agentId) =>
        config.Draft.Slots.OrderByDescending(slot => slot.Enabled && slot.AgentId == agentId)
            .Select(slot => slot.Profiles.GetValueOrDefault(agentId)).FirstOrDefault(profile => profile is not null && (profile.Executable.Length > 0 || profile.Arguments.Length > 0))?.Copy() ?? new();

    private async Task NewTaskAsync()
    {
        if (taskBook is null) { ErrorText.Text = taskLoadError; return; }
        if (taskBook.Tasks.Count(task => !task.Archived) >= TaskSchema.MaxTasks) { ErrorText.Text = "活动任务已达 200 张，请用任务卡片的「归档」释放名额；历史会保留。"; return; }
        var agent = config.Draft.Slots.FirstOrDefault(slot => slot.Enabled && Agents.IsSupported(slot.AgentId))?.AgentId ?? "omp";
        await EditTaskFormAsync(null, agent, ConfiguredTaskProfile(agent));
    }

    private static readonly (string,string)[] DeliveryChoices=[("manual","手动交付：打开 Agent 后粘贴"),("interactive","发送任务：保留交互终端"),("automatic","自动执行：发送任务并在完成后退出")];

    private Task EditTaskAsync(Guid id)
    {
        var task = taskBook?.Tasks.SingleOrDefault(candidate => candidate.Id == id);
        return task is null ? Task.CompletedTask : EditTaskFormAsync(task, task.AgentId, task.Profile.Copy());
    }

    private Task EditTaskFormAsync(TaskCard? task, string agentId, AgentProfile initialProfile)
    {
        var modeChoices = new List<(string,string)> { ("isolated", "独立工作目录（可并行）"), ("shared", "共享项目目录") };
        if (task?.DirectoryMode == "prepared") modeChoices.Add(("prepared", "沿用此任务已准备的工作目录"));
        var dialog = new FormWindow(this, task is null ? "新建任务" : "任务 · " + task.Name,
            [("name", "任务名称", task?.Name ?? "", false), ("project", "项目目录", task?.Project ?? config.Draft.Project, false), ("agent", "Agent", agentId, false),
             ("exe", "Agent 程序（留空自动查找）", initialProfile.Executable, false), ("args", "Agent 参数", "", false),
             ("priority", "优先级 0–100", (task?.Priority ?? 0).ToString(CultureInfo.InvariantCulture), false), ("mode", "目录策略", task?.DirectoryMode ?? "isolated", false),
             ("delivery", "任务交付方式", task?.DeliveryMode ?? "manual", false), ("description", "任务说明", task?.Description ?? "", true),
             ("acceptance", "验收项（每行一项）", string.Join(Environment.NewLine, task?.Acceptance.Select(item => item.Text) ?? []), true)],
            "程序和参数可从当前槽位继承，也可单独选择。切换 Agent 后请点击「继承所选 Agent 配置」。独立任务会创建 worktree，后续在该目录续接和验收。自动交付内容会保存在本地记录，请勿填写密钥。",
            choices: new()
            {
                ["agent"] = [.. AgentChoices().Select(choice => (choice.Id, choice.Label))],
                ["delivery"] = DeliveryChoices,
                ["mode"] = [.. modeChoices]
            },
            suggestions: new() { ["project"] = [.. config.RecentProjects], ["exe"] = (discoveredInstalls.GetValueOrDefault(agentId) ?? []).Append(initialProfile.Executable).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() },
            argumentLists: new() { ["args"] = initialProfile.Arguments },
            actions: new() { ["继承所选 Agent 配置"] = owner =>
            {
                var form = (FormWindow)owner; var profile = ConfiguredTaskProfile(form.ReadValue("agent"));
                form.SetValue("exe", profile.Executable); form.SetArguments("args", profile.Arguments);
                form.SetNote("已继承当前槽位的所选 Agent 程序和参数。保存前可继续修改；留空程序会自动查找。");
            } });
        dialog.ApplyAsync = async form =>
        {
            var project = form.Values["project"].Trim();
            if (!Path.IsPathFullyQualified(project) || !Directory.Exists(project)) throw new InvalidDataException("任务项目必须是存在的本地绝对目录。");
            if (!int.TryParse(form.Values["priority"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority) || priority is < 0 or > 100) throw new InvalidDataException("优先级必须是 0–100 的整数。");
            var executable = form.Values["exe"].Trim();
            if (executable.Length > 0 && (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))) throw new InvalidDataException("Agent 程序必须是存在的绝对文件路径，或留空自动查找。");
            var profile = new AgentProfile { Executable = executable, Arguments = [.. form.ArgumentValues["args"]] };
            var acceptance = form.Values["acceptance"].Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            var name = form.Values["name"].Trim(); var agent = form.Values["agent"]; var mode = form.Values["mode"];
            var description = form.Values["description"]; var delivery = form.Values["delivery"];
            if (!Agents.IsSupported(agent)) throw new InvalidDataException("请选择受支持的 Agent。");
            var saved = await UpdateTasksAsync(book =>
            {
                if (task is null)
                {
                    var created = TaskBookOps.Create(book, name, Path.GetFullPath(project), agent, profile, description, acceptance, mode, config.Draft.Resource, priority);
                    created.DeliveryMode = delivery;
                }
                else
                {
                    var previous = book.Tasks.Single(candidate => candidate.Id == task.Id);
                    if (previous.Revision != task.Revision) throw new IOException("任务已被其他操作修改，请核对后重新打开编辑；当前输入仍保留。");
                    var checkedTexts = previous.Acceptance.Where(item => item.Checked).Select(item => item.Text).ToHashSet(StringComparer.Ordinal);
                    TaskBookOps.Edit(book, task.Id, draft =>
                    {
                        draft.Name = name; draft.Project = Path.GetFullPath(project); draft.AgentId = agent; draft.Profile = profile;
                        draft.Priority = priority; draft.DirectoryMode = mode; draft.Description = description; draft.DeliveryMode = delivery;
                        draft.Acceptance = acceptance.Select(text => new AcceptanceItem { Id = Guid.NewGuid().ToString("N"), Text = text, Checked = checkedTexts.Contains(text) }).ToList();
                    });
                }
            }, "任务保存失败");
            if (!saved) form.SetError(ErrorText.Text);
            else QueueStatusText.Text = task is null ? $"已创建任务：{name}。加入队列后才会启动。" : "任务修改已保存。";
            return saved;
        };
        dialog.ShowDialog();
        return Task.CompletedTask;
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
            var profile=await TaskDelivery.PrepareAsync(task);
            (command, fingerprint) = await Task.Run(() =>
            {
                var spec = Commands.Resolve(task.AgentId, profile);
                return (spec, AgentProbe.Fingerprint(spec));
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
        if (!queueEnabled || closeRequested || taskBook is null || taskLoadError.Length > 0 || queuePumping || queueEditing || readOnly || workspaceCleanupActive) return Task.CompletedTask;
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
        if (taskBook is null || taskBook.Queue.Any(entry => InQueueScope(entry.TaskId)) || taskBook.Attempts.Any(run => !run.Terminal && InQueueScope(run.TaskId))) return false;
        queueEnabled = false;
        taskTimer.Stop();
        RenderTasks();
        QueueStatusText.Text = queueScopeTaskIds is null ? "队列已空，已停止轮询；新增任务后需明确执行。" : "本批次已结束，其他等待项保持暂停；点「执行队列」可执行全部。";
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
            if (taskBook is not null) { RenderQueueEntries(taskBook); RenderRail(); }
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
            if (TaskOutcome.From(entry!) is { } outcome)
            {
                await UpdateTasksAsync(book => TaskBookOps.FinishRun(book, run.AttemptId, outcome.State, outcome.Detail), "任务状态更新失败");
                continue;
            }
            if (entry!.State == "Pending" && DateTimeOffset.UtcNow - entry.Created > ClaimTimeout)
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
        if (book.Attempts.Count >= TaskSchema.MaxAttempts) { QueueStatusText.Text = TaskBookOps.AttemptCapacityMessage; queueEnabled = false; taskTimer.Stop(); return; }
        try
        {
            var ledger = await Task.Run(TaskRecoveryReader.ReadLedger, cancellationToken);
            if (ledger?.TaskClaims.Count >= LedgerMigration.MaxTaskClaims)
            { QueueStatusText.Text = "领取证据已达 1200 条上限，尚未准备工作目录或新增预约。迁移任务历史不会减少领取证据；请导出记录后联系维护者迁移控制账本。"; queueEnabled = false; taskTimer.Stop(); return; }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { QueueStatusText.Text = "领取记录无法核对，尚未准备或预约：" + e.Message; return; }
        await RefreshAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (status?.Ok != true || status.Memory is null) { QueueStatusText.Text = "监控状态不可用，队列暂不启动。"; return; }
        if (status.AdmissionHardBlockedReason is { Length: > 0 } blocked) { QueueStatusText.Text = "队列暂停：" + blocked; return; }
        if (book.Queue.Count == 0)
        {
            QueueStatusText.Text = "队列为空。";
            return;
        }
        if (queueScopeTaskIds is not null && book.Attempts.Any(run => !run.Terminal && InQueueScope(run.TaskId))) { QueueStatusText.Text = "本批次已有一项执行中，等待结束后再启动下一项。"; return; }
        var candidates = book.Queue.Where(entry => InQueueScope(entry.TaskId)).Select(entry => new QueueCandidate(entry.Id, entry.TaskId, entry.TaskRevision, entry.Priority, entry.Sequence, entry.EnqueuedAt, [.. entry.DependsOn], entry.Deferred)).ToArray();
        var decision = QueuePolicy.Decide(candidates, book.Attempts.Count(run => !run.Terminal), book.MaxRunning,
            id => book.Attempts.Any(run => run.TaskId == id && run.State == "Completed"));
        if (decision.Next is null)
        {
            QueueStatusText.Text = decision.Waiting.Count > 0 ? "队列等待：" + decision.Waiting[0].Reason : "队列为空。";
            return;
        }
        var entry = book.Queue.Single(candidate => candidate.Id == decision.Next.EntryId);
        var task = book.Tasks.Single(candidate => candidate.Id == entry.TaskId);
        if(!Agents.IsSupported(task.AgentId)){await HoldEntryAsync(entry,"Agent 类型已移除，仅支持 Omp、Codex 和 Claude Code；请编辑任务改选受支持 Agent 后重新入队。");return;}
        if (task.Revision != entry.TaskRevision) { await HoldEntryAsync(entry, "任务已被修改，请重新入队。"); return; }
        // The latch is global, but it only stops the entry whose own frozen settings ask for a conservative policy; a
        // practical/custom entry proceeds to prepare and is re-authorized (or refused) by the monitor's reserve.
        if (status.AdmissionPressure && string.Equals(entry.Resource.Mode, "conservative", StringComparison.Ordinal))
        { QueueStatusText.Text = "队列暂停：" + PressureMessage; return; }
        // The monitor's reserve is still the authoritative cap; this only keeps an at-cap tick from preparing a worktree it must release.
        if (ConcurrencyBlocked(1)) { await HoldEntryAsync(entry, ConcurrencyLimitMessage); return; }

        // A durable monitor claim is execution authority for this queue item. A claimed item with no recorded task run
        // must never be prepared again; this is a read-only inspection of the existing ledger, not a new authorization.
        var claim = await Task.Run(() => InspectQueueClaim(entry.Id), cancellationToken);
        if (claim.Failure.Length > 0 || claim.Claim is not null) { PauseQueueForClaim(entry, claim); return; }

        var profile = task.Profile.Copy();
        var resume = entry.ResumeOf is not null || !string.IsNullOrWhiteSpace(entry.SessionRef);
        if(!resume)
        {
            try{profile=await TaskDelivery.PrepareAsync(task,cancellationToken);}
            catch(Exception e)when(e is not OutOfMemoryException&&e is not OperationCanceledException){await HoldEntryAsync(entry,"任务交付检查失败："+e.Message);return;}
        }
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
        var fingerprint = await Task.Run(() => AgentProbe.Fingerprint(resolved));
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
            var preset = await ResumeCapability.CheckAsync(task.AgentId,profile,entry.Project,capability);
            if (preset is null) { await HoldEntryAsync(entry, $"当前版本（{capability.Version ?? "未知"}）没有已验证的续接参数。"); return; }
            try
            {
                profile.Arguments = [.. profile.Arguments, .. preset.Fill(entry.SessionRef).Arguments];
                resolved = await Task.Run(() => Commands.Resolve(task.AgentId, profile));
                fingerprint = await Task.Run(() => AgentProbe.Fingerprint(resolved));
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
        var forceIsolated = TaskBookOps.RequiresIsolatedWorkspace(entry);
        var pendingBudget = new BudgetItem(entry.Id, "agent", policy.PlanningBytes, policy.JobMemoryLimit,
            policy.PoolMode == ResourcePolicy.PoolShared ? entry.Id : null,
            policy.PoolMode == ResourcePolicy.PoolShared ? policy.PoolMemoryLimit : null);
        BudgetItem[] requested = forceIsolated
            ? [pendingBudget, new(Guid.Empty, "git", policy.GitPlanningBytes, policy.GitJobMemoryLimit)] : [pendingBudget];
        var reserved = ResourcePolicy.ReservedFor(status);
        var admission = ResourcePolicy.Evaluate(status.Memory, policy, reserved, requested);
        if (admission.Kind == AdmissionKind.Deny) { await HoldEntryAsync(entry, "资源不足：" + admission.Reason); return; }

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
                _ => Task.FromResult(false), cancellationToken, activeWorkspaces, forceIsolated, entry.QuickLaunch?.WorkspaceKey, config))).ToArray();
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
        if (status?.Ok != true || status.Memory is null || status.AdmissionHardBlockedReason is { Length: > 0 })
        { await HoldEntryAsync(entry, status?.AdmissionHardBlockedReason ?? "准备后监控状态不可用，等待重新核验。"); return; }
        if (status.AdmissionPressure && string.Equals(entry.Resource.Mode, "conservative", StringComparison.Ordinal))
        { await HoldEntryAsync(entry, PressureMessage); return; }
        reserved = ResourcePolicy.ReservedFor(status);
        admission = ResourcePolicy.Evaluate(status.Memory, policy, reserved, [ResourcePolicy.BudgetOf(plan, plan.PoolId is null ? null : policy.PoolMemoryLimit)]);
        if (admission.Kind == AdmissionKind.Deny) { await HoldEntryAsync(entry, "资源不足：" + admission.Reason); return; }

        Response reserve;
        try { reserve = await Ipc.ReserveWithCleanupAsync([plan], cancellationToken, null, policy, null, ResourcePolicy.PolicyHash(policy), ResourcePolicy.PlanDigest([plan]), null, entry.Id); }
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
                TaskBookOps.StartRun(editor, entry, plan.Id, plan.BatchId, plan.Workspace.Key, entry.ResumeOf, entry.SessionRef, plan.Cwd);
            }, "启动记录失败");
            if (!recorded) return;
            cancellationToken.ThrowIfCancellationRequested();
            QueueStatusText.Text = $"正在提交任务：{task.Name}";
            cancellationToken.ThrowIfCancellationRequested();
            await SubmitPanesAsync("",[plan],"vertical");
            submitted = true;
            QueueStatusText.Text = $"已提交任务：{task.Name}。进程出现不代表 Agent 已就绪。";
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

    private async Task ArchiveTaskHistoryAsync()
    {
        if (queuePumping || queueEditing || closeRequested) throw new IOException("正在核对派发，请稍后再迁移历史。");
        using var gate = taskStore.TryAcquireQueueGate() ?? throw new IOException("另一个窗口正在派发，请稍后重试。");
        queueEnabled = false; taskTimer.Stop(); queueEditing = true;
        try
        {
            var fresh = await Task.Run(taskStore.Load);
            var ledger = await Task.Run(TaskRecoveryReader.ReadLedger);
            taskBook = await Task.Run(() => taskStore.ArchiveHistory(fresh.Revision, id => TaskRecoveryReader.ReadAttempt(id, ledger)));
            QueueStatusText.Text = $"已迁移 {fresh.Attempts.Count - taskBook.Attempts.Count} 条已结束历史，当前活动尝试 {taskBook.Attempts.Count}/{TaskSchema.MaxAttempts}。历史仍可搜索与验收，队列保持暂停。";
        }
        finally { queueEditing = false; RenderTasks(); }
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
                ErrorText.Text = $"队列已暂停：尝试 {attempt} 的预约已撤销（{revoked.Detail}），但排队项的持久领取记录已保留，未写入启动记录。请点「原因与恢复」核对身份，再明确重新入队；不会自动重放。";
                QueueStatusText.Text = $"队列已暂停：尝试 {attempt} 未写入启动记录；请打开「原因与恢复」核对并重新入队。";
                return;
            }
            QueueStatusText.Text = "预约已领取或撤销结果未确认，队列已暂停；请核对运行记录，不会自动重放。";
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            QueueStatusText.Text = "撤销结果未确认，队列已暂停；请核对运行记录：" + e.Message;
        }
        var recoveryReason = QueueStatusText.Text;
        queueEnabled = false;
        taskTimer.Stop();
        RenderTasks();
        QueueStatusText.Text = recoveryReason;
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
            ErrorText.Text = $"任务「{name}」的旧启动记录暂不能核对，队列已暂停。请点该项「原因与恢复」查看证据和可用操作。{observation.Failure}";
            QueueStatusText.Text = "队列已暂停；请打开「原因与恢复」核对旧启动记录。";
            return;
        }
        var claim = observation.Claim!;
        var run = taskBook?.Attempts.FirstOrDefault(candidate => candidate.AttemptId == claim.AttemptId);
        var state = run?.State ?? observation.Original?.State ?? "未知或记录已裁剪";
        var attempt = claim.AttemptId.ToString("N")[..8];
        var evidence = observation.Original?.Detail is { Length: > 0 } detail ? $"最近记录：{detail}。" : "";
        if (run is null && observation.Original is null)
        {
            ErrorText.Text = $"任务「{name}」的原尝试 {attempt} 状态未知，队列已暂停。请打开「原因与恢复」查阅保留的身份归档和回执；证明原尝试结束后才能重新入队。";
            QueueStatusText.Text = $"队列已暂停：尝试 {attempt} 结果未知，请打开「原因与恢复」。";
            return;
        }
        ErrorText.Text = $"任务「{name}」有一次旧启动记录（{attempt}，{state}），尚未对应到任务历史，队列已暂停。{evidence}请点「原因与恢复」核对，符合条件时可直接重新入队。";
        QueueStatusText.Text = $"队列已暂停：尝试 {attempt}（{state}），请打开「原因与恢复」核对并重新入队。";
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
                return (spec, AgentProbe.Fingerprint(spec), PhysicalDirectory.Workspace(PhysicalDirectory.Resolve(Path.GetFullPath(task.Project)).Path));
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法核对任务命令：" + e.Message; return; }
        if (!string.Equals(run.WorkspaceKey, workspace.Key, StringComparison.Ordinal)) { ErrorText.Text = "上次尝试的工作目录与当前任务不一致，不能登记续接。"; return; }
        var changedProgram=run.ProgramFingerprint!=fingerprint&&run.ReverifiedFingerprint!=fingerprint;
        AgentCapability capability;
        try { capability = await AgentProbe.ProbeCapabilityAsync(task.AgentId, profile, task.Project, TimeSpan.FromSeconds(8)); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法核对 Agent 版本，不能登记会话：" + e.Message; return; }
        var preset = await ResumeCapability.CheckAsync(task.AgentId,profile,task.Project,capability);
        if (preset is null) { ErrorText.Text = $"当前 {Agents.Get(task.AgentId).Name} 版本（{capability.Version ?? "未知"}）没有已验证的续接参数。"; return; }
        var sessions=await Task.Run(()=>SessionDiscovery.Find(task.AgentId,task.Project));
        var actions=new Dictionary<string,Action<Window>>();
            if(task.AgentId=="omp")actions["选择会话文件…"]=owner=>{var pick=new Microsoft.Win32.OpenFileDialog{Title="选择原会话文件",Filter="会话文件 (*.jsonl)|*.jsonl",CheckFileExists=true};if(pick.ShowDialog(owner)==true)((FormWindow)owner).SetValue("session",pick.FileName);};
        var ask = new FormWindow(this, changedProgram?"重新核对程序并登记会话":"登记会话", [("session", preset.ValueLabel ?? "会话标识", run.SessionRef ?? "", false)],
            (changedProgram?"Agent 程序已经升级或更换。当前版本的续接参数已重新检查；点击登记表示确认使用当前程序打开原会话。原始启动指纹仍保留。\n\n":"")+sessions.Note+"\n"+string.Join("\n",sessions.Sessions.Take(8).Select(s=>$"{s.Modified.LocalDateTime:g} · {s.Reference}"))+"\n\n"+TaskUsability.SessionGuide(task.AgentId) + $"\n\n对应尝试：{run.AttemptId.ToString("N")[..8]}；已验证版本 {capability.Version}。登记前请确认原会话已经结束。",
            suggestions:new(){["session"]=sessions.Sessions.Select(s=>s.Reference).ToArray()},actions:actions,applyLabel: changedProgram?"确认当前程序并登记":"核对并登记");
        ask.ApplyAsync = async form =>
        {
            var session = form.Values["session"].Trim();
            if (SessionProblem(session) is { Length: > 0 } problem) throw new InvalidDataException(problem);
            if (AgentProbe.Fingerprint(await Task.Run(() => Commands.Resolve(task.AgentId, profile))) != fingerprint) throw new IOException("登记期间程序已变化，请重新核对。");
            var saved = await UpdateTasksAsync(book => TaskBookOps.RegisterSession(book, run.AttemptId, session, workspace.Key, fingerprint, reverified: changedProgram), "会话登记失败");
            if (!saved) form.SetError(ErrorText.Text);
            else QueueStatusText.Text = "已登记会话；续接会核对工作目录、程序指纹和已验证版本。";
            return saved;
        };
        ask.ShowDialog();
    }

    private async Task ResumeTaskAsync(Guid id)
    {
        if (taskBook is null) return;
        var task = taskBook.Tasks.SingleOrDefault(candidate => candidate.Id == id);
        var previous = TaskBookOps.LastRun(taskBook, id);
        if (task is null || previous is null) { ErrorText.Text = "任务还没有可续接的尝试。"; return; }
        if (!previous.Terminal) { ErrorText.Text = "上一次尝试仍在进行或结果未确认。"; return; }
        if (previous.SessionRef is not { Length: > 0 })
        {
            await RegisterSessionAsync(id);previous=taskBook is null?null:TaskBookOps.LastRun(taskBook,id);
            if(previous?.SessionRef is not {Length:>0})return;
        }
        var registered=previous.SessionRef!;
        var profile = task.Profile.Copy();
        if (profile.Arguments.Any(IsResumeArgument)) { ErrorText.Text = "任务参数已包含续接/继续参数，请先在任务编辑中移除后再续接。"; return; }
        string fingerprint;
        WorkspaceIdentity workspace;
        try { (_, fingerprint, workspace) = await Task.Run(() => { var spec = Commands.Resolve(task.AgentId, profile); return (spec, AgentProbe.Fingerprint(spec), PhysicalDirectory.Workspace(PhysicalDirectory.Resolve(Path.GetFullPath(task.Project)).Path)); }); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法准备续接命令：" + e.Message; return; }
        if(previous.ProgramFingerprint!=fingerprint&&previous.ReverifiedFingerprint!=fingerprint)
        {
            await RegisterSessionAsync(id);previous=taskBook is null?null:TaskBookOps.LastRun(taskBook,id);
            if(previous is null)return;registered=previous.SessionRef??"";
        }
        var (allowed, reason) = QueuePolicy.CanResume(previous, workspace.Key, fingerprint, taskBook!.Attempts);
        if (!allowed) { ErrorText.Text = reason; return; }
        AgentCapability capability;
        try { capability = await AgentProbe.ProbeCapabilityAsync(task.AgentId, profile, task.Project, TimeSpan.FromSeconds(8)); }
        catch (Exception e) when (e is not OutOfMemoryException) { ErrorText.Text = "无法核对 Agent 版本，不能续接：" + e.Message; return; }
        var preset = await ResumeCapability.CheckAsync(task.AgentId,profile,task.Project,capability);
        if (preset is null) { ErrorText.Text = $"当前 {Agents.Get(task.AgentId).Name} 版本（{capability.Version ?? "未知"}）没有已验证的续接参数。"; return; }
        var ask = new FormWindow(this, "续接会话", [("session", preset.ValueLabel ?? "会话标识", registered, false), ("scope", "执行范围", "batch", false)],
            TaskUsability.SessionGuide(task.AgentId) + $"\n\n续接会核对原工作目录和程序；已验证版本 {capability.Version}。默认只执行这次续接，其他等待项暂停。修改会话标识后，将与本次入队一起保存。",
            choices: new() { ["scope"] = [("batch", "只续接此任务"), ("all", "续接并执行整个队列")] }, applyLabel: "续接并入队");
        ask.ApplyAsync = async form =>
        {
            var session = form.Values["session"].Trim();
            var resumeWholeQueue = form.Values["scope"] == "all";
            if (SessionProblem(session) is { Length: > 0 } problem) throw new InvalidDataException(problem);
            var resumeProfile = profile.Copy();
            resumeProfile.Arguments = [.. resumeProfile.Arguments, .. preset.Fill(session).Arguments];
            var resumeCommand = await Task.Run(() => Commands.Resolve(task.AgentId, resumeProfile));
            var resumeFingerprint = await Task.Run(() => AgentProbe.Fingerprint(resumeCommand));
            if (!string.Equals(resumeFingerprint, fingerprint, StringComparison.Ordinal)) throw new IOException("准备续接命令期间 Agent 程序已变化，请稍后重试。");
            var saved = await UpdateTasksAsync(book =>
            {
                TaskBookOps.RegisterSession(book, previous.AttemptId, session, workspace.Key, resumeFingerprint);
                TaskBookOps.Enqueue(book, id, resumeCommand, resumeFingerprint, previous.AttemptId, session);
                book.LastDispatchTaskIds = resumeWholeQueue ? null : [id];
            }, "续接入队失败");
            if (!saved) form.SetError(ErrorText.Text);
            return saved;
        };
        if (ask.ShowDialog() != true) return;
        queueScopeTaskIds = taskBook!.LastDispatchTaskIds?.ToHashSet();
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
            || token.StartsWith("resume=", StringComparison.OrdinalIgnoreCase) || token.StartsWith("continue=", StringComparison.OrdinalIgnoreCase)
            || token.Split('=')[0] is "session" or "session-id" or "fork" or "restore" || argument is "-r" or "-c";
    }

    private static bool SameCommand(CommandSpec left, CommandSpec right) =>
        string.Equals(left.Executable, right.Executable, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Adapter, right.Adapter, StringComparison.Ordinal)
        && left.Arguments.SequenceEqual(right.Arguments);
}
