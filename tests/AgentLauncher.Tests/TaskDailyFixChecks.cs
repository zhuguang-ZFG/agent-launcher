using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class TaskDailyFixChecks
{
    public static void Run(Action<string, Action> test, string temp)
    {
        var command = new CommandSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], "exe");
        var fingerprint = new string('A', 64);
        TaskCard Create(TaskBook book) => TaskBookOps.Create(book, "日常任务", temp, "codex", new AgentProfile { Executable = command.Executable }, "", [], "isolated", null, 0);
        test("UX11 ordinary isolated tasks request a worktree while prepared and shared tasks reuse their directory", () =>
        {
            var book = new TaskBook(); var task = Create(book); var entry = TaskBookOps.Enqueue(book, task.Id, command, fingerprint);
            Assert(TaskBookOps.RequiresIsolatedWorkspace(entry), "single ordinary isolated task still uses source");
            entry.DirectoryMode = "prepared"; Assert(!TaskBookOps.RequiresIsolatedWorkspace(entry), "prepared task would create another workspace");
            entry.DirectoryMode = "shared"; Assert(!TaskBookOps.RequiresIsolatedWorkspace(entry), "shared task would isolate");
        });
        test("UX11 attempts freeze actual worktree path independently of later task edits", () =>
        {
            var book = new TaskBook(); var task = Create(book); var entry = TaskBookOps.Enqueue(book, task.Id, command, fingerprint);
            var priorRevision = task.Revision;
            var actual = Path.Combine(temp, "isolated-task");
            var run = TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "actual-workspace", workspacePath: actual);
            Assert(task.Project == actual && task.DirectoryMode == "prepared" && task.QuickLaunch is null, "ordinary task did not bind prepared directory");
            Assert(task.Revision > priorRevision && run.TaskRevision == priorRevision, "prepared binding did not invalidate stale editor while preserving frozen run revision");
            Reject(() => TaskBookOps.Edit(book, task.Id, draft => draft.Project = temp, priorRevision));
            TaskBookOps.FinishRun(book, run.AttemptId, "Review", "finished");
            var again = TaskBookOps.Enqueue(book, task.Id, command, fingerprint);
            Assert(again.Project == actual && !TaskBookOps.RequiresIsolatedWorkspace(again), "subsequent run lost bound workspace");
            TaskBookOps.Edit(book, task.Id, draft => draft.Project = Path.Combine(temp, "other-project"));
            Assert(run.WorkspacePath == actual && book.Copy().Attempts.Single().WorkspacePath == actual, "actual attempt path was lost");
        });
        test("UX12 attempt capacity fails before consuming queue or appending a run", () =>
        {
            var book = new TaskBook(); var task = Create(book); var entry = TaskBookOps.Enqueue(book, task.Id, command, fingerprint);
            for (var i = 0; i < TaskSchema.MaxAttempts; i++) book.Attempts.Add(new TaskRun { AttemptId = Guid.NewGuid(), TaskId = task.Id, State = "Completed" });
            Reject(() => TaskBookOps.EnsureAttemptCapacity(book));
            Reject(() => TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace"));
            Assert(book.Attempts.Count == TaskSchema.MaxAttempts && book.Queue.Single().Id == entry.Id, "capacity error consumed intent");
        });
        test("UX17 quick task attempts retain original slot provenance after edits and copies", () =>
        {
            var slot = new Slot { Enabled = true, AgentId = "codex", Profiles = new() { ["codex"] = new AgentProfile { Executable = command.Executable } } };
            var draft = new Draft { Project = temp, Slots = [slot] }; var book = new TaskBook();
            var ids = TaskBookOps.EnqueueQuickLaunch(book, draft, [new QuickLaunchItem(slot.Id, temp, new WorkspaceIdentity(temp, "workspace"), command, fingerprint)], "");
            var run = TaskBookOps.StartRun(book, book.Queue.Single(), Guid.NewGuid(), Guid.NewGuid(), "workspace", workspacePath: temp);
            TaskBookOps.Edit(book, ids.Single(), task => task.Project = Path.Combine(temp, "another-project"));
            Assert(run.TaskId != slot.Id && book.Tasks.Single().QuickLaunch is null && book.Copy().Attempts.Single().SourceSlotId == slot.Id, "edited task lost original slot provenance");
        });
        test("UX12 immutable history migration preserves last completed resume parents unknown occupancy and original backup", () =>
        {
            var path = Path.Combine(temp, "daily-history", "tasks.json"); var store = new TaskStore(path); var book = new TaskBook(); var task = Create(book);
            var evidence = new Dictionary<Guid, ControlEntry>();
            TaskRun Add(string state, bool verified = true)
            {
                var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, task.Name, temp, new WorkspaceIdentity(temp, "history-workspace"), false, command, ResourcePolicy.AgentBudget, SourceSlotId: task.Id);
                var run = new TaskRun { TaskId = task.Id, TaskRevision = task.Revision, AttemptId = plan.Id, BatchId = plan.BatchId, At = DateTimeOffset.UtcNow, State = state, WorkspaceKey = plan.Workspace.Key, WorkspacePath = temp, ProgramFingerprint = fingerprint };
                book.Attempts.Add(run);
                if (verified) evidence[run.AttemptId] = new ControlEntry { Plan = plan, Digest = Store.Digest(plan), State = "Settled" };
                return run;
            }
            var migrated = Add("Failed"); var completed = Add("Completed"); var unknown = Add("Failed", false); var reviewing = Add("Review");
            var parent = Add("Failed"); parent.SessionRef = "continue-me";
            var latest = Add("Failed");
            TaskBookOps.Enqueue(book, task.Id, command, fingerprint, parent.AttemptId, parent.SessionRef);
            Store.Save(path, book); var original = File.ReadAllBytes(path);
            var next = store.ArchiveHistory(book.Revision, id => evidence.GetValueOrDefault(id));
            Assert(next.Attempts.Select(run => run.AttemptId).ToHashSet().SetEquals(new[] { completed, unknown, reviewing, parent, latest }.Select(run => run.AttemptId)), "protected evidence was removed");
            Assert(next.Queue.Single().ResumeOf == parent.AttemptId && next.HistoryArchives.Single().Count == 1, "resume reference or archive index changed");
            var archive = store.ReadHistoryArchives(next).Single();
            Assert(archive.Attempts.Single().AttemptId == migrated.AttemptId && archive.Evidence.Single().Digest == evidence[migrated.AttemptId].Digest && original.SequenceEqual(File.ReadAllBytes(path + ".bak")), "durable evidence or prior backup lost");
            Assert(store.Load().Attempts.Count + archive.Attempts.Length == book.Attempts.Count, "history count changed after reload");
            Assert(Directory.GetFiles(store.SnapshotDirectory).Any(file => original.SequenceEqual(File.ReadAllBytes(file))), "pre-save immutable task snapshot missing");
            Reject(() => store.ArchiveHistory(book.Revision, id => evidence.GetValueOrDefault(id)));
            var archivePath = Path.Combine(path + ".history", next.HistoryArchives.Single().Id.ToString("N") + ".json");
            File.AppendAllText(archivePath, " ");
            Reject(() => store.Load());
            Reject(() => store.Update(next.Revision, saved => saved.MaxRunning = 2));
        });
        test("UX12 missing proof prevents history migration without changing active evidence", () =>
        {
            var path = Path.Combine(temp, "daily-history-unknown", "tasks.json"); var store = new TaskStore(path); var book = new TaskBook(); var task = Create(book);
            for (var i = 0; i < 3; i++) book.Attempts.Add(new TaskRun { AttemptId = Guid.NewGuid(), TaskId = task.Id, State = "Failed" });
            Store.Save(path, book); var before = File.ReadAllBytes(path);
            Reject(() => store.ArchiveHistory(book.Revision, _ => null));
            Assert(before.SequenceEqual(File.ReadAllBytes(path)) && !Directory.Exists(path + ".history"), "unverified evidence was migrated");
        });
        test("UX12 immutable snapshots retain successive versions and damaged primary during explicit recovery", () =>
        {
            var path = Path.Combine(temp, "daily-snapshots", "tasks.json"); var store = new TaskStore(path); var book = new TaskBook(); Create(book);
            Store.Save(path, book); var first = File.ReadAllBytes(path);
            Directory.CreateDirectory(store.SnapshotDirectory);
            var interruptedPath = Path.Combine(store.SnapshotDirectory, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first)) + ".json");
            File.WriteAllText(interruptedPath, "partial snapshot");
            var next = store.Update(book.Revision, value => value.Tasks[0].Description = "second version"); var second = File.ReadAllBytes(path);
            Assert(Directory.GetFiles(store.SnapshotDirectory, "*.incomplete-*").Any(file => File.ReadAllText(file) == "partial snapshot") && File.ReadAllBytes(interruptedPath).SequenceEqual(first), "interrupted snapshot blocked save or lost original partial evidence");
            _ = store.Update(next.Revision, value => value.Tasks[0].Description = "third version");
            var snapshots = Directory.GetFiles(store.SnapshotDirectory).Select(File.ReadAllBytes).ToArray();
            Assert(snapshots.Any(bytes => bytes.SequenceEqual(first)) && snapshots.Any(bytes => bytes.SequenceEqual(second)), "successive saves lost a historical version");
            File.WriteAllText(path, "{broken primary evidence"); var damaged = File.ReadAllBytes(path);
            var restored = store.RestoreBackup(store.BackupFingerprint());
            Assert(restored.Tasks[0].Description == "second version" && Directory.GetFiles(store.SnapshotDirectory).Any(file => File.ReadAllBytes(file).SequenceEqual(damaged)), "recovery lost damaged source or selected backup");
            var backup = File.ReadAllBytes(path + ".bak");
            var firstFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first));
            Reject(() => store.RestoreSnapshot(firstFingerprint, restored.Revision - 1));
            var fromSnapshot = store.RestoreSnapshot(firstFingerprint, restored.Revision);
            Assert(fromSnapshot.Tasks[0].Description == "" && backup.SequenceEqual(File.ReadAllBytes(path + ".bak")), "snapshot recovery replaced backup or wrong content");
            File.Delete(path); File.Delete(path + ".bak");
            Reject(() => store.Load());
            var missingRestored = store.RestoreSnapshot(firstFingerprint);
            Assert(missingRestored.Tasks.Count == 1 && store.Load().Tasks.Count == 1, "snapshot-only recovery failed");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException) { return; }
        throw new Exception("unsafe operation was accepted");
    }
}
