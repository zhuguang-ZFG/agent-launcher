using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class TaskUsabilityChecks
{
    public static void Run(Action<string, Action> test, string temp)
    {
        TaskCard Create(TaskBook book, string name = "任务") => TaskBookOps.Create(book, name, temp, "custom", new AgentProfile { Executable = "C:\\agent.exe" }, "请修复中文问题", ["测试通过", "保留历史"], "shared", null, 0);
        var command = new CommandSpec("C:\\agent.exe", ["--raw", ""], "exe");
        var fingerprint = new string('A', 64);
        test("UX04 null task queue and attempt elements report damaged structure", () =>
        {
            foreach (var book in new[] { new TaskBook { Tasks = [null!] }, new TaskBook { Queue = [null!], NextSequence = 1 }, new TaskBook { Attempts = [null!] } }) Reject(book.Validate);
        });
        test("UX01 archive frees active capacity while retaining task and attempt identities", () =>
        {
            var book = new TaskBook();
            var first = Create(book, "归档对象");
            var run = TaskBookOps.StartRun(book, TaskBookOps.Enqueue(book, first.Id, command, fingerprint), Guid.NewGuid(), Guid.NewGuid(), "workspace");
            TaskBookOps.FinishRun(book, run.AttemptId, "Review", "finished");
            TaskBookOps.Complete(book, first.Id);
            while (book.Tasks.Count < TaskSchema.MaxTasks) Create(book);
            Reject(() => Create(book));
            TaskBookOps.SetArchived(book, first.Id, true);
            Create(book, "新任务");
            Assert(book.Tasks.Count == 201 && book.Tasks.Count(task => !task.Archived) == 200 && book.Attempts.Single().AttemptId == run.AttemptId, "archive deleted provenance or did not release a slot");
            Reject(() => TaskBookOps.SetArchived(book, first.Id, false));
            Reject(() => TaskBookOps.Enqueue(book, first.Id, command, fingerprint));
            book.Validate();
            TaskBookOps.SetArchived(book, book.Tasks.Last().Id, true);
            TaskBookOps.SetArchived(book, first.Id, false);
            Assert(!first.Archived && book.Attempts.Single().State == "Completed", "restore replayed or lost the prior result");
        });
        test("UX01 queued and unknown running tasks cannot be archived", () =>
        {
            var book = new TaskBook(); var task = Create(book);
            var entry = TaskBookOps.Enqueue(book, task.Id, command, fingerprint);
            Reject(() => TaskBookOps.SetArchived(book, task.Id, true));
            TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace");
            var before = JsonSerializer.Serialize(book, Store.Json);
            Reject(() => TaskBookOps.SetArchived(book, task.Id, true));
            Assert(JsonSerializer.Serialize(book, Store.Json) == before, "unknown running archive changed the book");
        });
        test("UX02 handoff brief includes requirements without arguments or claimed execution", () =>
        {
            var book = new TaskBook(); var task = Create(book, "修复登录");
            task.Profile.Arguments = ["secret-test-argument"];
            var brief = TaskUsability.Brief(task);
            Assert(brief.Contains("修复登录") && brief.Contains("请修复中文问题") && brief.Contains("测试通过") && brief.Contains(temp), "handoff omitted user requirements");
            Assert(!brief.Contains("secret-test-argument"), "handoff copied hidden launch arguments");
            Assert(TaskUsability.SessionGuide("codex").Contains("codex resume") && TaskUsability.SessionGuide("omp").Contains("完整会话文件路径"), "agent-specific session instructions missing");
        });
        test("UX03 close warning states pending count pause and explicit resume", () =>
        {
            var text = TaskUsability.CloseQueueNotice(7, false);
            Assert(text.Contains("7") && text.Contains("暂停") && text.Contains("继续执行") && text.Contains("继续运行"), "idle waiting queue close had no actionable warning");
            Assert(TaskUsability.CloseQueueNotice(0, false) == "", "idle window asks an unnecessary question");
        });
        test("UX06 deferral dispatches the next eligible task and rejects a stale dispatch", () =>
        {
            var book = new TaskBook(); var first = Create(book, "前项"); var second = Create(book, "后项");
            var high = TaskBookOps.Enqueue(book, first.Id, command, fingerprint); high.Priority = 100;
            var next = TaskBookOps.Enqueue(book, second.Id, command, fingerprint);
            var frozen = high.Copy();
            TaskBookOps.SetDeferred(book, high.Id, true);
            var candidates = TaskUsability.OrderedQueue(book).Select(entry => new QueueCandidate(entry.Id, entry.TaskId, entry.TaskRevision, entry.Priority, entry.Sequence, entry.EnqueuedAt, [.. entry.DependsOn], entry.Deferred)).ToArray();
            Assert(QueuePolicy.Decide(candidates, 0, 1, _ => true).Next?.EntryId == next.Id, "deferred head still blocked an independent task");
            Reject(() => TaskBookOps.StartRun(book, frozen, Guid.NewGuid(), Guid.NewGuid(), "workspace"));
            Assert(book.Attempts.Count == 0 && book.Queue.Count == 2, "stale start consumed deferred intent");
            TaskBookOps.SetDeferred(book, high.Id, false);
            Assert(TaskUsability.OrderedQueue(book)[0].Id == high.Id && high.Sequence == frozen.Sequence, "resume changed identity or ordering");
        });
        test("UX05 batch import preserves global concurrency and old queue intent", () =>
        {
            var book = new TaskBook { MaxRunning = 4 };
            var existing = Create(book, "已有任务"); var old = TaskBookOps.Enqueue(book, existing.Id, command, fingerprint);
            var slot = new Slot { Enabled = true, AgentId = "custom" };
            var draft = new Draft { Project = temp, DirectoryMode = "shared", Slots = [slot] };
            var ids = TaskBookOps.EnqueueQuickLaunch(book, draft, [new(slot.Id, temp, new(temp, "workspace"), command, fingerprint)], "", resetConcurrency: false);
            book.LastDispatchTaskIds = ids.ToList();
            Assert(book.MaxRunning == 4 && book.Queue.Any(entry => entry.Id == old.Id) && !ids.Contains(existing.Id), "batch import modified the unrelated queue");
            var copy = book.Copy(); copy.Validate();
            Assert(copy.LastDispatchTaskIds!.SequenceEqual(ids), "batch scope was not retained across persistence copies");
        });
        test("UX04 explicit task backup recovery keeps backup and unreadable original unchanged", () =>
        {
            var path = Path.Combine(temp, "ux-recovery", "tasks.json"); var store = new TaskStore(path);
            var saved = store.Update(0, book => Create(book, "可恢复任务"));
            store.Update(saved.Revision, _ => { });
            var backup = File.ReadAllBytes(path + ".bak");
            File.WriteAllText(path, "{broken-primary"); var broken = File.ReadAllBytes(path);
            var hash = store.BackupFingerprint();
            Reject(() => store.RestoreBackup(new string('F', 64)));
            Assert(File.ReadAllBytes(path).SequenceEqual(broken) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "stale confirmation altered evidence");
            var recovered = store.RestoreBackup(hash);
            Assert(recovered.Tasks.Single().Name == "可恢复任务" && recovered.Revision > saved.Revision, "backup not restored with a fresh revision");
            Assert(File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "valid backup was overwritten");
            Assert(Directory.GetFiles(Path.GetDirectoryName(path)!, "tasks.json.unreadable-*.json").Any(file => File.ReadAllBytes(file).SequenceEqual(broken)), "unreadable primary not preserved");
            Reject(() => store.RestoreBackup(hash));
            Reject(() => store.Update(saved.Revision, _ => { }));
        });
        test("UX01 schema2 loads without changing archived or deferred meaning or rewriting", () =>
        {
            var path = Path.Combine(temp, "ux-schema2.json"); var book = new TaskBook(); Create(book);
            book.SchemaVersion = 2; Store.Save(path, book); var before = File.ReadAllBytes(path);
            var migrated = new TaskStore(path).Load();
            Assert(migrated.SchemaVersion == 3 && !migrated.Tasks.Single().Archived && migrated.LastDispatchTaskIds is null && before.SequenceEqual(File.ReadAllBytes(path)), "schema2 migration changed disk or invented new intent");
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException) { return; }
        throw new Exception("unsafe operation was accepted");
    }
}
