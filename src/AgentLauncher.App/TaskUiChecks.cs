#if INTEGRATION_TESTS
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
    private static string FileMutexFor(string path) => "Local\\AgentLauncher-file-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
    // The store guard means "retry later"; a poll must not treat a concurrent writer as a fatal check error.
    private static T? ReadStoreSettled<T>(string path) where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return Store.Read<T>(path); }
            catch (IOException e) when (attempt < 3 && e.Message.Contains("记录正在写入", StringComparison.Ordinal)) { Thread.Sleep(50); }
        }
    }
    private async Task RunTaskChecksAsync(Action<string, bool> check)
    {
        var originalSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        try
        {
        IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T target) yield return target;
                foreach (var nested in FindLogical<T>(child)) yield return nested;
            }
        }
        async Task<bool> WaitForPublishedTaskEditAsync(Func<TaskBook, bool> ready)
        {
            bool Published()
            {
                var persisted = taskStore.Load();
                // Disk publication precedes the event handler's dispatcher continuation and taskBook assignment.
                return taskBook is { } current && current.Revision == persisted.Revision && ready(current) && ready(persisted);
            }
            for (var i = 0; i < 50 && !Published(); i++) await Task.Delay(100);
            return Published();
        }
        var tasksPath = Store.FilePath("tasks.json");
        File.Delete(tasksPath);
        File.Delete(tasksPath + ".bak");
        var fixture = Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe");
        check("ACC-B05 task fixture program exists", File.Exists(fixture));
        var book = new TaskBook();
        var waiting = TaskBookOps.Create(book, "任务甲", Store.Root, "codex", new AgentProfile { Executable = fixture }, "", ["通过"], "shared", null, 4);
        TaskBookOps.Enqueue(book, waiting.Id, new CommandSpec(fixture, ["--fixture"], "exe"), new string('A', 64));
        var reviewed = TaskBookOps.Create(book, "任务乙", Store.Root, "codex", new AgentProfile { Executable = fixture }, "", [], "shared", null, 1);
        var reviewedRun = TaskBookOps.StartRun(book, TaskBookOps.Enqueue(book, reviewed.Id, new CommandSpec(fixture, [], "exe"), AgentProbe.Fingerprint(fixture)), Guid.NewGuid(), Guid.NewGuid(), "volume:task-check");
        reviewedRun.SessionRef = "session-check";
        TaskBookOps.FinishRun(book, reviewedRun.AttemptId, "Review", "进程已结束，等待人工验收");
        Store.Save(tasksPath, book);
        await LoadTasksAsync();
        RenderTasks();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        check("ACC-B05 task cards render with queue position and review state", TaskList.Children.Count == 2 && TaskHeading.Text.Contains("2 张") && TaskHeading.Text.Contains("待执行 1"));
        var acceptance = FindLogical<CheckBox>(TaskList).First(box => AutomationProperties.GetName(box).StartsWith("验收 ", StringComparison.Ordinal));
        acceptance.IsChecked = true;
        acceptance.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        var acceptancePublished = await WaitForPublishedTaskEditAsync(current => current.Tasks.Single(task => task.Id == waiting.Id).Acceptance[0].Checked);
        check("ACC-B05 accepting a checkbox persists without dropping the queue", acceptancePublished && taskStore.Load().Tasks.Single(task => task.Id == waiting.Id).Acceptance[0].Checked && taskStore.Load().Queue.Count == 1);
        check("ACC-B05 resume action appears only for a settled session", FindLogical<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "续接会话 任务乙")
            && !FindLogical<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "续接会话 任务甲"));
        await ToggleQueueAsync();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        check("ACC-B05 queue starts paused and only runs when asked", queueEnabled && QueueToggleButton.Content as string == "暂停队列");
        var attemptsBefore = taskStore.Load().Attempts.Count;
        // Advisory 2026-11-13: while the workspaces dialog is open (may run an explicit cleanup), the pump must not
        // dispatch — cleanup verifies occupancy against the monitor and a launch landing mid-flow invalidates it.
        workspaceCleanupActive = true;
        try
        {
            await PumpQueueAsync();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
            check("ACC-WC03 workspaces dialog blocks queue dispatch", !queuePumping && taskStore.Load().Queue.Count == 1 && taskStore.Load().Attempts.Count == attemptsBefore);
        }
        finally { workspaceCleanupActive = false; }
        var detail = "";
        for (var i = 0; i < 300; i++)
        {
            if(i==10)Store.Save(Store.FilePath("queue-check-state.json"),new{Error=ErrorText.Text,Queue=QueueStatusText.Text,busy,launchFinalizing,submittedAttempts,queuePumping,queueEnabled,closeRequested,taskLoadError,readOnly,Message=status?.Message,Blocked=status?.AdmissionBlockedReason});
            detail = taskStore.Load().Queue.Single().Detail;
            if (detail.Contains("程序或参数已变化")) break;
            if (!queuePumping) await PumpQueueAsync();
            await Task.Delay(100);
        }
        check("ACC-B06 dispatch holds a changed program instead of launching", detail.Contains("程序或参数已变化") && taskStore.Load().Attempts.Count == attemptsBefore && QueueStatusText.Text.Contains("程序或参数已变化"));
        await ToggleQueueAsync();
        check("ACC-B05 queue toggles back to paused", !queueEnabled && QueueToggleButton.Content as string == "执行队列");
        TaskConcurrency.SelectedIndex = 1;
        for (var i = 0; i < 50 && taskBook?.MaxRunning != 2; i++) await Task.Delay(100);
        check("ACC-B05 queue concurrency is adjustable within 1-4", taskStore.Load().MaxRunning == 2 && TaskConcurrency.SelectedIndex == 1);
        TaskConcurrency.SelectedIndex = 0;
        for (var i = 0; i < 50 && taskBook?.MaxRunning != 1; i++) await Task.Delay(100);
        check("ACC-B05 queue concurrency restores to one", taskStore.Load().MaxRunning == 1 && taskBook?.MaxRunning == 1);
        RenderTasks();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        var attemptsBeforeDequeue = taskStore.Load().Attempts.Count;
        var dequeueButton = FindLogical<Button>(TaskList).FirstOrDefault(button => AutomationProperties.GetName(button) == "移出队列 任务甲");
        check("ACC-B07 queued task exposes queue-only removal", dequeueButton != null);
        dequeueButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dequeuePublished = await WaitForPublishedTaskEditAsync(current => current.Queue.Count == 0);
        check("ACC-B07 removing a queued item keeps attempts and the task record",
            dequeuePublished && taskStore.Load().Queue.Count == 0 && taskStore.Load().Attempts.Count == attemptsBeforeDequeue && taskStore.Load().Tasks.Any(task => task.Id == waiting.Id));
        await RunTaskCommandChecksAsync(check);
        var retainedItem = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "retained", Store.Root, PhysicalDirectory.Workspace(Store.Root), false, new CommandSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], "exe"), ResourcePolicy.AgentBudget,SourceSlotId:Guid.NewGuid());
        var retainedRun=new TaskRun{AttemptId=retainedItem.Id,BatchId=retainedItem.BatchId,TaskId=retainedItem.SourceSlotId!.Value,WorkspaceKey=retainedItem.Workspace.Key};
        check("ACC-L01 task recovery only accepts its own attempt batch task and workspace",AttemptIdentity.Matches(retainedRun,new(){Plan=retainedItem,Digest=Store.Digest(retainedItem)})&&!AttemptIdentity.Matches(retainedRun,new(){Plan=retainedItem with{BatchId=Guid.NewGuid()},Digest=Store.Digest(retainedItem)}));
        var pooledDraft = new Draft { Project = Store.Root, Resource = new ResourceSettings { Mode = "practical", JobMemoryLimit = 2 * ResourcePolicy.GiB, PlanningBytes = 2 * ResourcePolicy.GiB, Pool = ResourcePolicy.PoolShared, PoolMemoryLimit = 2 * ResourcePolicy.GiB }, Slots = [new() { Enabled = true }, new() { Enabled = true }, new() { Enabled = true }] };
        var pooledSample = new MemorySnapshot(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
        check("ACC-L02 three slots in one shared pool are billed once by the precheck",
            DecideAdmission(pooledDraft, pooledSample, []).CapSumRequested == 2 * ResourcePolicy.GiB);
        var checkedPool=Guid.NewGuid();var checkedPolicy=ResourcePolicy.SnapshotFor(pooledDraft.Resource,pooledSample.Total,pooledSample.CommitLimit);
        var monitorBudgets=pooledDraft.Slots.Select(slot=>ResourcePolicy.BudgetOf(retainedItem with{Id=slot.Id,PoolId=checkedPool,PlanningBytes=pooledDraft.Resource.PlanningBytes},pooledDraft.Resource.PoolMemoryLimit)).ToArray();
        var monitorDecision=ResourcePolicy.Evaluate(pooledSample,checkedPolicy,[],monitorBudgets);
        check("ACC-L02 shared precheck equals monitor admission",DecideAdmission(pooledDraft,pooledSample,[]).CapSumRequested==monitorDecision.CapSumRequested&&DecideAdmission(pooledDraft,pooledSample,[]).Kind==monitorDecision.Kind);
        pooledDraft.Resource.Pool = ResourcePolicy.PoolIndependent;
        check("ACC-L02 independent slots are billed per item",
            DecideAdmission(pooledDraft, pooledSample, []).CapSumRequested == 6 * ResourcePolicy.GiB);
        File.WriteAllText(tasksPath, "{\"SchemaVersion\":1,\"Revision\":3,\"Tasks\":\"bad\"}");
        File.WriteAllText(tasksPath + ".bak", JsonSerializer.Serialize(book, Store.Json));
        await LoadTasksAsync();
        RenderTasks();
        check("ACC-B06 corrupt task book fails closed and keeps the file", taskBook is null && QueueToggleButton.IsEnabled == false && File.ReadAllText(tasksPath).Contains("\"bad\"") && TaskEmptyText.Text.Contains("无法读取"));
        File.Delete(tasksPath);
        var backup = File.ReadAllBytes(tasksPath + ".bak");
        await LoadTasksAsync();
        check("CR-N02 missing task primary keeps queue disabled and backup untouched", taskBook is null && !QueueToggleButton.IsEnabled
            && !File.Exists(tasksPath) && backup.SequenceEqual(File.ReadAllBytes(tasksPath + ".bak")) && TaskEmptyText.Text.Contains("备份"));
        File.Delete(tasksPath + ".bak");
        Store.Save(tasksPath, new TaskBook());
        await LoadTasksAsync();
        ErrorText.Text = "";
        }
        finally
        {
            queueEnabled = false; taskTimer.Stop();
            queueCancellation?.Cancel();
            await queuePumpTask;
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", originalSample);
            ReviewKillMonitor();
        }
    }
    private async Task RunTaskCommandChecksAsync(Action<string, bool> check)
    {
        await LoadTasksAsync();
        var root = Path.Combine(Store.Root, "queue shim & 中文");
        Directory.CreateDirectory(root);
        var shim = Path.Combine(root, "omp.cmd");
        var script = Path.Combine(root, "queue-agent.js");
        var output = Path.Combine(root, "runs.jsonl");
        File.WriteAllText(shim, """
@ECHO off
GOTO start
:find_dp0
SET dp0=%~dp0
EXIT /b
:start
SETLOCAL
CALL :find_dp0

IF EXIST "%dp0%\node.exe" (
  SET "_prog=%dp0%\node.exe"
) ELSE (
  SET "_prog=node"
  SET PATHEXT=%PATHEXT:;.JS;=;%
)

endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\queue-agent.js" %*
""");
        File.WriteAllText(script, "const fs = require('node:fs');\n"
            + "if (process.argv[2] === '--version') { console.log('omp/18.18.8'); } else if(process.argv[2] === '--help') { console.log('Usage: omp [messages...]\\n --print  Non-interactive mode\\n --resume <id>'); } else {\n"
            + $"fs.appendFileSync({JsonSerializer.Serialize(output)}, JSON.stringify({{ cwd: process.cwd(), args: process.argv.slice(2) }}) + '\\n');\n"
            + "if(process.argv.some(arg=>arg.includes('异常任务夹具')))process.exit(7);\n}\n");
        var arguments = new[] { "", "中文 value", "literal; &|%", "tail\\" };
        var priorMinimize = config.MinimizeAfterLaunch;
        var priorSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        config.MinimizeAfterLaunch = false;
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        try
        {
            var otherScript = Path.Combine(root, "other-agent.js");
            File.WriteAllText(otherScript, "console.log('other package')");
            var otherShim = Path.Combine(root, "other.cmd");
            File.WriteAllText(otherShim, File.ReadAllText(shim).Replace("queue-agent.js", "other-agent.js", StringComparison.Ordinal));
            var identityDraft = new Draft { Project = root, DirectoryMode = "shared", Slots = [
                new() { Enabled = true, AgentId = "codex", Profiles = new() { ["codex"] = new() { Executable = shim } } },
                new() { Enabled = true, AgentId = "codex", Profiles = new() { ["codex"] = new() { Executable = otherShim } } }] };
            var captured = CaptureQuickQueue(identityDraft, "").Items;
            check("CR-N04 quick queue keeps distinct package identities on one Node runtime", captured.Count == 2
                && captured[0].Command.Executable == captured[1].Command.Executable && captured[0].ProgramFingerprint != captured[1].ProgramFingerprint
                && captured.All(item => item.ProgramFingerprint == AgentProbe.Fingerprint(item.Command)));
            Guid taskId = default;
            check("ACC-B08 shim task is saved", await UpdateTasksAsync(book => taskId = TaskBookOps.Create(book, "队列 shim", root, "omp",
                new AgentProfile { Executable = shim, Arguments = arguments }, "", [], "shared", null, 0).Id, "测试任务保存失败"));
            var runs = new List<TaskRun>();
            for (var index = 0; index < 2; index++)
            {
                await EnqueueTaskAsync(taskId);
                await DispatchNextAsync();
                var stored = taskStore.Load();
                var run = TaskBookOps.LastRun(stored, taskId);
                check($"ACC-B08 shim dispatch {index + 1} consumes the queue and creates a new attempt",
                    stored.Queue.All(entry => entry.TaskId != taskId) && run is { Terminal: false } && runs.All(prior => prior.AttemptId != run.AttemptId));
                runs.Add(run!);
                for (var i = 0; i < 100 && ReadStoreSettled<Receipt>(Store.ReceiptPath(run!.AttemptId))?.Phase != "Finished"; i++) await Task.Delay(100);
                check($"ACC-B08 shim dispatch {index + 1} finishes the real pane successfully", ReadStoreSettled<Receipt>(Store.ReceiptPath(run!.AttemptId)) is { Phase: "Finished", ExitCode: 0 });
                await ReconcileTaskRunsAsync();
                check($"ACC-B08 shim dispatch {index + 1} reaches review", TaskBookOps.LastRun(taskStore.Load(), taskId)?.State == "Review");
            }
            var executions = File.ReadAllLines(output).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            // The memory guard contract injects --extension <packaged omp-memory-guard.mjs> ahead of raw arguments for Omp plans.
            check("ACC-B08 shim children receive raw arguments once with their working directory intact", executions.Length == 2
                && executions.All(execution => execution.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray() is { Length: > 2 } received
                    && received[0] == "--extension" && Path.GetFileName(received[1]) == OmpMemoryGuard.ExtensionFile && File.Exists(received[1])
                    && received.Skip(2).SequenceEqual(arguments)
                    && PhysicalDirectory.Resolve(execution.GetProperty("cwd").GetString()!).Key == PhysicalDirectory.Resolve(root).Key));
            check("ACC-B08 repeat dispatch keeps the prepared workspace but renews batch and attempt identities",
                runs[0].WorkspaceKey == runs[1].WorkspaceKey && runs[0].BatchId != runs[1].BatchId && runs[0].AttemptId != runs[1].AttemptId);
            RenderTasks();
            check("ACC-B10 a completed first run exposes explicit session registration",
                ReviewFind<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "登记会话 队列 shim"));
            void RecordSessionDiagnostic(string action, FormWindow? dialog = null, Button? applyButton = null)
            {
                TaskBook? persisted = null; string? readError = null;
                try { persisted = taskStore.Load(); } catch (Exception e) when (e is IOException or JsonException or InvalidDataException) { readError = e.Message; }
                var report = this.arguments is ["--ui-check", var folder] ? folder : Store.Root;
                Store.Save(Path.Combine(report, "task-session-diagnostic.json"), new
                {
                    Action = action, At = DateTimeOffset.UtcNow, Title = dialog?.Title, Visible = dialog?.IsVisible,
                    ApplyError = dialog?.ApplyError, ApplyEnabled = applyButton?.IsEnabled, Error = ErrorText.Text,
                    QueueStatus = QueueStatusText.Text, queueEnabled, queuePumping, ExpectedParent = runs[^1].AttemptId,
                    ReadError = readError, Queue = persisted?.Queue.Where(entry => entry.TaskId == taskId).ToArray(),
                    Attempts = persisted?.Attempts.Where(run => run.TaskId == taskId).ToArray()
                });
            }
            async Task AnswerDialogAsync(string action, string value, bool apply)
            {
                var priorDialogs = OwnedWindows.OfType<FormWindow>().ToHashSet();
                ReviewFind<Button>(TaskList).Single(button => AutomationProperties.GetName(button) == action).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FormWindow? dialog = null;
                for (var i = 0; i < 100 && dialog is null; i++)
                {
                    dialog = OwnedWindows.OfType<FormWindow>().FirstOrDefault(window => window.IsVisible && !priorDialogs.Contains(window));
                    if (dialog is null) await Task.Delay(50);
                }
                if (dialog is null) RecordSessionDiagnostic(action);
                check("ACC-B10 the requested session dialog opens", dialog is not null);
                var applyButton = ReviewFind<Button>(dialog!).Single(button => button.IsDefault);
                var closed = false;
                dialog!.Closed += (_, _) => closed = true;
                try
                {
                    dialog.SetValue("session",value);
                    ReviewFind<Button>(dialog).Single(button => apply ? button.IsDefault : button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!apply && dialog.IsVisible) dialog.Close();
                    // ApplyAsync persists before closing; a dispatcher tick is not a completion boundary.
                    for (var i = 0; i < 100 && !closed && dialog.ApplyError.Length == 0; i++) await Task.Delay(50);
                    if (!closed || dialog.ApplyError.Length > 0) RecordSessionDiagnostic(action, dialog, applyButton);
                    check("ACC-B10 session dialog completes before the next action", closed && dialog.ApplyError.Length == 0);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                finally { if (dialog.IsVisible && applyButton.IsEnabled) dialog.Close(); }
            }
            var beforeCancel = File.ReadAllText(taskStore.FilePath);
            await AnswerDialogAsync("登记会话 队列 shim", "cancelled-session", false);
            check("ACC-B10 cancelling initial registration preserves the task book", File.ReadAllText(taskStore.FilePath) == beforeCancel);
            const string session = "session-中文 with spaces";
            await AnswerDialogAsync("登记会话 队列 shim", session, true);
            for (var i = 0; i < 100 && TaskBookOps.LastRun(taskStore.Load(), taskId)?.SessionRef != session; i++) await Task.Delay(50);
            await LoadTasksAsync();
            check("ACC-B10 registering a completed first run persists its selected session", TaskBookOps.LastRun(taskStore.Load(), taskId)?.SessionRef == session);
            check("ACC-B10 registration makes explicit resume available", ReviewFind<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "续接会话 队列 shim"));
            var beforeResumeCancel = File.ReadAllText(taskStore.FilePath);
            await AnswerDialogAsync("续接会话 队列 shim", session, false);
            check("ACC-B10 cancelling resume preserves session registration and queue", File.ReadAllText(taskStore.FilePath) == beforeResumeCancel);
            await AnswerDialogAsync("续接会话 队列 shim", session, true);
            TaskRun? resumed = null;
            for (var i = 0; i < 100; i++)
            {
                resumed = TaskBookOps.LastRun(taskStore.Load(), taskId);
                if (resumed?.ResumeOf == runs[1].AttemptId) break;
                await Task.Delay(50);
            }
            if (resumed?.ResumeOf != runs[1].AttemptId || resumed.SessionRef != session || resumed.AttemptId == runs[1].AttemptId)
                RecordSessionDiagnostic("resume linkage");
            check("ACC-B10 resume creates a linked new attempt", resumed?.ResumeOf == runs[1].AttemptId && resumed.SessionRef == session && resumed.AttemptId != runs[1].AttemptId);
            for (var i = 0; i < 100 && ReadStoreSettled<Receipt>(Store.ReceiptPath(resumed!.AttemptId))?.Phase != "Finished"; i++) await Task.Delay(100);
            check("ACC-B10 resumed shim finishes through the real Terminal pane", ReadStoreSettled<Receipt>(Store.ReceiptPath(resumed!.AttemptId)) is { Phase: "Finished", ExitCode: 0 });
            for (var i = 0; i < 100 && queuePumping; i++) await Task.Delay(50);
            queueEnabled = false; taskTimer.Stop();
            await ReconcileTaskRunsAsync();
            executions = File.ReadAllLines(output).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            check("ACC-B10 resumed shim preserves raw argv and one literal session value", executions.Length == 3
                && executions[2].GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray() is { Length: > 2 } resumedArgs
                && resumedArgs[0] == "--extension" && Path.GetFileName(resumedArgs[1]) == OmpMemoryGuard.ExtensionFile
                && resumedArgs.Skip(2).SequenceEqual([.. arguments, "--resume", session]));
            var frozenResume = Commands.Resolve("omp", new AgentProfile { Executable = shim, Arguments = [.. arguments, "--resume", session] });
            var occupiedAttempt = Guid.NewGuid();
            check("ACC-B09 queued resume and later occupied session are saved", await UpdateTasksAsync(book =>
            {
                book.MaxRunning = 2;
                book.LastDispatchTaskIds = null;
                TaskBookOps.Enqueue(book, taskId, frozenResume, resumed.ProgramFingerprint, resumed.AttemptId, session);
                book.Attempts.Add(new TaskRun { TaskId = taskId, AttemptId = occupiedAttempt, BatchId = Guid.NewGuid(), State = "Running", SessionRef = session,
                    WorkspaceKey = resumed.WorkspaceKey, ProgramFingerprint = resumed.ProgramFingerprint });
            }, "会话占用夹具保存失败"));
            queueScopeTaskIds = null;
            var attemptsBeforeHold = taskStore.Load().Attempts.Count;
            var entriesBeforeHold = (await Ipc.SendAsync(new("status", Guid.NewGuid()))).Entries?.Select(entry => entry.Plan.Id).ToHashSet() ?? [];
            await DispatchNextAsync();
            var held = taskStore.Load();
            check("ACC-B09 dispatch holds the selected occupied session before reserving or launching", held.Queue.Single(entry => entry.TaskId == taskId).Detail.Contains("占用")
                && held.Attempts.Count == attemptsBeforeHold && File.ReadAllLines(output).Length == 3
                && ((await Ipc.SendAsync(new("status", Guid.NewGuid()))).Entries?.Select(entry => entry.Plan.Id).ToHashSet() ?? []).SetEquals(entriesBeforeHold));
            check("ACC-B09 fixture releases only its synthetic lease", await UpdateTasksAsync(book =>
            {
                TaskBookOps.FinishRun(book, occupiedAttempt, "Cancelled", "isolated test lease released");
                TaskBookOps.Dequeue(book, taskId);
            }, "会话夹具清理失败"));
            check("ACC-B10 resume intent is retained for version revalidation", await UpdateTasksAsync(book =>
                TaskBookOps.Enqueue(book, taskId, frozenResume, resumed.ProgramFingerprint, resumed.AttemptId, session), "版本变化夹具入队失败"));
            var verifiedScript = File.ReadAllText(script);
            try
            {
                File.WriteAllText(script, verifiedScript.Replace("omp/18.18.8", "omp/99.0.0", StringComparison.Ordinal));
                await DispatchNextAsync();
                var versionHeld = taskStore.Load();
                check("ACC-B10 an unverified CLI upgrade holds resume without reserving or launching", versionHeld.Queue.Single(entry => entry.TaskId == taskId).Detail.Contains("程序已升级或更换")
                    && versionHeld.Attempts.Count == attemptsBeforeHold && File.ReadAllLines(output).Length == 3
                    && ((await Ipc.SendAsync(new("status", Guid.NewGuid()))).Entries?.Select(entry => entry.Plan.Id).ToHashSet() ?? []).SetEquals(entriesBeforeHold));
            }
            finally { File.WriteAllText(script, verifiedScript); }
            await UpdateTasksAsync(book => TaskBookOps.Dequeue(book, taskId), "版本变化夹具清理失败");
            await RunTaskRetryChecksAsync(check, taskId, frozenResume);
            Guid deliveryId=default;
            await UpdateTasksAsync(book=>{var card=TaskBookOps.Create(book,"自动交付",root,"omp",new(){Executable=shim},"异常任务夹具\n中文引号 \" 保真",["验收要求"],"shared",null,0);card.DeliveryMode="automatic";deliveryId=card.Id;},"自动交付夹具失败");
            await EnqueueTaskAsync(deliveryId);
            var deliveryEntry=taskStore.Load().Queue.Single(entry=>entry.TaskId==deliveryId);
            var prompt=TaskDelivery.Prompt(taskStore.Load().Tasks.Single(card=>card.Id==deliveryId));
            check("UX12 queue freezes automatic instructions as one argv value",deliveryEntry.Command.Arguments.TakeLast(3).SequenceEqual(new[]{"--print","--",prompt}));
            await DispatchNextAsync();var deliveryRun=TaskBookOps.LastRun(taskStore.Load(),deliveryId)!;
            for(var i=0;i<100&&ReadStoreSettled<Receipt>(Store.ReceiptPath(deliveryRun.AttemptId))?.Phase!="Finished";i++)await Task.Delay(100);
            check("UX12 automatic task executes through real Terminal and retains exit code",ReadStoreSettled<Receipt>(Store.ReceiptPath(deliveryRun.AttemptId)) is{Phase:"Finished",ExitCode:7});
            await ReconcileTaskRunsAsync();
            check("UX12 abnormal automatic task displays failure with diagnostics",TaskBookOps.LastRun(taskStore.Load(),deliveryId) is{State:"Failed",Detail:var failure}&&failure.Contains("7"));
            var delivered=File.ReadAllLines(output).Select(line=>JsonSerializer.Deserialize<JsonElement>(line)).Last().GetProperty("args").EnumerateArray().Select(value=>value.GetString()).ToArray();
            check("UX12 automatic prompt reaches Agent once with exact Chinese and newline content",delivered.TakeLast(3).SequenceEqual(new[]{"--print","--",prompt})&&delivered.Count(arg=>arg==prompt)==1);
        }
        finally
        {
            queueEnabled = false; taskTimer.Stop();
            config.MinimizeAfterLaunch = priorMinimize;
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", priorSample);
            ReviewKillMonitor();
            ResetWorkbenchForChecks();
        }
    }

    private async Task RunTaskRetryChecksAsync(Action<string, bool> check, Guid taskId, CommandSpec command)
    {
        await LoadTasksAsync();
        var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "任务重试门禁", Store.Root, PhysicalDirectory.Workspace(Store.Root),
            true, command, ResourcePolicy.AgentBudget, SourceSlotId: Guid.NewGuid(), AgentId: "codex");
        var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan]));
        if (!reserved.Ok) throw new IOException(reserved.Message);
        var revoked = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: plan.Id));
        if (!revoked.Ok) throw new IOException(revoked.Message);
        status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
        RenderSessionsNow();
        check("CR-N03 ordinary unstarted quick launch retains its retry action", ReviewFind<Button>(Sessions)
            .Any(button => AutomationProperties.GetName(button) == "仅重试此项 任务重试门禁"));
        taskStore.Update(taskBook!.Revision, book => book.Attempts.Add(new TaskRun { TaskId = taskId, TaskRevision = 1, AttemptId = plan.Id,
            BatchId = plan.BatchId, State = "Failed", WorkspaceKey = plan.Workspace.Key, ProgramFingerprint = AgentProbe.Fingerprint(command) }));
        var bytes = File.ReadAllBytes(taskStore.FilePath);
        var refused = false;
        try { await RetrySessionCoreAsync(plan.Id); } catch (IOException e) { refused = e.Message.Contains("重新入队"); }
        var current = await Ipc.SendAsync(new("status", Guid.NewGuid()));
        check("CR-N03 stale session retry reloads task provenance before reserving or launching", refused
            && !current.Entries!.Any(entry => entry.Plan.RetryOf == plan.Id) && bytes.SequenceEqual(File.ReadAllBytes(taskStore.FilePath)));
        await LoadTasksAsync();
        RenderSessionsNow();
        check("CR-N03 task attempts expose requeue guidance without generic retry", !ReviewFind<Button>(Sessions)
            .Any(button => AutomationProperties.GetName(button) == "仅重试此项 任务重试门禁")
            && ReviewFind<TextBlock>(Sessions).Any(text => text.Text.Contains("请从任务卡重新入队")));
    }

}
#endif
