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

        test("ACC-B09 resume dispatch rejects a session acquired after enqueue without consuming intent", () =>
        {
            var book = new TaskBook { MaxRunning = 2 };
            var task = TaskBookOps.Create(book, "续接门禁", temp, "omp", new AgentProfile(), "", [], "shared", null, 0);
            var command = new CommandSpec("C:\\tools\\omp.exe", [], "exe");
            var fingerprint = new string('D', 64);
            var original = TaskBookOps.StartRun(book, TaskBookOps.Enqueue(book, task.Id, command, fingerprint), Guid.NewGuid(), Guid.NewGuid(), "workspace");
            TaskBookOps.FinishRun(book, original.AttemptId, "Review", "finished");
            original.SessionRef = "session-1";
            var entry = TaskBookOps.Enqueue(book, task.Id, command with { Arguments = ["--resume", "session-1"] }, fingerprint, original.AttemptId, "session-1");
            book.Attempts.Add(new TaskRun { TaskId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), BatchId = Guid.NewGuid(), State = "Running", SessionRef = "session-1", WorkspaceKey = "workspace", ProgramFingerprint = fingerprint });
            var before = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace", entry.ResumeOf, entry.SessionRef), "dispatch acquired an already occupied session");
            Assert(JsonSerializer.Serialize(book, Store.Json) == before, "rejected dispatch changed the queue or attempt history");
        });

        test("ACC-B09 resume dispatch rechecks the selected reference workspace program and parent task", () =>
        {
            foreach (var changed in new[] { "session", "workspace", "program", "parent-task", "parent-state" })
            {
                var book = new TaskBook();
                var task = TaskBookOps.Create(book, "续接边界", temp, "omp", new AgentProfile(), "", [], "shared", null, 0);
                var command = new CommandSpec("C:\\tools\\omp.exe", [], "exe");
                var fingerprint = new string('E', 64);
                var original = TaskBookOps.StartRun(book, TaskBookOps.Enqueue(book, task.Id, command, fingerprint), Guid.NewGuid(), Guid.NewGuid(), "workspace");
                TaskBookOps.FinishRun(book, original.AttemptId, "Review", "finished");
                original.SessionRef = "registered-session";
                var entry = TaskBookOps.Enqueue(book, task.Id, command, fingerprint, original.AttemptId, "registered-session");
                if (changed == "session") original.SessionRef = "changed-session";
                if (changed == "program") original.ProgramFingerprint = new string('F', 64);
                if (changed == "parent-task") original.TaskId = Guid.NewGuid();
                if (changed == "parent-state") { original.State = "Running"; book.MaxRunning = 2; }
                var before = JsonSerializer.Serialize(book, Store.Json);
                AssertThrows(() => TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), changed == "workspace" ? "other-workspace" : "workspace", entry.ResumeOf, entry.SessionRef), $"resume ignored changed {changed}");
                Assert(JsonSerializer.Serialize(book, Store.Json) == before, $"changed {changed} rejection altered history");
            }
        });

        test("ACC-B09 session registration records a settled matching attempt and rejects invalid input unchanged", () =>
        {
            var book = new TaskBook();
            var task = TaskBookOps.Create(book, "登记会话", temp, "omp", new AgentProfile(), "", [], "shared", null, 0);
            var command = new CommandSpec("C:\\tools\\omp.exe", [], "exe");
            var fingerprint = new string('G', 64);
            var entry = TaskBookOps.Enqueue(book, task.Id, command, fingerprint);
            Assert(QueuePolicy.CanDispatchResume(book, entry, "workspace", fingerprint).Allowed, "a normal queue entry was treated as a resume");
            var run = TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace");
            var running = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.RegisterSession(book, run.AttemptId, "session-g", "workspace", fingerprint), "a running attempt registered a session");
            Assert(JsonSerializer.Serialize(book, Store.Json) == running, "rejected running registration changed history");
            TaskBookOps.FinishRun(book, run.AttemptId, "Review", "finished");
            var settled = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.RegisterSession(book, run.AttemptId, "   ", "workspace", fingerprint), "a blank session was registered");
            AssertThrows(() => TaskBookOps.RegisterSession(book, run.AttemptId, "bad\0session", "workspace", fingerprint), "a NUL session was registered");
            AssertThrows(() => TaskBookOps.RegisterSession(book, run.AttemptId, new string('s', 201), "workspace", fingerprint), "an overlong session was registered");
            AssertThrows(() => TaskBookOps.RegisterSession(book, run.AttemptId, "session-g", "other-workspace", fingerprint), "a changed workspace registered a session");
            AssertThrows(() => TaskBookOps.RegisterSession(book, run.AttemptId, "session-g", "workspace", new string('H', 64)), "a changed program registered a session");
            AssertThrows(() => TaskBookOps.RegisterSession(book, Guid.NewGuid(), "session-g", "workspace", fingerprint), "an unknown attempt registered a session");
            Assert(JsonSerializer.Serialize(book, Store.Json) == settled, "invalid registration changed history");
            var registered = TaskBookOps.RegisterSession(book, run.AttemptId, "  session-g  ", "workspace", fingerprint);
            Assert(registered.SessionRef == "session-g" && book.Attempts.Single().SessionRef == "session-g", "session registration was not trimmed onto the settled attempt");
            var resume = TaskBookOps.Enqueue(book, task.Id, command with { Arguments = ["--resume", "session-g"] }, fingerprint, run.AttemptId, "session-g");
            Assert(QueuePolicy.CanDispatchResume(book, resume, "workspace", fingerprint).Allowed, "a registered session resume was refused");
            var queued = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.RegisterSession(book, run.AttemptId, "session-other", "workspace", fingerprint), "a queued resume reference was rewritten");
            Assert(JsonSerializer.Serialize(book, Store.Json) == queued, "refused rewrite changed history");
            Assert(TaskBookOps.RegisterSession(book, run.AttemptId, "session-g", "workspace", fingerprint).SessionRef == "session-g", "same-value registration was refused");
            book.Validate();
        });

        test("ACC-B09 session registration and resume dispatch refuse a reference held by a live attempt", () =>
        {
            var book = new TaskBook { MaxRunning = 3 };
            var task = TaskBookOps.Create(book, "会话占用", temp, "omp", new AgentProfile(), "", [], "shared", null, 0);
            var command = new CommandSpec("C:\\tools\\omp.exe", [], "exe");
            var fingerprint = new string('I', 64);
            var original = TaskBookOps.StartRun(book, TaskBookOps.Enqueue(book, task.Id, command, fingerprint), Guid.NewGuid(), Guid.NewGuid(), "workspace");
            TaskBookOps.FinishRun(book, original.AttemptId, "Review", "finished");
            var holder = new TaskRun { TaskId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), BatchId = Guid.NewGuid(), State = "Running", SessionRef = "session-i", WorkspaceKey = "workspace", ProgramFingerprint = fingerprint };
            book.Attempts.Add(holder);
            var held = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.RegisterSession(book, original.AttemptId, "session-i", "workspace", fingerprint), "a session held by a live attempt was registered");
            Assert(JsonSerializer.Serialize(book, Store.Json) == held, "rejected occupied registration changed history");
            book.Attempts.Remove(holder);
            TaskBookOps.RegisterSession(book, original.AttemptId, "session-i", "workspace", fingerprint);
            var entry = TaskBookOps.Enqueue(book, task.Id, command with { Arguments = ["--resume", "session-i"] }, fingerprint, original.AttemptId, "session-i");
            book.Attempts.Add(new TaskRun { TaskId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), BatchId = Guid.NewGuid(), State = "Running", SessionRef = "session-i", WorkspaceKey = "workspace", ProgramFingerprint = fingerprint });
            var leased = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace", entry.ResumeOf, entry.SessionRef), "dispatch acquired a session leased after enqueue");
            Assert(!QueuePolicy.CanDispatchResume(book, entry, "workspace", fingerprint).Allowed, "resume gate allowed a session leased after enqueue");
            Assert(JsonSerializer.Serialize(book, Store.Json) == leased, "rejected leased dispatch changed the queue or attempt history");
        });

        test("ACC-B09 dispatch rejects a stale queue snapshot or mismatched resume identity without changing history", () =>
        {
            var book = new TaskBook { MaxRunning = 2 };
            var task = TaskBookOps.Create(book, "派发快照", temp, "omp", new AgentProfile(), "", [], "shared", null, 0);
            var command = new CommandSpec("C:\\tools\\omp.exe", [], "exe");
            var fingerprint = new string('J', 64);
            var original = TaskBookOps.StartRun(book, TaskBookOps.Enqueue(book, task.Id, command, fingerprint), Guid.NewGuid(), Guid.NewGuid(), "workspace");
            TaskBookOps.FinishRun(book, original.AttemptId, "Review", "finished");
            TaskBookOps.RegisterSession(book, original.AttemptId, "session-j", "workspace", fingerprint);
            var entry = TaskBookOps.Enqueue(book, task.Id, command with { Arguments = ["--resume", "session-j"] }, fingerprint, original.AttemptId, "session-j");
            var planned = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace", Guid.NewGuid(), "session-j"), "a swapped resume attempt was dispatched");
            AssertThrows(() => TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace", original.AttemptId, "session-other"), "a swapped session was dispatched");
            Assert(JsonSerializer.Serialize(book, Store.Json) == planned, "rejected dispatch changed the queue or attempt history");
            TaskBookOps.Dequeue(book, task.Id);
            var removed = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.StartRun(book, entry, Guid.NewGuid(), Guid.NewGuid(), "workspace", entry.ResumeOf, entry.SessionRef), "a removed queue entry was dispatched");
            Assert(JsonSerializer.Serialize(book, Store.Json) == removed, "rejected removed entry changed history");
            var fresh = TaskBookOps.Enqueue(book, task.Id, command with { Arguments = ["--resume", "session-j"] }, fingerprint, original.AttemptId, "session-j");
            book.Tasks[0].Revision = 2;
            var stale = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.StartRun(book, fresh, Guid.NewGuid(), Guid.NewGuid(), "workspace", fresh.ResumeOf, fresh.SessionRef), "a stale task revision was dispatched");
            Assert(JsonSerializer.Serialize(book, Store.Json) == stale, "rejected stale revision changed history");
            book.Tasks[0].Revision = 1;
            var run = TaskBookOps.StartRun(book, fresh, Guid.NewGuid(), Guid.NewGuid(), "workspace", fresh.ResumeOf, fresh.SessionRef);
            Assert(run.ResumeOf == original.AttemptId && run.SessionRef == "session-j" && book.Queue.Count == 0, "authorized resume used a value other than the queue intent");
            book.Validate();
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
            File.Delete(path);
            var gate = store.TryAcquireQueueGate();
            Assert(gate is not null, "queue dispatch gate was unavailable on an idle store");
            Assert(Task.Run(() => store.TryAcquireQueueGate()).GetAwaiter().GetResult() is null, "a second window acquired the queue dispatch gate");
            gate!.Dispose();
            using var reacquired = Task.Run(() => store.TryAcquireQueueGate()).GetAwaiter().GetResult();
            Assert(reacquired is not null, "queue dispatch gate stayed locked after release");
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
