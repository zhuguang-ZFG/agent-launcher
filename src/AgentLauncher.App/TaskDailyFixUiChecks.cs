#if INTEGRATION_TESTS
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunTaskDailyFixChecksAsync(Action<string, bool> check)
    {
        var prior = taskStore.Load(); var priorDraft = config.Draft.Copy();
        try
        {
            Store.Save(taskStore.FilePath, new TaskBook()); await LoadTasksAsync();
            var fixture = Path.Combine(AppContext.BaseDirectory, "AgentLauncher.App.exe");
            config.Draft.Project = Store.Root;
            config.Draft.Slots = [new Slot { Enabled = true, AgentId = "codex", Profiles = new() { ["codex"] = new AgentProfile { Executable = fixture, Arguments = ["--draft-fixture", "中文 value"] } } }];
            var exercise = Dispatcher.InvokeAsync(async () =>
            {
                var form = OwnedWindows.OfType<FormWindow>().Single(window => window.Title == "新建任务");
                var apply = ReviewFind<Button>(form).Single(button => button.IsDefault);
                async Task ApplyAndWaitAsync()
                {
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    for (var i = 0; i < 100 && form.IsVisible && (!apply.IsEnabled || form.ApplyError.Length == 0); i++) await Task.Delay(20);
                }
                try
                {
                    check("UX11 new task inherits configured CLI executable and structured arguments", form.ReadValue("exe") == fixture && form.ReadValue("agent") == "codex");
                    form.SetValue("name", "保留草稿"); form.SetValue("project", Path.Combine(Store.Root, "does-not-exist"));
                    form.SetValue("description", "保留中文说明\n第二行"); form.SetValue("acceptance", "保留验收一\n保留验收二");
                    await ApplyAndWaitAsync();
                    check("UX11 invalid project keeps task form description and acceptance draft", form.IsVisible && form.ApplyError.Contains("绝对目录") && form.ReadValue("description").Contains("第二行") && form.ReadValue("acceptance").Contains("验收二") && taskStore.Load().Tasks.Count == 0);
                    form.SetValue("project", Store.Root); form.SetValue("name", ""); await ApplyAndWaitAsync();
                    check("UX11 invalid task name remains editable in original form", form.IsVisible && form.ApplyError.Contains("名称") && form.ReadValue("description").Contains("第二行") && taskStore.Load().Tasks.Count == 0);
                    form.SetValue("name", "保留草稿");
                    taskStore.Update(taskStore.Load().Revision, book => book.MaxRunning = 2);
                    await ApplyAndWaitAsync();
                    check("UX11 save conflict preserves form and allows retry after reload", form.IsVisible && form.ApplyError.Contains("其他窗口") && form.ReadValue("name") == "保留草稿" && taskStore.Load().Tasks.Count == 0);
                    await ApplyAndWaitAsync();
                    check("UX11 successful save alone closes the form and retains inherited CLI arguments", !form.IsVisible && taskStore.Load().Tasks.Single().Profile.Executable == fixture
                        && taskStore.Load().Tasks.Single().Profile.Arguments.SequenceEqual(["--draft-fixture", "中文 value"]) && taskStore.Load().Tasks.Single().Acceptance.Count == 2);
                }
                finally { if (form.IsVisible) form.Close(); }
            });
            await NewTaskAsync(); await exercise.Task.Unwrap();

            var snapshotBytes = File.ReadAllBytes(taskStore.FilePath);
            var snapshotHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshotBytes));
            taskStore.Update(taskStore.Load().Revision, book => book.Tasks[0].Name = "稍后修改的任务"); await LoadTasksAsync();
            var beforeRestore = File.ReadAllBytes(taskStore.FilePath); var backupBeforeRestore = File.ReadAllBytes(taskStore.FilePath + ".bak");
            var priorConfirm = TestConfirm;
            try
            {
                var preview = ""; TestConfirm = (text, _, _) => { preview = text; return MessageBoxResult.No; };
                var choose = Dispatcher.InvokeAsync(async () =>
                {
                    FormWindow? form = null;
                    for (var i = 0; i < 200 && form is null; i++) { form = OwnedWindows.OfType<FormWindow>().FirstOrDefault(window => window.Title == "选择任务快照"); if (form is null) await Task.Delay(20); }
                    if (form is null) throw new IOException("snapshot chooser did not open");
                    try
                    {
                        form.SetValue("snapshot", snapshotHash); var apply = ReviewFind<Button>(form).Single(button => button.IsDefault);
                        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (var i = 0; i < 100 && (!apply.IsEnabled || form.ApplyError.Length == 0); i++) await Task.Delay(20);
                        check("UX12 snapshot restore previews exact content and cancellation keeps selected draft", preview.Contains("保留草稿") && preview.Contains("将替换当前 1 张任务") && form.IsVisible
                            && form.ReadValue("snapshot") == snapshotHash && beforeRestore.SequenceEqual(File.ReadAllBytes(taskStore.FilePath)));
                        TestConfirm = (_, _, _) => MessageBoxResult.Yes;
                        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        for (var i = 0; i < 100 && form.IsVisible; i++) await Task.Delay(20);
                        check("UX12 confirmed snapshot restore keeps queue paused and original backup", !form.IsVisible && !queueEnabled && taskStore.Load().Tasks.Single().Name == "保留草稿"
                            && backupBeforeRestore.SequenceEqual(File.ReadAllBytes(taskStore.FilePath + ".bak")));
                    }
                    finally { if (form.IsVisible) form.Close(); }
                });
                await ChooseTaskSnapshotAsync(this); await choose.Task.Unwrap();
            }
            finally { TestConfirm = priorConfirm; }

            var full = taskStore.Load(); var task = full.Tasks.Single();
            TaskBookOps.Enqueue(full, task.Id, new CommandSpec(fixture, task.Profile.Arguments, "exe"), new string('A', 64));
            for (var i = 0; i < TaskSchema.MaxAttempts; i++) full.Attempts.Add(new TaskRun { TaskId = task.Id, AttemptId = Guid.NewGuid(), BatchId = Guid.NewGuid(), State = "Completed", WorkspaceKey = "capacity-fixture" });
            Store.Save(taskStore.FilePath, full); await LoadTasksAsync();
            var controlPath = Store.FilePath("control.json"); var control = File.Exists(controlPath) ? File.ReadAllBytes(controlPath) : null;
            var preparations = Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : [];
            await DispatchNextAsync();
            check("UX12 capacity preflight preserves queue and never prepares or reserves", QueueStatusText.Text.Contains("1000") && taskStore.Load().Queue.Count == 1 && taskStore.Load().Attempts.Count == TaskSchema.MaxAttempts
                && (control is null ? !File.Exists(controlPath) : control.SequenceEqual(File.ReadAllBytes(controlPath)))
                && preparations.SequenceEqual(Directory.Exists(Store.FilePath("preparations")) ? Directory.GetFiles(Store.FilePath("preparations")).Order().ToArray() : []));
            await CheckOrdinaryTaskWorktreesAsync(check, fixture);
        }
        finally
        {
            config.Draft = priorDraft; Store.Save(taskStore.FilePath, prior); await LoadTasksAsync(); ErrorText.Text = "";
        }
    }

    private static async Task CheckOrdinaryTaskWorktreesAsync(Action<string, bool> check, string fixture)
    {
        var priorSample = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE");
        ReviewKillMonitor(); Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", "1");
        var ledgerPath = Store.FilePath("control.json"); var priorLedger = File.Exists(ledgerPath) ? File.ReadAllBytes(ledgerPath) : null;
        var priorBackup = File.Exists(ledgerPath + ".bak") ? File.ReadAllBytes(ledgerPath + ".bak") : null;
        try
        {
            Store.Save(ledgerPath, new Ledger { BootId = Store.BootId() });
            var source = Path.Combine(Store.Root, "ordinary-isolation-repo"); Directory.CreateDirectory(source);
            var git = Commands.FindExe("git.exe") ?? throw new IOException("Git fixture missing");
            async Task Git(params string[] args)
            {
                var info = new System.Diagnostics.ProcessStartInfo(git) { WorkingDirectory = source, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in args) info.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("Git fixture failed to start");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
                _ = await stdout; var error = await stderr; if (process.ExitCode != 0) throw new IOException(error);
            }
            await Git("init", "-q"); await File.WriteAllTextAsync(Path.Combine(source, "tracked.txt"), "baseline"); await Git("add", "tracked.txt");
            await Git("-c", "user.email=fixture@example.invalid", "-c", "user.name=Task Test", "commit", "-qm", "baseline");
            var monitor = await Ipc.EnsureMonitorAsync(); if (!monitor.Ok) throw new IOException(monitor.Message);
            var configuration = Store.Read<Configuration>(Store.FilePath("configuration.json")) ?? new Configuration();
            var book = new TaskBook(); var plans = new List<LaunchItem>();
            for (var i = 0; i < 2; i++)
            {
                var task = TaskBookOps.Create(book, "普通隔离任务 " + i, source, "codex", new AgentProfile { Executable = fixture }, "", [], "isolated", null, 0);
                var command = Commands.Resolve(task.AgentId, task.Profile);
                var entry = TaskBookOps.Enqueue(book, task.Id, command, AgentProbe.Fingerprint(command));
                var draft = new Draft { Revision = configuration.Draft.Revision, Project = entry.Project, DirectoryMode = entry.DirectoryMode, Slots = [new Slot { Id = task.Id, Enabled = true, AgentId = task.AgentId, Profiles = new() { [task.AgentId] = task.Profile.Copy() } }] };
                var prepared = await new WorkspacePreparer().PrepareAsync(draft, _ => { }, _ => Task.FromResult(false), CancellationToken.None,
                    plans.Select(plan => plan.Workspace).ToArray(), TaskBookOps.RequiresIsolatedWorkspace(entry));
                var plan = prepared.Single(); plans.Add(plan);
                var run = TaskBookOps.StartRun(book, entry, plan.Id, plan.BatchId, plan.Workspace.Key, workspacePath: plan.Cwd);
                TaskBookOps.FinishRun(book, run.AttemptId, "Review", "preparation-only fixture");
                check($"UX11 ordinary isolated task {i + 1} prepares and binds actual worktree", plan.Cwd != PhysicalDirectory.Resolve(source).Path && task.Project == plan.Cwd && task.DirectoryMode == "prepared" && run.WorkspacePath == plan.Cwd);
            }
            await File.WriteAllTextAsync(Path.Combine(plans[0].Cwd, "tracked.txt"), "task one");
            check("UX11 two ordinary tasks own distinct worktrees and source stays unchanged", plans[0].Workspace.Key != plans[1].Workspace.Key
                && await File.ReadAllTextAsync(Path.Combine(source, "tracked.txt")) == "baseline" && await File.ReadAllTextAsync(Path.Combine(plans[1].Cwd, "tracked.txt")) == "baseline");
        }
        finally
        {
            ReviewKillMonitor(); Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE", priorSample);
            if (priorLedger is null) File.Delete(ledgerPath); else File.WriteAllBytes(ledgerPath, priorLedger);
            if (priorBackup is null) File.Delete(ledgerPath + ".bak"); else File.WriteAllBytes(ledgerPath + ".bak", priorBackup);
        }
    }
}
#endif
