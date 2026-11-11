using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class TaskChecks
{
    public static void Run(Action<string, Action> test, string temp)
    {
        test("ACC-B01 queue orders by priority then FIFO and gates dependencies and concurrency", () =>
        {
            var dependency = Guid.NewGuid();
            var first = new QueueCandidate(Guid.NewGuid(), Guid.NewGuid(), 1, 0, 1, DateTimeOffset.UtcNow, []);
            var urgent = new QueueCandidate(Guid.NewGuid(), Guid.NewGuid(), 1, 5, 2, DateTimeOffset.UtcNow, []);
            var later = new QueueCandidate(Guid.NewGuid(), Guid.NewGuid(), 1, 0, 3, DateTimeOffset.UtcNow, []);
            var blocked = new QueueCandidate(Guid.NewGuid(), Guid.NewGuid(), 1, 9, 4, DateTimeOffset.UtcNow, [dependency]);
            var decision = QueuePolicy.Decide([first, urgent, later, blocked], 0, 1, _ => false);
            Assert(decision.Next == urgent, "highest priority was not dispatched first");
            Assert(decision.Waiting.Single(wait => wait.EntryId == blocked.EntryId).Reason == "等待依赖任务", "unfinished dependency was not reported");
            var full = QueuePolicy.Decide([first, urgent], 1, 1, _ => true);
            Assert(full.Next is null && full.Waiting.Count == 2 && full.Waiting.All(wait => wait.Reason == "并发上限"), "concurrency limit did not hold the queue");
            var fifo = QueuePolicy.Decide([later, first], 0, 2, _ => true);
            Assert(fifo.Next == first, "equal priority did not follow enqueue sequence");
            var unblocked = QueuePolicy.Decide([blocked], 0, 1, id => id == dependency);
            Assert(unblocked.Next == blocked, "completed dependency still blocked dispatch");
        });

        test("ACC-B02 task edits bump revisions and only launch-relevant edits drop the queue entry", () =>
        {
            var book = new TaskBook();
            var task = TaskBookOps.Create(book, "修复登录", temp, "omp", new AgentProfile(), "说明", ["测试通过", "  "], "isolated", null, 3);
            Assert(task.Acceptance.Count == 1 && task.Revision == 1, "task creation accepted blank acceptance or wrong revision");
            var entry = TaskBookOps.Enqueue(book, task.Id, new CommandSpec("C:\\tools\\omp.exe", ["--continue"], "exe"), new string('A', 64));
            Assert(book.Queue.Count == 1 && entry.Sequence == 1 && book.NextSequence == 1 && entry.TaskRevision == 1, "enqueue lost sequence or revision");
            AssertThrows(() => TaskBookOps.Enqueue(book, task.Id, entry.Command, entry.ProgramFingerprint), "duplicate enqueue was accepted");
            TaskBookOps.Edit(book, task.Id, draft => draft.Acceptance[0].Checked = true);
            Assert(book.Tasks[0].Acceptance[0].Checked && book.Queue.Count == 1 && book.Tasks[0].Revision == 1, "acceptance toggle dropped the queue entry or bumped the launch revision");
            TaskBookOps.Edit(book, task.Id, draft => draft.Priority = 7);
            Assert(book.Queue.Count == 0, "priority edit kept a stale frozen queue entry");
            Assert(book.Tasks[0].Revision == 2, "launch-relevant edit did not bump the revision");
            AssertThrows(() => TaskBookOps.Enqueue(book, task.Id, new CommandSpec("C:\\tools\\omp.exe", [], "exe"), "short"), "short fingerprint was accepted");
            book.Validate();
        });

        test("ACC-B03 runs respect the concurrency limit and resume needs a settled matching session", () =>
        {
            var book = new TaskBook();
            var task = TaskBookOps.Create(book, "续接", temp, "omp", new AgentProfile(), "", [], "isolated", null, 0);
            var entry = TaskBookOps.Enqueue(book, task.Id, new CommandSpec("C:\\tools\\omp.exe", [], "exe"), new string('B', 64));
            var workspace = "volume:abc";
            var run = TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), workspace);
            Assert(book.Queue.Count == 0 && run.State == "Running", "dispatch did not consume the queue entry");
            var second = TaskBookOps.Enqueue(book, task.Id, entry.Command, entry.ProgramFingerprint);
            AssertThrows(() => TaskBookOps.StartRun(book, second, Guid.NewGuid(), Guid.NewGuid(), workspace), "second run ignored the concurrency limit");
            Assert(!QueuePolicy.CanResume(run, workspace, run.ProgramFingerprint, book.Attempts).Allowed, "resume allowed a running attempt");
            AssertThrows(() => TaskBookOps.Complete(book, task.Id), "completion skipped review for a running attempt");
            TaskBookOps.FinishRun(book, run.AttemptId, "Review", "进程已结束");
            Assert(QueuePolicy.CanResume(run, workspace, run.ProgramFingerprint, book.Attempts).Allowed == false, "resume allowed an attempt without a session reference");
            run.SessionRef = "session-1";
            Assert(QueuePolicy.CanResume(run, workspace, run.ProgramFingerprint, book.Attempts).Allowed, "settled session was not resumable");
            Assert(!QueuePolicy.CanResume(run, workspace, new string('C', 64), book.Attempts).Allowed, "resume ignored a changed program fingerprint");
            Assert(!QueuePolicy.CanResume(run, "volume:other", run.ProgramFingerprint, book.Attempts).Allowed, "resume ignored a changed workspace");
            var live = new TaskRun { TaskId = task.Id, AttemptId = Guid.NewGuid(), State = "Running", SessionRef = "session-1", WorkspaceKey = workspace, ProgramFingerprint = run.ProgramFingerprint };
            book.Attempts.Add(live);
            Assert(!QueuePolicy.CanResume(run, workspace, run.ProgramFingerprint, book.Attempts).Allowed, "resume allowed a session still held by a live attempt");
            book.Attempts.Remove(live);
            var completed = TaskBookOps.Complete(book, task.Id);
            Assert(completed.State == "Completed", "review attempt was not completed");
        });

        test("ACC-B04 task store gates revisioned writes and never overwrites a corrupt book", () =>
        {
            var path = Path.Combine(temp, "tasks", "tasks.json");
            var store = new TaskStore(path);
            var empty = store.Load();
            Assert(empty.Revision == 0 && empty.Tasks.Count == 0, "missing task book did not load as empty");
            var created = store.Update(0, book => TaskBookOps.Create(book, "任务一", temp, "omp", new AgentProfile(), "", [], "isolated", null, 0));
            Assert(created.Revision == 1 && store.Load().Tasks.Count == 1, "first write was not persisted with a new revision");
            AssertThrows(() => store.Update(0, _ => { }), "stale revision overwrote the task book");
            var corrupt = "{\"SchemaVersion\":1,\"Revision\":9,\"Tasks\":\"bad\"}";
            File.WriteAllText(path, corrupt);
            AssertThrows(() => store.Load(), "corrupt task book was accepted");
            AssertThrows(() => store.Update(9, book => book.MaxRunning = 2), "corrupt task book was overwritten");
            Assert(File.ReadAllText(path) == corrupt, "failed load rewrote the corrupt task book");
        });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        catch (JsonException) { return; }
        throw new Exception(message);
    }
}
