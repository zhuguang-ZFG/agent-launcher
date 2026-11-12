#if INTEGRATION_TESTS
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunQuickQueueChecksAsync(Action<string, bool> check)
    {
        var priorConfig = config;
        var priorBook = taskStore.Load();
        var priorSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        var project = Path.Combine(Store.Root, "quick queue snapshot");
        Directory.CreateDirectory(project);
        var marker = Path.Combine(project, "unexpected-agent.txt");
        var node = Commands.FindExe("node.exe") ?? throw new IOException("Node fixture is required.");
        var args = new[] { "-e", $"require('node:fs').writeFileSync({JsonSerializer.Serialize(marker)}, 'started')", "", "中文; & literal" };
        var resource = new ResourceSettings { Mode = "custom", JobMemoryLimit = 8 * ResourcePolicy.GiB, PlanningBytes = 8 * ResourcePolicy.GiB,
            PhysicalFloorOverride = 8 * ResourcePolicy.GiB, Pool = ResourcePolicy.PoolShared, PoolMemoryLimit = 8 * ResourcePolicy.GiB };
        config = new Configuration
        {
            Draft = new Draft { Project = project, DirectoryMode = "shared", Resource = resource, Slots =
            [new Slot { Enabled = true, AgentId = "custom", Note = "原始说明", Profiles = new() { ["custom"] = new() { Executable = node, Arguments = args } } },
             new Slot { Enabled = false, AgentId = "custom" },
             new Slot { Enabled = true, AgentId = "custom", Directory = project, Profiles = new() { ["custom"] = new() { Executable = node, Arguments = args } } }] }
        };
        queueEnabled = false;
        taskTimer.Stop();
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        Store.Save(taskStore.FilePath, new TaskBook());
        Store.Save(configPath, config);
        try
        {
            await LoadTasksAsync();
            Render();
            await CheckFreshCommandsAsync();
            await RefreshAsync();
            var original = JsonSerializer.Serialize(config.Draft, Store.Json);
            var before = File.ReadAllText(taskStore.FilePath);
            var preparations = Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : [];

            async Task AnswerAsync(bool apply, bool duplicateClick = false, bool wholeQueue = false)
            {
                QueueQuickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (duplicateClick) QueueQuickButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FormWindow? dialog = null;
                for (var i = 0; i < 100 && dialog is null; i++)
                {
                    dialog = OwnedWindows.OfType<FormWindow>().FirstOrDefault(window => window.IsVisible && window.Title == "按资源依次启动");
                    if (dialog is null) await Task.Delay(25);
                }
                if (dialog is null) throw new IOException("Quick queue confirmation did not open: " + ErrorText.Text);
                if (wholeQueue) ReviewFind<ComboBox>(dialog).Single().SelectedIndex = 1;
                ReviewFind<Button>(dialog).Single(button => apply ? button.IsDefault : button.IsCancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!apply && dialog.IsVisible) dialog.Close();
                await quickQueueTask;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }

            check("QQ resource pressure offers queueing while immediate launch stays blocked", QueueQuickButton.IsEnabled && !LaunchButton.IsEnabled);
            await AnswerAsync(false);
            check("QQ cancelling import leaves tasks and quick configuration unchanged", File.ReadAllText(taskStore.FilePath) == before && JsonSerializer.Serialize(config.Draft, Store.Json) == original);
            await AnswerAsync(true, duplicateClick: true);
            var queued = taskStore.Load();
            check("QQ double click imports each enabled slot exactly once", queued.Tasks.Count == 2 && queued.Queue.Count == 2 && queued.MaxRunning == 1
                && queued.Queue.Select(entry => queued.Tasks.Single(task => task.Id == entry.TaskId).QuickLaunch!.Position).SequenceEqual([1, 3]));
            check("QQ queued profiles freeze raw arguments and resources without changing quick settings", queued.Tasks.All(task => task.Profile.Arguments.SequenceEqual(args)
                && task.Resource is { Pool: ResourcePolicy.PoolIndependent } budget && budget.PlanningBytes == 8 * ResourcePolicy.GiB && budget.JobMemoryLimit == 8 * ResourcePolicy.GiB)
                && JsonSerializer.Serialize(config.Draft, Store.Json) == original);
            check("QQ denied preflight leaves every task waiting without preparation or Agent creation", queued.Attempts.Count == 0 && !File.Exists(marker)
                && preparations.SequenceEqual(Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : []));
            var accepted = File.ReadAllText(taskStore.FilePath);
            await AnswerAsync(true);
            check("QQ repeated confirmed import rejects an already waiting snapshot without mutation", File.ReadAllText(taskStore.FilePath) == accepted);
            await ToggleQueueAsync();
            await queuePumpTask;
            var first = queued.Queue[0].TaskId;
            await UpdateTasksAsync(book => TaskBookOps.Dequeue(book, first), "测试移出失败");
            var removed = taskStore.Load();
            check("QQ removing waiting work keeps other intent and creates no attempt", !queueEnabled && removed.Queue.Count == 1
                && removed.Queue[0].TaskId == queued.Queue[1].TaskId && removed.Tasks.Count == 2 && removed.Attempts.Count == 0 && !File.Exists(marker));
            await UpdateTasksAsync(book => TaskBookOps.Dequeue(book, queued.Queue[1].TaskId), "测试移出失败");
            ReviewKillMonitor();
            queueEnabled = true;
            taskTimer.Start();
            await PumpQueueAsync();
            check("QQ an empty queue stops polling without recreating an idle monitor", !queueEnabled && !taskTimer.IsEnabled && !Ipc.MonitorRunning());
            await UpdateTasksAsync(book => book.MaxRunning = 3, "批次并发夹具保存失败");
            await AnswerAsync(true);
            var scoped = taskStore.Load();
            check("UX05 default confirmation persists this batch without changing global concurrency", scoped.MaxRunning == 3
                && scoped.LastDispatchTaskIds is { Count: 2 } scope && scoped.Queue.All(entry => scope.Contains(entry.TaskId))
                && queueScopeTaskIds?.SetEquals(scope) == true);
            await ToggleQueueAsync();
            await UpdateTasksAsync(book => { foreach (var taskId in book.Queue.Select(entry => entry.TaskId).ToArray()) TaskBookOps.Dequeue(book, taskId); }, "批次夹具清理失败");
            await AnswerAsync(true, wholeQueue: true);
            var whole = taskStore.Load();
            check("UX05 whole queue requires an explicit choice and resets global concurrency", whole.MaxRunning == 1 && whole.LastDispatchTaskIds is null && queueScopeTaskIds is null && whole.Queue.Count == 2);
            await ToggleQueueAsync();
            await UpdateTasksAsync(book => { foreach (var taskId in book.Queue.Select(entry => entry.TaskId).ToArray()) TaskBookOps.Dequeue(book, taskId); }, "整个队列夹具清理失败");
            await RunQueueClaimRecoveryChecksAsync(check);
        }
        finally
        {
            queueEnabled = false;
            taskTimer.Stop();
            queueCancellation?.Cancel();
            await queuePumpTask;
            config = priorConfig;
            Store.Save(configPath, config);
            Store.Save(taskStore.FilePath, priorBook);
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", priorSample);
            ReviewKillMonitor();
            await LoadTasksAsync();
            Render();
            await CheckFreshCommandsAsync();
            ErrorText.Text = "";
        }
    }

    /// <summary>
    /// Permanent regression for a queue entry that keeps a durable monitor claim after reserve succeeded but the app never
    /// recorded a task run (cancel before StartRun, or tasks.json save failure). The next pump must pause before preparing a
    /// command or workspace, preserve both queue intents and the claim, and point at the explicit dequeue + re-enqueue recovery.
    /// </summary>
    private async Task RunQueueClaimRecoveryChecksAsync(Action<string, bool> check)
    {
        var priorConfig = config;
        var priorBook = taskStore.Load();
        var priorSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        queueEnabled = false;
        taskTimer.Stop();
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        try
        {
            var project = Path.Combine(Store.Root, "claim recovery");
            Directory.CreateDirectory(project);
            var marker = Path.Combine(project, "claim-agent.txt");
            var node = Commands.FindExe("node.exe") ?? throw new IOException("Node fixture is required.");
            var arguments = new[] { "-e", $"require('node:fs').writeFileSync({JsonSerializer.Serialize(marker)}, 'started')" };
            config = new Configuration { Draft = new Draft { Project = project, DirectoryMode = "isolated", Layout = "vertical" } };
            var book = new TaskBook();
            var first = TaskBookOps.Create(book, "领取恢复甲", project, "custom", new AgentProfile { Executable = node, Arguments = arguments }, "", [], "isolated", null, 5);
            var second = TaskBookOps.Create(book, "领取恢复乙", project, "custom", new AgentProfile { Executable = node, Arguments = arguments }, "", [], "isolated", null, 1);
            var command = Commands.Resolve("custom", first.Profile);
            var fingerprint = AgentProbe.Fingerprint(command);
            var firstEntry = TaskBookOps.Enqueue(book, first.Id, command, fingerprint);
            var secondEntry = TaskBookOps.Enqueue(book, second.Id, command, fingerprint);
            Store.Save(taskStore.FilePath, book);
            await LoadTasksAsync();
            await RefreshAsync();
            check("QQ-C07 claim recovery uses a healthy real monitor", status is { Ok: true, Memory: not null });
            var memory = status?.Memory ?? throw new IOException("监控资源读数不可用。");
            var policy = ResourcePolicy.SnapshotFor(firstEntry.Resource, memory.Total, memory.CommitLimit);
            var cwd = PhysicalDirectory.Resolve(Path.GetFullPath(firstEntry.Project)).Path;
            var claimed = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, first.Name, cwd, PhysicalDirectory.Workspace(cwd),
                firstEntry.DirectoryMode == "shared", firstEntry.Command with { Arguments = [.. firstEntry.Command.Arguments] }, policy.JobMemoryLimit,
                PlanningBytes: policy.PlanningBytes, ResourceMode: policy.Mode, SourceSlotId: firstEntry.TaskId, AgentId: firstEntry.AgentId);
            var reserve = await Ipc.ReserveWithCleanupAsync([claimed], default, null, policy, null, ResourcePolicy.PolicyHash(policy),
                ResourcePolicy.PlanDigest([claimed]), null, null, firstEntry.Id);
            check("QQ-C08 reserve before StartRun records the durable queue claim", reserve.Ok);
            var revoke = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: claimed.Id));
            check("QQ-C08 revoke confirms the unstarted attempt while the durable claim survives",
                revoke.Ok && revoke.Entries?.Any(entry => entry.Plan.Id == claimed.Id && entry.State == "Revoked") == true);
            var ledger = LedgerMigration.Accept(Store.Read<Ledger>(Store.FilePath("control.json"))!);
            check("QQ-C08 the interrupted start keeps the queue item's durable claim",
                ledger.TaskClaims.Count(claim => claim.EntryId == firstEntry.Id && claim.AttemptId == claimed.Id) == 1);
            var bookBefore = File.ReadAllText(taskStore.FilePath);
            var preparationsBefore = Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : [];

            queueEnabled = true;
            taskTimer.Start();
            await PumpQueueAsync();

            var paused = taskStore.Load();
            var pausedLedger = LedgerMigration.Accept(Store.Read<Ledger>(Store.FilePath("control.json"))!);
            var preparationsAfter = Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : [];
            check("QQ-C09 a permanently claimed head pauses before preparation and starts no attempt",
                !queueEnabled && !taskTimer.IsEnabled && preparationsBefore.SequenceEqual(preparationsAfter) && paused.Attempts.Count == 0 && !File.Exists(marker));
            check("QQ-C10 the pause preserves both queue intents and the durable claim",
                File.ReadAllText(taskStore.FilePath) == bookBefore && paused.Queue.Count == 2
                && paused.Queue.Any(entry => entry.Id == firstEntry.Id && entry.Detail.Length == 0)
                && paused.Queue.Any(entry => entry.Id == secondEntry.Id && entry.Detail.Length == 0)
                && pausedLedger.TaskClaims.Count == ledger.TaskClaims.Count);
            check("QQ-C11 the pause shows the original attempt and the explicit dequeue and re-enqueue recovery",
                QueueStatusText.Text.Contains(claimed.Id.ToString("N")[..8]) && QueueStatusText.Text.Contains("原因与恢复") && QueueStatusText.Text.Contains("重新入队"));

            queueEnabled = true;
            taskTimer.Start();
            await RevokeQueueReservationAsync(claimed.Id, false, false);
            check("QQ-C12 a revoke without a recorded run pauses with the same recovery instead of a silent enabled queue",
                !queueEnabled && !taskTimer.IsEnabled && QueueStatusText.Text.Contains("原因与恢复") && QueueStatusText.Text.Contains("重新入队"));

            await UpdateTasksAsync(editor => TaskBookOps.Dequeue(editor, first.Id), "恢复移出失败");
            var enqueue = ReviewFind<Button>(TaskList).FirstOrDefault(button => button.Content as string == "加入队列");
            check("QQ-C13 the blocked task exposes an enabled explicit re-enqueue action", enqueue is { IsEnabled: true });
            enqueue!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 100 && taskStore.Load().Queue.Count < 2; i++) await Task.Delay(50);
            var recovered = taskStore.Load();
            var recoveredEntry = recovered.Queue.SingleOrDefault(entry => entry.TaskId == first.Id);
            check("QQ-C14 explicit dequeue and re-enqueue create a fresh entry without touching the old claim",
                recovered.Queue.Count == 2 && recoveredEntry is not null && recoveredEntry.Id != firstEntry.Id && recovered.Attempts.Count == 0
                && LedgerMigration.Accept(Store.Read<Ledger>(Store.FilePath("control.json"))!).TaskClaims.All(claim => claim.EntryId != recoveredEntry!.Id));
            var recoveredIntent = recoveredEntry!;
            var recoveryPolicy = ResourcePolicy.SnapshotFor(recoveredIntent.Resource, memory.Total, memory.CommitLimit);
            var recoveryCwd = PhysicalDirectory.Resolve(Path.GetFullPath(recoveredIntent.Project)).Path;
            var recoveryPlan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, first.Name, recoveryCwd, PhysicalDirectory.Workspace(recoveryCwd),
                recoveredIntent.DirectoryMode == "shared", recoveredIntent.Command with { Arguments = [.. recoveredIntent.Command.Arguments] }, recoveryPolicy.JobMemoryLimit,
                PlanningBytes: recoveryPolicy.PlanningBytes, ResourceMode: recoveryPolicy.Mode, SourceSlotId: recoveredIntent.TaskId, AgentId: recoveredIntent.AgentId);
            var accepted = await Ipc.ReserveWithCleanupAsync([recoveryPlan], default, null, recoveryPolicy, null, ResourcePolicy.PolicyHash(recoveryPolicy),
                ResourcePolicy.PlanDigest([recoveryPlan]), null, null, recoveredIntent.Id);
            check("QQ-C15 the monitor accepts the explicitly re-enqueued item past the old claim", accepted.Ok);
            var released = await Ipc.SendAsync(new("revoke", Guid.NewGuid(), Attempt: recoveryPlan.Id));
            check("QQ-C15 the recovery probe is revoked", released.Ok);
        }
        finally
        {
            queueEnabled = false;
            taskTimer.Stop();
            queueCancellation?.Cancel();
            await queuePumpTask;
            config = priorConfig;
            Store.Save(configPath, config);
            Store.Save(taskStore.FilePath, priorBook);
            Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", priorSample);
            ReviewKillMonitor();
            await LoadTasksAsync();
            Render();
            await CheckFreshCommandsAsync();
            ErrorText.Text = "";
        }
    }
}
#endif
