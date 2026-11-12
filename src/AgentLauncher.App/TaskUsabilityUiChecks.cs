#if INTEGRATION_TESTS
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunTaskUsabilityChecksAsync(Action<string, bool> check, string? screenshotFolder = null)
    {
        void Capture(Window window, string name)
        {
            if (screenshotFolder is null) return;
            window.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(window);
            var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(screenshotFolder, name + ".png")); encoder.Save(output);
        }
        var priorBook = taskStore.Load();
        var priorLedger = File.Exists(Store.FilePath("control.json")) ? File.ReadAllBytes(Store.FilePath("control.json")) : null;
        var priorConfirm = TestConfirm;
        var priorSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        ReviewKillMonitor();
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        var ownedBefore = OwnedWindows.Cast<Window>().ToHashSet();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe");
            var command = new CommandSpec(fixture, [], "exe");
            var book = new TaskBook { MaxRunning = 3 };
            var first = TaskBookOps.Create(book, "UX 前项", Store.Root, "custom", new AgentProfile { Executable = fixture }, "修复中文输入", ["验证中文", "保留记录"], "shared", null, 100);
            var second = TaskBookOps.Create(book, "UX 后项", Store.Root, "custom", new AgentProfile { Executable = fixture }, "", [], "shared",
                new ResourceSettings { Mode = "custom", JobMemoryLimit = 8 * ResourcePolicy.GiB, PlanningBytes = 8 * ResourcePolicy.GiB, PhysicalFloorOverride = 8 * ResourcePolicy.GiB }, 0);
            var head = TaskBookOps.Enqueue(book, first.Id, command, new string('A', 64));
            var tail = TaskBookOps.Enqueue(book, second.Id, command, AgentProbe.Fingerprint(command));
            book.LastDispatchTaskIds = [second.Id];
            Store.Save(taskStore.FilePath, book);
            Store.Save(Store.FilePath("control.json"), new Ledger { BootId = Store.BootId() });
            await LoadTasksAsync(); RenderTasks();
            check("UX03 reopen surfaces paused queue with persisted batch scope", !queueEnabled && QueueResumeBanner.Visibility == Visibility.Visible && QueueResumeText.Text.Contains("上次批次") && queueScopeTaskIds?.SetEquals([second.Id]) == true);
            var orderLabels = QueueList.Children.OfType<StackPanel>().Select(row => row.Children.OfType<TextBlock>().First().Text).ToArray();
            check("UX07 queue labels rank only current scope and mark unrelated work outside it", orderLabels[0].Contains("范围外") && orderLabels[1].StartsWith("第 1 项", StringComparison.Ordinal));
            QueueResumeBanner.BringIntoView();
            await Dispatcher.InvokeAsync(() => UpdateLayout(), System.Windows.Threading.DispatcherPriority.ContextIdle);
            Capture(this, "queue");
            QueueList.BringIntoView(new Rect(0, 0, QueueList.ActualWidth, Math.Min(QueueList.ActualHeight, 320)));
            await Dispatcher.InvokeAsync(() => UpdateLayout(), System.Windows.Threading.DispatcherPriority.ContextIdle);
            Capture(this, "queue-order");
            await ContinueQueueAsync();
            check("UX03 explicit continue restores only the saved batch scope", queueEnabled && queueScopeTaskIds?.SetEquals([second.Id]) == true && taskBook?.LastDispatchTaskIds?.SequenceEqual([second.Id]) == true);
            queueEnabled = false; taskTimer.Stop();
            await DispatchNextAsync();
            var scoped = taskStore.Load();
            check("UX05 batch dispatch never touches an older higher priority unrelated entry", scoped.Queue.Single(entry => entry.Id == head.Id).Detail == "" && scoped.Queue.Single(entry => entry.Id == tail.Id).Detail.Contains("资源不足") && scoped.Attempts.Count == 0 && scoped.MaxRunning == 3);
            queueScopeTaskIds = null;
            await SetQueueDeferredAsync(head.Id, true);
            await DispatchNextAsync();
            check("UX06 deferred head preserves identity and allows later independent work to be checked", taskStore.Load().Queue.Single(entry => entry.Id == head.Id).Deferred && QueueList.Children.OfType<StackPanel>().Any(row => ReviewFind<TextBlock>(row).Any(text => text.Text.Contains("暂缓") && text.Text.Contains("UX 前项"))));
            var priorTerminalPath = config.TerminalPath;
            var preparations = Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : [];
            try
            {
                config.TerminalPath = Path.Combine(Store.Root, "missing-terminal.exe");
                queueScopeTaskIds = [first.Id];
                await UpdateTasksAsync(editor => { var item = editor.Queue.Single(entry => entry.Id == head.Id); item.Deferred = false; item.ProgramFingerprint = AgentProbe.Fingerprint(command); }, "Terminal preflight fixture");
                await DispatchNextAsync();
                var retained = taskStore.Load();
                check("UX04 missing Terminal preserves the queue before preparation reservation or task consumption", retained.Queue.Single(entry => entry.Id == head.Id).Detail.Contains("Windows Terminal") && retained.Attempts.Count == 0
                    && preparations.SequenceEqual(Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : [])
                    && TaskRecoveryReader.ReadLedger()?.TaskClaims.Count == 0);
            }
            finally
            {
                config.TerminalPath = priorTerminalPath; queueScopeTaskIds = null;
                await UpdateTasksAsync(editor => { var item = editor.Queue.Single(entry => entry.Id == head.Id); item.Deferred = true; item.ProgramFingerprint = new string('A', 64); }, "Terminal preflight cleanup");
            }
            var closePrompt = "";
            TestConfirm = (text, _, _) => { closePrompt = text; return MessageBoxResult.No; };
            queueEnabled = true;
            var closingArgs = new CancelEventArgs(); ClosingWindow(this, closingArgs);
            check("UX03 closing an idle but enabled queue asks and cancel preserves it", closingArgs.Cancel && closePrompt.Contains("2") && closePrompt.Contains("继续执行") && !closeRequested && queueEnabled);
            queueEnabled = false; taskTimer.Stop();

            var attempt = Guid.NewGuid();
            var plan = new LaunchItem(attempt, Guid.NewGuid(), 1, first.Name, Store.Root, PhysicalDirectory.Workspace(Store.Root), false, command, ResourcePolicy.AgentBudget, SourceSlotId: first.Id);
            var original = new ControlEntry { Plan = plan, Digest = Store.Digest(plan), State = "Revoked", Detail = "原尝试已撤销，未启动" };
            var ledger = new Ledger { BootId = Store.BootId(), Entries = [original], TaskClaims = [new(head.Id, attempt)] };
            Store.Save(Store.FilePath("control.json"), ledger);
            var rejected = false;
            try { await SetQueueDeferredAsync(head.Id, false); } catch (IOException) { rejected = true; }
            check("UX06 a durable claim cannot be hidden by deferring or resuming its queue intent", rejected && taskStore.Load().Queue.Single(entry => entry.Id == head.Id).Deferred);
            ledger.Entries.Clear(); Store.Save(Store.FilePath("control.json"), ledger);
            Store.Save(Store.ReceiptPath(attempt), new Receipt("Finished", 0, 0, 0, "unbound receipt", DateTimeOffset.UtcNow));
            check("UX04 unbound Finished receipt does not authorize replay of missing identity", !TaskRecoveryReader.Inspect(first.Id, head.Id).CanReplaceIntent);
            await ShowTaskRecoveryAsync(first.Id);
            var recovery = OwnedWindows.Cast<Window>().Single(window => !ownedBefore.Contains(window) && window.Title == "原因与恢复");
            check("UX04 recovery view explains missing proof and disables requeue", ReviewFind<TextBox>(recovery).Any(box => box.Text.Contains("尚不能证明") && box.Text.Contains(attempt.ToString())) && ReviewFind<Button>(recovery).Single(button => button.Content as string == "核对后重新入队…").IsEnabled == false);
            Capture(recovery, "recovery");
            recovery.Close();
            var outcomePath = Path.Combine(Store.Root, "attempts", attempt.ToString("N"), "outcome.json");
            Store.Save(outcomePath, new ArchivedAttempt(1, attempt, original.Digest, original));
            check("UX04 verified archived terminal identity authorizes an explicit new intent", TaskRecoveryReader.Inspect(first.Id, head.Id).CanReplaceIntent);
            var ledgerBytes = File.ReadAllBytes(Store.FilePath("control.json"));
            var receiptBytes = File.ReadAllBytes(Store.ReceiptPath(attempt));
            try
            {
                File.WriteAllText(Store.FilePath("control.json"), "{");
                File.WriteAllText(Store.ReceiptPath(attempt), "{");
                var diagnostic = TaskHistoryReader.Diagnostic(new(attempt, first.Id, first.Name, "Revoked", null, ""));
                check("UX08 verified archive remains readable when the live ledger and receipt are damaged", diagnostic.Contains("Revoked") && diagnostic.Contains("LedgerNotice") && diagnostic.Contains("ReceiptNotice") && !TaskRecoveryReader.Inspect(first.Id, head.Id).CanReplaceIntent);
            }
            finally { File.WriteAllBytes(Store.FilePath("control.json"), ledgerBytes); File.WriteAllBytes(Store.ReceiptPath(attempt), receiptBytes); }
            await ReplaceQueueIntentAsync(head.Id);
            var recovered = taskStore.Load();
            var replacement = recovered.Queue.Single(entry => entry.TaskId == first.Id);
            check("UX04 guided replacement renews identity keeps FIFO claim history and stays paused", replacement.Id != head.Id && replacement.Sequence == head.Sequence && recovered.Attempts.Count == 0 && !queueEnabled && !taskTimer.IsEnabled && Store.Read<Ledger>(Store.FilePath("control.json"))!.TaskClaims.Single() == new TaskReservationClaim(head.Id, attempt));
            ShowTaskBrief(first);
            var brief = OwnedWindows.Cast<Window>().Single(window => !ownedBefore.Contains(window) && window.Title.StartsWith("复制任务说明 ·", StringComparison.Ordinal));
            check("UX02 actual handoff window warns before copy and includes description and acceptance", ReviewFind<TextBlock>(brief).Any(text => text.Text.Contains("尚未")) && ReviewFind<TextBox>(brief).Single().Text.Contains("修复中文输入") && ReviewFind<TextBox>(brief).Single().Text.Contains("验证中文"));
            Capture(brief, "handoff");
            brief.Close();
            await UpdateTasksAsync(editor =>
            {
                TaskBookOps.Dequeue(editor, first.Id); TaskBookOps.Dequeue(editor, second.Id);
                for (var i = 0; i < 20; i++) editor.Attempts.Add(new TaskRun { TaskId = first.Id, TaskRevision = first.Revision, AttemptId = Guid.NewGuid(), BatchId = Guid.NewGuid(), State = "Completed", At = DateTimeOffset.UtcNow.AddMinutes(-i), WorkspaceKey = "history-fixture", ProgramFingerprint = new string('A', 64) });
            }, "UX fixture history");
            await SetTaskArchivedAsync(first.Id, true);
            check("UX01 archive hides card in active view while all twenty attempts remain", taskStore.Load().Tasks.Single(task => task.Id == first.Id).Archived && taskStore.Load().Attempts.Count == 20 && !ReviewFind<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "复制任务说明 UX 前项"));
            TaskFilter.SelectedIndex = 1;
            check("UX01 archived view exposes reversible restoration", ReviewFind<Button>(TaskList).Any(button => AutomationProperties.GetName(button) == "恢复归档 UX 前项"));
            var history = await Task.Run(() => TaskHistoryReader.Load(taskStore, first.Id));
            check("UX08 task history returns retained runs and older verified archives for archived tasks", history.Items.Length == 21 && history.Items.All(item => item.TaskId == first.Id) && history.Items.Any(item => item.AttemptId == attempt));
            await ShowTaskHistoryAsync(first.Id);
            var historyWindow = OwnedWindows.OfType<TaskHistoryWindow>().Single();
            Capture(historyWindow, "history");
            var search = ReviewFind<TextBox>(historyWindow).Single(box => AutomationProperties.GetName(box).StartsWith("搜索历史", StringComparison.Ordinal));
            search.Text = history.Items[0].AttemptId.ToString();
            check("UX08 actual history window filters by complete attempt identity", ReviewFind<ListBox>(historyWindow).Single().Items.Count == 1);
            historyWindow.Close();
            await SetTaskArchivedAsync(first.Id, false);
            check("UX01 restored card remains unqueued and keeps history", !taskStore.Load().Tasks.Single(task => task.Id == first.Id).Archived && taskStore.Load().Queue.Count == 0 && taskStore.Load().Attempts.Count == 20);
            check("UX09 per CLI session guidance distinguishes task identity and session identity", TaskUsability.SessionGuide("codex").Contains("任务编号不是会话 ID") && TaskUsability.SessionGuide("omp").Contains("完整路径"));
        }
        finally
        {
            queueEnabled = false; taskTimer.Stop(); queueScopeTaskIds = null;
            TestConfirm = priorConfirm; Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", priorSample);
            ReviewKillMonitor();
            foreach (var window in OwnedWindows.Cast<Window>().Where(window => !ownedBefore.Contains(window)).ToArray()) window.Close();
            Store.Save(taskStore.FilePath, priorBook);
            if (priorLedger is null) { File.Delete(Store.FilePath("control.json")); File.Delete(Store.FilePath("control.json.bak")); }
            else File.WriteAllBytes(Store.FilePath("control.json"), priorLedger);
            TaskFilter.SelectedIndex = 0; TaskSearch.Text = "";
            await LoadTasksAsync(); ErrorText.Text = "";
        }
    }

    private async Task RunTaskUsabilityOnlyAsync(string folder)
    {
        Directory.CreateDirectory(folder); Directory.CreateDirectory(Store.Root);
        var checks = new List<string>();
        void Check(string name, bool value)
        {
            if (!value) { Store.Save(Path.Combine(folder, "usability-checks.json"), new { passed = checks, failed = name }); throw new Exception("UI regression: " + name); }
            checks.Add(name);
        }
        await RunTaskUsabilityChecksAsync(Check, folder);
        await RunQuickQueueChecksAsync(Check);
        Store.Save(Path.Combine(folder, "usability-checks.json"), new { passed = checks, Dpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX });
        Close();
    }
}
#endif
