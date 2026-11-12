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
    private async Task RunTaskChecksAsync(Action<string, bool> check)
    {
        IEnumerable<T> FindLogical<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is T target) yield return target;
                foreach (var nested in FindLogical<T>(child)) yield return nested;
            }
        }
        var tasksPath = Store.FilePath("tasks.json");
        File.Delete(tasksPath);
        var fixture = Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe");
        check("ACC-B05 task fixture program exists", File.Exists(fixture));
        var book = new TaskBook();
        var waiting = TaskBookOps.Create(book, "任务甲", Store.Root, "custom", new AgentProfile { Executable = fixture }, "", ["通过"], "shared", null, 4);
        TaskBookOps.Enqueue(book, waiting.Id, new CommandSpec(fixture, ["--fixture"], "exe"), new string('A', 64));
        var reviewed = TaskBookOps.Create(book, "任务乙", Store.Root, "custom", new AgentProfile { Executable = fixture }, "", [], "shared", null, 1);
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
        for (var i = 0; i < 50 && !taskStore.Load().Tasks.Single(task => task.Id == waiting.Id).Acceptance[0].Checked; i++) await Task.Delay(100);
        check("ACC-B05 accepting a checkbox persists without dropping the queue", taskStore.Load().Tasks.Single(task => task.Id == waiting.Id).Acceptance[0].Checked && taskStore.Load().Queue.Count == 1);
        check("ACC-B05 resume action appears only for a settled session", FindLogical<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "续接会话 任务乙")
            && !FindLogical<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "续接会话 任务甲"));
        QueueToggleClick(this, new RoutedEventArgs());
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        check("ACC-B05 queue starts paused and only runs when asked", queueEnabled && QueueToggleButton.Content as string == "暂停队列");
        var attemptsBefore = taskStore.Load().Attempts.Count;
        var detail = "";
        for (var i = 0; i < 300; i++)
        {
            detail = taskStore.Load().Queue.Single().Detail;
            if (detail.Contains("程序或参数已变化")) break;
            if (!queuePumping) await PumpQueueAsync();
            await Task.Delay(100);
        }
        check("ACC-B06 dispatch holds a changed program instead of launching", detail.Contains("程序或参数已变化") && taskStore.Load().Attempts.Count == attemptsBefore && QueueStatusText.Text.Contains("程序或参数已变化"));
        QueueToggleClick(this, new RoutedEventArgs());
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
        for (var i = 0; i < 50 && taskStore.Load().Queue.Count > 0; i++) await Task.Delay(100);
        check("ACC-B07 removing a queued item keeps attempts and the task record",
            taskStore.Load().Queue.Count == 0 && taskStore.Load().Attempts.Count == attemptsBeforeDequeue && taskStore.Load().Tasks.Any(task => task.Id == waiting.Id));
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
        File.Delete(tasksPath + ".bak");
        await LoadTasksAsync();
        ErrorText.Text = "";
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
            + "if (process.argv[2] === '--version') { console.log('omp/18.18.8'); } else {\n"
            + $"fs.appendFileSync({JsonSerializer.Serialize(output)}, JSON.stringify({{ cwd: process.cwd(), args: process.argv.slice(2) }}) + '\\n');\n}}\n");
        var arguments = new[] { "", "中文 value", "literal; &|%", "tail\\" };
        var priorMinimize = config.MinimizeAfterLaunch;
        var priorSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        config.MinimizeAfterLaunch = false;
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        try
        {
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
                for (var i = 0; i < 100 && Store.Read<Receipt>(Store.ReceiptPath(run!.AttemptId))?.Phase != "Finished"; i++) await Task.Delay(100);
                check($"ACC-B08 shim dispatch {index + 1} finishes the real pane successfully", Store.Read<Receipt>(Store.ReceiptPath(run!.AttemptId)) is { Phase: "Finished", ExitCode: 0 });
                await ReconcileTaskRunsAsync();
                check($"ACC-B08 shim dispatch {index + 1} reaches review", TaskBookOps.LastRun(taskStore.Load(), taskId)?.State == "Review");
            }
            var executions = File.ReadAllLines(output).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            check("ACC-B08 shim children receive raw arguments once with their working directory intact", executions.Length == 2
                && executions.All(execution => execution.GetProperty("args").EnumerateArray().Select(value => value.GetString()).SequenceEqual(arguments)
                    && PhysicalDirectory.Resolve(execution.GetProperty("cwd").GetString()!).Key == PhysicalDirectory.Resolve(root).Key));
            check("ACC-B08 repeat dispatch keeps the prepared workspace but renews batch and attempt identities",
                runs[0].WorkspaceKey == runs[1].WorkspaceKey && runs[0].BatchId != runs[1].BatchId && runs[0].AttemptId != runs[1].AttemptId);
            RenderTasks();
            check("ACC-B10 a completed first run exposes explicit session registration",
                ReviewFind<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "登记会话 队列 shim"));
            async Task AnswerDialogAsync(string action, string value, bool apply)
            {
                ReviewFind<Button>(TaskList).Single(button => AutomationProperties.GetName(button) == action).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FormWindow? dialog = null;
                for (var i = 0; i < 100 && dialog is null; i++)
                {
                    dialog = OwnedWindows.OfType<FormWindow>().FirstOrDefault(window => window.IsVisible);
                    if (dialog is null) await Task.Delay(50);
                }
                check("ACC-B10 the requested session dialog opens", dialog is not null);
                ReviewFind<TextBox>(dialog!).First().Text = value;
                ReviewFind<Button>(dialog!).Single(button => button.Content as string == (apply ? "应用" : "取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!apply && dialog!.IsVisible) dialog.Close();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
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
            check("ACC-B10 resume creates a linked new attempt", resumed?.ResumeOf == runs[1].AttemptId && resumed.SessionRef == session && resumed.AttemptId != runs[1].AttemptId);
            for (var i = 0; i < 100 && Store.Read<Receipt>(Store.ReceiptPath(resumed!.AttemptId))?.Phase != "Finished"; i++) await Task.Delay(100);
            check("ACC-B10 resumed shim finishes through the real Terminal pane", Store.Read<Receipt>(Store.ReceiptPath(resumed!.AttemptId)) is { Phase: "Finished", ExitCode: 0 });
            for (var i = 0; i < 100 && queuePumping; i++) await Task.Delay(50);
            queueEnabled = false; taskTimer.Stop();
            await ReconcileTaskRunsAsync();
            executions = File.ReadAllLines(output).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            check("ACC-B10 resumed shim preserves raw argv and one literal session value", executions.Length == 3
                && executions[2].GetProperty("args").EnumerateArray().Select(value => value.GetString()).SequenceEqual([.. arguments, "--resume", session]));
            var frozenResume = Commands.Resolve("omp", new AgentProfile { Executable = shim, Arguments = [.. arguments, "--resume", session] });
            var occupiedAttempt = Guid.NewGuid();
            check("ACC-B09 queued resume and later occupied session are saved", await UpdateTasksAsync(book =>
            {
                book.MaxRunning = 2;
                TaskBookOps.Enqueue(book, taskId, frozenResume, resumed.ProgramFingerprint, resumed.AttemptId, session);
                book.Attempts.Add(new TaskRun { TaskId = taskId, AttemptId = occupiedAttempt, BatchId = Guid.NewGuid(), State = "Running", SessionRef = session,
                    WorkspaceKey = resumed.WorkspaceKey, ProgramFingerprint = resumed.ProgramFingerprint });
            }, "会话占用夹具保存失败"));
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
                check("ACC-B10 an unverified CLI upgrade holds resume without reserving or launching", versionHeld.Queue.Single(entry => entry.TaskId == taskId).Detail.Contains("没有已验证的续接参数")
                    && versionHeld.Attempts.Count == attemptsBeforeHold && File.ReadAllLines(output).Length == 3
                    && ((await Ipc.SendAsync(new("status", Guid.NewGuid()))).Entries?.Select(entry => entry.Plan.Id).ToHashSet() ?? []).SetEquals(entriesBeforeHold));
            }
            finally { File.WriteAllText(script, verifiedScript); }
            await UpdateTasksAsync(book => TaskBookOps.Dequeue(book, taskId), "版本变化夹具清理失败");
        }
        finally
        {
            queueEnabled = false; taskTimer.Stop();
            config.MinimizeAfterLaunch = priorMinimize;
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", priorSample);
            ReviewKillMonitor();
        }
    }

}
#endif
