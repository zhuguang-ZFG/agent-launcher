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

        test("ACC-B11 quick launch freezes resolved raw argv and one task per enabled draft slot in order", () =>
        {
            var book = new TaskBook();
            var first = QuickSlot("omp", "说明一", "--model", "a b");
            var second = QuickSlot("codex", "说明二", "--sandbox", "workspace-write");
            var draft = QuickDraft("shared", first, new Slot { Enabled = false, AgentId = "pi" }, second);
            var items = new[]
            {
                QuickItem(second, temp, "volume:second", "C:\\resolved\\codex.exe", new[] { "--resolved", "x y" }),
                QuickItem(first, temp, "volume:first", "C:\\resolved\\omp.exe", new[] { "--flag", "literal value" })
            };
            var draftBefore = JsonSerializer.Serialize(draft, Store.Json);
            var itemsBefore = JsonSerializer.Serialize(items, Store.Json);
            var ids = TaskBookOps.EnqueueQuickLaunch(book, draft, items, Path.Combine(temp, "worktrees"));

            Assert(ids.Count == 2 && book.Tasks.Select(task => task.Id).SequenceEqual(ids), "quick launch did not return one task id per enabled slot");
            Assert(book.Tasks[0].QuickLaunch!.Position == 1 && book.Tasks[0].QuickLaunch!.SlotId == first.Id && book.Tasks[1].QuickLaunch!.Position == 3 && book.Tasks[1].QuickLaunch!.SlotId == second.Id, "quick launch order or original position changed");
            Assert(book.Tasks[0].Name == "Omp · 1" && book.Tasks[1].Name == "Codex · 3", "quick launch names lost the display name or original position");
            Assert(book.Tasks[0].Profile.Executable == "C:\\raw\\omp.exe" && book.Tasks[0].Profile.Arguments.SequenceEqual(new[] { "--model", "a b" }), "quick launch did not freeze the raw profile arguments");
            Assert(book.Tasks[0].Description == "说明一" && book.Tasks[0].Project == temp && book.Tasks[0].DirectoryMode == "shared", "quick launch lost the note, project, or directory mode");
            Assert(book.Tasks[0].Resource!.Pool == ResourcePolicy.PoolIndependent && book.Tasks[0].Resource!.PoolMemoryLimit is null && book.Tasks[0].Resource!.JobMemoryLimit == draft.Resource.JobMemoryLimit, "quick launch did not freeze an independent resource pool");
            var entry = book.Queue[0];
            Assert(entry.QuickLaunch == book.Tasks[0].QuickLaunch && entry.Command.Executable == "C:\\resolved\\omp.exe" && entry.Command.Arguments.SequenceEqual(new[] { "--flag", "literal value" }), "queue entry lost the resolved command or frozen metadata");
            Assert(entry.QuickLaunch!.Key.Length == 64 && entry.QuickLaunch!.Key.All(Uri.IsHexDigit), "quick launch key is not 64 hex characters");
            Assert(entry.Sequence == 1 && book.Queue[1].Sequence == 2 && book.NextSequence == 2 && book.MaxRunning == 1, "quick launch did not keep FIFO order or reset the limit");
            Assert(book.Attempts.Count == 0 && book.Tasks.All(task => task.DependsOn.Count == 0), "quick launch created an attempt or dependency");
            Assert(JsonSerializer.Serialize(draft, Store.Json) == draftBefore && JsonSerializer.Serialize(items, Store.Json) == itemsBefore, "quick launch mutated its inputs");
            book.Validate();
            var restored = JsonSerializer.Deserialize<TaskBook>(JsonSerializer.Serialize(book, Store.Json), Store.Json)!;
            restored.Validate();
            Assert(restored.Tasks[1].QuickLaunch == book.Tasks[1].QuickLaunch && restored.Queue[1].QuickLaunch == book.Queue[1].QuickLaunch, "quick launch metadata did not round-trip");
        });

        test("ACC-B12 quick launch forces isolation only for duplicate workspace groups and rejects prepared duplicates", () =>
        {
            var book = new TaskBook();
            var repoA = QuickSlot("omp", "甲", "--a");
            var repoB = QuickSlot("codex", "乙", "--b");
            var other = QuickSlot("pi", "丙", "--c");
            var draft = QuickDraft("isolated", repoA, repoB, other);
            var items = new[]
            {
                QuickItem(repoA, temp, "volume:repo", "C:\\resolved\\omp.exe", Array.Empty<string>()),
                QuickItem(repoB, temp, "volume:repo", "C:\\resolved\\codex.exe", Array.Empty<string>()),
                QuickItem(other, temp, "volume:other", "C:\\resolved\\pi.exe", Array.Empty<string>())
            };
            var root = Path.Combine(temp, "worktrees");
            TaskBookOps.EnqueueQuickLaunch(book, draft, items, root);
            Assert(book.Tasks[0].QuickLaunch!.IsolateWorkspace && book.Tasks[1].QuickLaunch!.IsolateWorkspace && !book.Tasks[2].QuickLaunch!.IsolateWorkspace, "per-slot isolation flag did not follow duplicate workspace keys");
            Assert(book.Queue.All(entry => entry.QuickLaunch!.WorktreeRoot == root), "frozen worktree root was not stored");

            var preparedBook = new TaskBook();
            var prepared = QuickDraft("prepared", repoA, repoB);
            var preparedItems = new[]
            {
                QuickItem(repoA, temp, "volume:repo", "C:\\resolved\\omp.exe", Array.Empty<string>()),
                QuickItem(repoB, temp, "volume:repo", "C:\\resolved\\codex.exe", Array.Empty<string>())
            };
            var before = JsonSerializer.Serialize(preparedBook, Store.Json);
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(preparedBook, prepared, preparedItems, root), "prepared mode accepted duplicate workspace keys");
            Assert(JsonSerializer.Serialize(preparedBook, Store.Json) == before, "rejected prepared import changed the book");
        });

        test("ACC-B13 quick launch refuses an identical active import without mutation", () =>
        {
            var book = new TaskBook();
            var slot = QuickSlot("omp", "重复", "--once");
            var draft = QuickDraft("shared", slot);
            var item = QuickItem(slot, temp, "volume:dup", "C:\\resolved\\omp.exe", new[] { "--resolved" });
            var root = Path.Combine(temp, "worktrees");
            var ids = TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { item }, root);
            var queued = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { item }, root), "identical queued import was accepted");
            Assert(JsonSerializer.Serialize(book, Store.Json) == queued, "rejected queued import changed the book");
            TaskBookOps.StartRun(book, book.Queue.Single(), Guid.NewGuid(), Guid.NewGuid(), item.Workspace.Key);
            var running = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { item }, root), "identical import was accepted while a run was live");
            Assert(JsonSerializer.Serialize(book, Store.Json) == running, "rejected running import changed the book");
            TaskBookOps.FinishRun(book, book.Attempts.Single().AttemptId, "Cancelled", "测试结束");
            var again = TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { item }, root);
            Assert(again.Count == 1 && again[0] != ids[0] && book.Tasks.Count == 2, "terminal import was not re-importable as new tasks");
        });

        test("ACC-B14 quick launch stages invalid items and capacity failures without mutating the book", () =>
        {
            var slotA = QuickSlot("omp", "甲", "--a");
            var slotB = QuickSlot("codex", "乙", "--b");
            var slotC = QuickSlot("pi", "丙", "--c");
            var draft = QuickDraft("shared", slotA, slotB, slotC);
            var valid = new[]
            {
                QuickItem(slotA, temp, "volume:a", "C:\\resolved\\a.exe", Array.Empty<string>()),
                QuickItem(slotB, temp, "volume:b", "C:\\resolved\\b.exe", Array.Empty<string>()),
                QuickItem(slotC, temp, "volume:c", "C:\\resolved\\c.exe", Array.Empty<string>())
            };
            var root = Path.Combine(temp, "worktrees");
            var book = new TaskBook();
            var existing = TaskBookOps.Create(book, "旧任务", temp, "omp", new AgentProfile(), "", Array.Empty<string>(), "shared", null, 0);
            TaskBookOps.Enqueue(book, existing.Id, new CommandSpec("C:\\old\\omp.exe", new[] { "--old" }, "exe"), new string('Z', 64));
            book.MaxRunning = 3;
            var before = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { valid[0], valid[1] with { Command = new CommandSpec("", Array.Empty<string>(), "exe") }, valid[2] }, root), "an invalid later command was accepted");
            Assert(JsonSerializer.Serialize(book, Store.Json) == before, "invalid later item changed the book");
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { valid[0], valid[1] with { SlotId = Guid.NewGuid() }, valid[2] }, root), "a mismatched slot input was accepted");
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { valid[0], valid[2] }, root), "a partial input set was accepted");
            Assert(JsonSerializer.Serialize(book, Store.Json) == before, "rejected input sets changed the book");

            var full = new TaskBook { MaxRunning = 2 };
            while (full.Tasks.Count < TaskSchema.MaxTasks) full.Tasks.Add(new TaskCard { Id = Guid.NewGuid(), Revision = 1, Name = "占位", Project = temp, AgentId = "omp", Profile = new AgentProfile(), UpdatedAt = DateTimeOffset.UtcNow });
            var fullBefore = JsonSerializer.Serialize(full, Store.Json);
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(full, draft, valid, root), "task capacity was exceeded");
            Assert(JsonSerializer.Serialize(full, Store.Json) == fullBefore, "task capacity rejection changed the book");

            var crowded = new TaskBook();
            while (crowded.Queue.Count < TaskSchema.MaxQueueEntries) crowded.Queue.Add(new QueueEntry
            {
                Id = Guid.NewGuid(), TaskId = Guid.NewGuid(), TaskRevision = 1, Priority = 0, Sequence = crowded.Queue.Count + 1,
                EnqueuedAt = DateTimeOffset.UtcNow, Project = temp, AgentId = "omp", Command = new CommandSpec("C:\\old\\omp.exe", Array.Empty<string>(), "exe"),
                ProgramFingerprint = new string('0', 64), DirectoryMode = "shared", Resource = new ResourceSettings(), DependsOn = []
            });
            var crowdedBefore = JsonSerializer.Serialize(crowded, Store.Json);
            AssertThrows(() => TaskBookOps.EnqueueQuickLaunch(crowded, draft, valid, root), "queue capacity was exceeded");
            Assert(JsonSerializer.Serialize(crowded, Store.Json) == crowdedBefore, "queue capacity rejection changed the book");
        });

        test("ACC-B15 quick launch metadata freezes at enqueue and detaches only on launch-relevant edits", () =>
        {
            var book = new TaskBook();
            var slot = QuickSlot("omp", "元数据", "--meta");
            var draft = QuickDraft("isolated", slot);
            var item = QuickItem(slot, temp, "volume:meta", "C:\\resolved\\omp.exe", new[] { "--resolved" });
            var id = TaskBookOps.EnqueueQuickLaunch(book, draft, new[] { item }, Path.Combine(temp, "worktrees")).Single();
            var task = book.Tasks.Single();
            Assert(task.Copy().QuickLaunch == task.QuickLaunch && book.Queue.Single().QuickLaunch == task.QuickLaunch, "task or entry copy detached the frozen metadata");
            TaskBookOps.Edit(book, id, edit => edit.Description = "显示修改");
            Assert(book.Tasks.Single().QuickLaunch == task.QuickLaunch && book.Queue.Count == 1 && book.Tasks.Single().Revision == 1, "display-only edit dropped metadata or the queue");
            var tampered = book.Queue.Single().Copy();
            tampered.QuickLaunch = tampered.QuickLaunch! with { WorkspaceKey = "volume:tampered" };
            var before = JsonSerializer.Serialize(book, Store.Json);
            AssertThrows(() => TaskBookOps.StartRun(book, tampered, Guid.NewGuid(), Guid.NewGuid(), item.Workspace.Key), "tampered metadata dispatched");
            Assert(JsonSerializer.Serialize(book, Store.Json) == before, "rejected tampered dispatch changed history");
            TaskBookOps.Edit(book, id, edit => { edit.Resource!.Pool = ResourcePolicy.PoolShared; edit.Resource.PoolMemoryLimit = 4 * ResourcePolicy.GiB; });
            Assert(book.Tasks.Single().QuickLaunch is null && book.Queue.Count == 0 && book.Tasks.Single().Revision == 2, "pool edit retained frozen metadata or the queued entry");
            var invalid = book.Tasks.Single().Copy();
            invalid.QuickLaunch = new QuickLaunchSource("short", Guid.NewGuid(), 1, "volume:x", temp, false);
            AssertThrows(invalid.Validate, "task validation accepted an invalid quick launch key");
            var fresh = new TaskBook();
            var freshId = TaskBookOps.EnqueueQuickLaunch(fresh, draft, new[] { item }, Path.Combine(temp, "worktrees")).Single();
            fresh.Tasks.Single().QuickLaunch = new QuickLaunchSource(new string('0', 63), freshId, 1, "volume:x", temp, false);
            AssertThrows(fresh.Validate, "book validation accepted invalid task metadata");
        });

        test("ACC-B16 task store migrates schema1 in memory only and refuses unsupported versions without writing", () =>
        {
            var path = Path.Combine(temp, "tasks-migrate", "tasks.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var legacy = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Revision = 3,
                MaxRunning = 1,
                NextSequence = 0,
                Tasks = new[]
                {
                    new
                    {
                        Id = Guid.NewGuid(), Revision = 1, Name = "旧任务", Project = temp, AgentId = "omp",
                        Profile = new { Executable = "C:\\old\\omp.exe", Arguments = new[] { "--old", "a b" } },
                        Description = "旧说明", Acceptance = Array.Empty<object>(), DirectoryMode = "shared",
                        DependsOn = Array.Empty<Guid>(), Priority = 0, UpdatedAt = DateTimeOffset.UtcNow
                    }
                },
                Queue = Array.Empty<object>(),
                Attempts = Array.Empty<object>()
            }, Store.Json);
            File.WriteAllText(path, legacy);
            var store = new TaskStore(path);
            var loaded = store.Load();
            Assert(loaded.SchemaVersion == TaskSchema.Current && loaded.Tasks.Count == 1 && loaded.Tasks[0].Profile.Arguments.SequenceEqual(new[] { "--old", "a b" }) && loaded.Tasks[0].QuickLaunch is null, "schema1 book did not load with its old content");
            Assert(File.ReadAllText(path) == legacy, "loading a schema1 book rewrote the file");
            var updated = store.Update(3, book => book.MaxRunning = 2);
            Assert(updated.SchemaVersion == TaskSchema.Current && store.Load().MaxRunning == 2 && File.ReadAllText(path).Contains("\"SchemaVersion\":2", StringComparison.Ordinal), "update path did not persist the migrated schema");
            var future = legacy.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":3", StringComparison.Ordinal);
            File.WriteAllText(path, future);
            AssertThrows(() => store.Load(), "a future schema was accepted");
            AssertThrows(() => store.Update(4, book => book.MaxRunning = 1), "a future schema was overwritten");
            Assert(File.ReadAllText(path) == future, "failed future load rewrote the file");
            AssertThrows(() => new TaskBook { SchemaVersion = 1 }.Validate(), "the old schema version still validated as writable");
        });
    }

    private static Draft QuickDraft(string mode, params Slot[] slots) => new()
    {
        Project = "",
        DirectoryMode = mode,
        Slots = [.. slots],
        Resource = new ResourceSettings { Pool = ResourcePolicy.PoolShared, PoolMemoryLimit = 4 * ResourcePolicy.GiB }
    };

    private static Slot QuickSlot(string agent, string note, params string[] rawArguments) => new()
    {
        Enabled = true,
        AgentId = agent,
        Note = note,
        Profiles = new() { [agent] = new AgentProfile { Executable = "C:\\raw\\" + agent + ".exe", Arguments = rawArguments } }
    };

    private static QuickLaunchItem QuickItem(Slot slot, string project, string workspaceKey, string executable, string[] arguments) =>
        new(slot.Id, project, new WorkspaceIdentity(Path.Combine(project, "ws", workspaceKey.Replace(':', '_')), workspaceKey), new CommandSpec(executable, arguments, "exe"), new string('Q', 64));

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
