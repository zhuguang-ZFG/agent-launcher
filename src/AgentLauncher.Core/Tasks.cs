using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

public static class TaskSchema
{
    // Version 3 adds archived cards and deferred queue intents; older dispatchers must reject these semantics.
    public const int Current = 3;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 20000;
    public const int MaxAcceptanceItems = 32;
    public const int MaxAcceptanceTextLength = 500;
    public const int MaxTasks = 200;
    public const int MaxStoredTasks = 1200;
    public const int MaxQueueEntries = 200;
    public const int MaxAttempts = 1000;
    public const int MaxDependencies = 16;
    public const int MaxDetailLength = 500;
}

public sealed class AcceptanceItem
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string Text { get; set; } = "";
    [JsonRequired] public bool Checked { get; set; }
    public AcceptanceItem Copy() => new() { Id = Id, Text = Text, Checked = Checked };
}

/// <summary>User-authored task card. Editing bumps Revision; an enqueued entry keeps the Revision it was authorized with.</summary>
public sealed class TaskCard
{
    [JsonRequired] public Guid Id { get; set; }
    [JsonRequired] public long Revision { get; set; }
    [JsonRequired] public string Name { get; set; } = "";
    [JsonRequired] public string Project { get; set; } = "";
    [JsonRequired] public string AgentId { get; set; } = "pi";
    [JsonRequired] public AgentProfile Profile { get; set; } = new();
    public string Description { get; set; } = "";
    [JsonRequired] public List<AcceptanceItem> Acceptance { get; set; } = [];
    [JsonRequired] public string DirectoryMode { get; set; } = "isolated";
    public ResourceSettings? Resource { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BaseCommit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Branch { get; set; }
    [JsonRequired] public List<Guid> DependsOn { get; set; } = [];
    [JsonRequired] public int Priority { get; set; }
    [JsonRequired] public DateTimeOffset UpdatedAt { get; set; }
    public bool Archived { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QuickLaunchSource? QuickLaunch { get; set; }

    public TaskCard Copy() => new()
    {
        Id = Id, Revision = Revision, Name = Name, Project = Project, AgentId = AgentId, Profile = Profile.Copy(),
        Description = Description, Acceptance = Acceptance.Select(item => item.Copy()).ToList(), DirectoryMode = DirectoryMode,
        Resource = Resource?.Copy(), BaseCommit = BaseCommit, Branch = Branch, DependsOn = [.. DependsOn], Priority = Priority, UpdatedAt = UpdatedAt,
        QuickLaunch = QuickLaunch, Archived = Archived
    };

    public void Validate()
    {
        if (Id == Guid.Empty) throw new InvalidDataException("任务身份缺失。");
        if (Revision < 1) throw new InvalidDataException("任务修订号无效。");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > TaskSchema.MaxNameLength || Name.Contains('\0')) throw new InvalidDataException("任务名称缺失或过长。");
        if (string.IsNullOrWhiteSpace(Project) || Project.Contains('\0')) throw new InvalidDataException("任务项目缺失。");
        if (Description.Length > TaskSchema.MaxDescriptionLength || Description.Contains('\0')) throw new InvalidDataException("任务说明过长或含 NUL。");
        if (Agents.All.All(agent => agent.Id != AgentId)) throw new InvalidDataException("未知 Agent。");
        if (DirectoryMode is not ("shared" or "isolated" or "prepared")) throw new InvalidDataException("未知目录策略。");
        if (Profile.Executable.Contains('\0') || Profile.Arguments.Any(argument => argument.Contains('\0'))) throw new InvalidDataException("任务参数含 NUL。");
        if (Acceptance.Count > TaskSchema.MaxAcceptanceItems) throw new InvalidDataException("验收项过多。");
        if (Acceptance.Any(item => string.IsNullOrWhiteSpace(item.Id) || item.Text.Length > TaskSchema.MaxAcceptanceTextLength || item.Text.Contains('\0'))) throw new InvalidDataException("验收项无效或过长。");
        if (DependsOn.Count > TaskSchema.MaxDependencies || DependsOn.Contains(Id) || DependsOn.Distinct().Count() != DependsOn.Count) throw new InvalidDataException("任务依赖无效。");
        if (Priority is < 0 or > 100) throw new InvalidDataException("优先级超出范围。");
        if (Resource is { } resource)
        {
            if (resource.Mode is not ("conservative" or "practical" or "custom")) throw new InvalidDataException("任务资源模式无效。");
            if (resource.JobMemoryLimit == 0 || resource.PlanningBytes == 0) throw new InvalidDataException("任务资源额度无效。");
        }
        QuickLaunch?.Validate();
    }
}

/// <summary>Queue intent. The revision and resolved command are frozen when the user enqueues; dispatch rechecks them.</summary>
public sealed class QueueEntry
{
    [JsonRequired] public Guid Id { get; set; }
    [JsonRequired] public Guid TaskId { get; set; }
    [JsonRequired] public long TaskRevision { get; set; }
    [JsonRequired] public int Priority { get; set; }
    [JsonRequired] public long Sequence { get; set; }
    [JsonRequired] public DateTimeOffset EnqueuedAt { get; set; }
    [JsonRequired] public string Project { get; set; } = "";
    [JsonRequired] public string AgentId { get; set; } = "";
    [JsonRequired] public CommandSpec Command { get; set; } = new("", [], "");
    [JsonRequired] public string ProgramFingerprint { get; set; } = "";
    [JsonRequired] public string DirectoryMode { get; set; } = "isolated";
    public string Detail { get; set; } = "";
    public bool Deferred { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ResumeOf { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SessionRef { get; set; }
    [JsonRequired] public ResourceSettings Resource { get; set; } = new();
    [JsonRequired] public List<Guid> DependsOn { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public QuickLaunchSource? QuickLaunch { get; set; }
    public QueueEntry Copy() => new()
    {
        Id = Id, TaskId = TaskId, TaskRevision = TaskRevision, Priority = Priority, Sequence = Sequence, EnqueuedAt = EnqueuedAt,
        Project = Project, AgentId = AgentId, Command = Command with { Arguments = [.. Command.Arguments] }, ProgramFingerprint = ProgramFingerprint,
        DirectoryMode = DirectoryMode, Resource = Resource.Copy(), DependsOn = [.. DependsOn],
        Detail = Detail, Deferred = Deferred, ResumeOf = ResumeOf, SessionRef = SessionRef, QuickLaunch = QuickLaunch
    };
}

/// <summary>One dispatched attempt. Task state (review/complete) stays separate from the process receipt states.</summary>
public sealed class TaskRun
{
    [JsonRequired] public Guid TaskId { get; set; }
    [JsonRequired] public long TaskRevision { get; set; }
    [JsonRequired] public Guid AttemptId { get; set; }
    [JsonRequired] public Guid BatchId { get; set; }
    [JsonRequired] public string State { get; set; } = "Running";
    [JsonRequired] public DateTimeOffset At { get; set; }
    public string Detail { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ResumeOf { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SessionRef { get; set; }
    [JsonRequired] public string WorkspaceKey { get; set; } = "";
    [JsonRequired] public string ProgramFingerprint { get; set; } = "";
    public bool Terminal => State is "Review" or "Completed" or "Failed" or "Cancelled";
    public TaskRun Copy() => new()
    {
        TaskId = TaskId, TaskRevision = TaskRevision, AttemptId = AttemptId, BatchId = BatchId, State = State, At = At,
        Detail = Detail, ResumeOf = ResumeOf, SessionRef = SessionRef, WorkspaceKey = WorkspaceKey, ProgramFingerprint = ProgramFingerprint
    };
}

public sealed class TaskBook
{


    [JsonRequired] public int SchemaVersion { get; set; } = TaskSchema.Current;
    [JsonRequired] public long Revision { get; set; }
    [JsonRequired] public List<TaskCard> Tasks { get; set; } = [];
    [JsonRequired] public List<QueueEntry> Queue { get; set; } = [];
    [JsonRequired] public List<TaskRun> Attempts { get; set; } = [];
    [JsonRequired] public int MaxRunning { get; set; } = 1;
    [JsonRequired] public long NextSequence { get; set; }
    public List<Guid>? LastDispatchTaskIds { get; set; }

    public TaskBook Copy() => new()
    {
        SchemaVersion = SchemaVersion, Revision = Revision, Tasks = Tasks.Select(task => task.Copy()).ToList(),
        Queue = Queue.Select(entry => entry.Copy()).ToList(), Attempts = Attempts.Select(run => run.Copy()).ToList(),
        MaxRunning = MaxRunning, NextSequence = NextSequence, LastDispatchTaskIds = LastDispatchTaskIds is null ? null : [.. LastDispatchTaskIds]
    };

    public void Validate()
    {
        if (SchemaVersion != TaskSchema.Current) throw new InvalidDataException("任务文件版本不支持，禁止覆盖。");
        if (Tasks is null || Queue is null || Attempts is null || Tasks.Any(task => task is null) || Queue.Any(entry => entry is null) || Attempts.Any(run => run is null)) throw new InvalidDataException("任务文件结构损坏。");
        if (Tasks.Count > TaskSchema.MaxStoredTasks || Tasks.Count(task => task is not null && !task.Archived) > TaskSchema.MaxTasks || Queue.Count > TaskSchema.MaxQueueEntries || Attempts.Count > TaskSchema.MaxAttempts) throw new InvalidDataException("任务文件超出大小边界。");
        if (MaxRunning is < 1 or > ResourcePolicy.MaxSlots) throw new InvalidDataException("并发上限无效。");
        if (Queue.Count > 0 && NextSequence <= 0) throw new InvalidDataException("队列序号无效。");
        if (Tasks.Select(task => task.Id).Distinct().Count() != Tasks.Count) throw new InvalidDataException("任务身份重复。");
        foreach (var task in Tasks)
        {
            if (task is null) throw new InvalidDataException("任务文件结构损坏。");
            task.Validate();
        }
        foreach (var entry in Queue)
        {
            if (entry is null) throw new InvalidDataException("任务文件结构损坏。");
            entry.QuickLaunch?.Validate();
            if (entry.Id == Guid.Empty || entry.Sequence <= 0 || !Tasks.Any(task => task.Id == entry.TaskId && task.Revision == entry.TaskRevision && !task.Archived)) throw new InvalidDataException("队列项与任务修订不一致或任务已归档。");
            if (Queue.Count(existing => existing.TaskId == entry.TaskId) > 1) throw new InvalidDataException("同一任务重复入队。");
            if (entry.Command.Executable.Length == 0 || entry.Command.Arguments.Any(argument => argument.Contains('\0'))) throw new InvalidDataException("队列项命令无效。");
        }
        if (Attempts.Any(run => run is null) || Attempts.Select(run => run.AttemptId).Distinct().Count() != Attempts.Count) throw new InvalidDataException("尝试身份重复。");
        if (Tasks.Any(task => task.Archived && Attempts.Any(run => run.TaskId == task.Id && !run.Terminal))) throw new InvalidDataException("结果未确认的任务不能归档。");
        if (LastDispatchTaskIds is { } selection && (selection.Count > TaskSchema.MaxTasks || selection.Distinct().Count() != selection.Count || selection.Any(id => !Tasks.Any(task => task.Id == id)))) throw new InvalidDataException("上次批次范围无效。");
    }
}

/// <summary>Pure queue ordering and admission-free scheduling. Resource admission stays with the monitor.</summary>
public sealed record QueueCandidate(Guid EntryId, Guid TaskId, long TaskRevision, int Priority, long Sequence, DateTimeOffset EnqueuedAt, Guid[] DependsOn, bool Deferred = false);
public sealed record QueueWait(Guid EntryId, string Reason);
public sealed record QueueDecision(QueueCandidate? Next, IReadOnlyList<QueueWait> Waiting);

public static class QueuePolicy
{
    public const int DefaultMaxRunning = 1;

    /// <summary>Highest priority first, then FIFO. Only one entry is dispatched per tick; dependencies must already be completed.</summary>
    public static QueueDecision Decide(IReadOnlyList<QueueCandidate> entries, int running, int maxRunning, Func<Guid, bool> taskCompleted)
    {
        if (maxRunning < 1) throw new ArgumentOutOfRangeException(nameof(maxRunning));
        var order = entries.Select((entry, index) => (entry, index))
            .OrderByDescending(item => item.entry.Priority)
            .ThenBy(item => item.entry.Sequence)
            .ThenBy(item => item.index)
            .Select(item => item.entry)
            .ToArray();
        var waiting = new List<QueueWait>();
        QueueCandidate? next = null;
        foreach (var candidate in order)
        {
            if (candidate.Deferred) { waiting.Add(new(candidate.EntryId, "已暂缓；恢复排队后才会执行")); continue; }
            if (next is not null) { waiting.Add(new(candidate.EntryId, "等待前序队列项")); continue; }
            if (running >= maxRunning) { waiting.Add(new(candidate.EntryId, "并发上限")); continue; }
            var missing = candidate.DependsOn.Where(id => !taskCompleted(id)).ToArray();
            if (missing.Length > 0) { waiting.Add(new(candidate.EntryId, "等待依赖任务")); continue; }
            next = candidate;
        }
        return new(next, waiting);
    }

    /// <summary>Duplicate queue entries for one task revision are refused before any dispatch.</summary>
    public static bool CanEnqueue(IReadOnlyList<QueueEntry> queue, Guid taskId) => queue.All(entry => entry.TaskId != taskId);

    /// <summary>Resume requires the previous attempt to be terminal, settled by the user, and from the same workspace and program.</summary>
    public static (bool Allowed, string Reason) CanResume(TaskRun? previous, string workspaceKey, string programFingerprint, IReadOnlyList<TaskRun> attempts)
    {
        if (previous is null) return (false, "没有可续接的尝试。");
        if (!previous.Terminal) return (false, "上一次尝试仍在进行或结果未确认。");
        if (attempts.Any(run => run.SessionRef is { Length: > 0 } session && previous.SessionRef is { Length: > 0 } prior && run.SessionRef == prior && !run.Terminal)) return (false, "该会话仍被其他尝试占用。");
        if (!string.Equals(previous.WorkspaceKey, workspaceKey, StringComparison.Ordinal)) return (false, "工作目录物理身份已改变，不能续接。");
        if (!string.Equals(previous.ProgramFingerprint, programFingerprint, StringComparison.Ordinal)) return (false, "Agent 程序已升级或更换，不能续接旧能力。");
        if (string.IsNullOrWhiteSpace(previous.SessionRef)) return (false, "该尝试没有可核对的会话标识，只能新开任务。");
        return (true, "");
    }

    /// <summary>Dispatch authority for one queue entry: normal entries pass, resume entries must match their frozen parent and registered session.</summary>
    public static (bool Allowed, string Reason) CanDispatchResume(TaskBook book, QueueEntry entry, string workspaceKey, string programFingerprint)
    {
        if (entry.ResumeOf is null && entry.SessionRef is null) return (true, "");
        if (entry.ResumeOf is not { } attemptId || string.IsNullOrWhiteSpace(entry.SessionRef)) return (false, "续接队列项缺少上次尝试或会话标识，请重新入队。");
        var previous = book.Attempts.SingleOrDefault(run => run.AttemptId == attemptId);
        if (previous is null) return (false, "续接引用的上次尝试不存在。");
        if (previous.TaskId != entry.TaskId) return (false, "续接引用的尝试不属于同一任务。");
        if (!string.Equals(previous.SessionRef, entry.SessionRef, StringComparison.Ordinal)) return (false, "队列项会话标识与尝试登记不一致，请重新核对。");
        return CanResume(previous, workspaceKey, programFingerprint, book.Attempts);
    }
}

/// <summary>Book mutations shared by the app and tests. Callers persist the result through the task store.</summary>
public static class TaskBookOps
{
    public static TaskCard Create(TaskBook book, string name, string project, string agentId, AgentProfile profile, string description, IEnumerable<string> acceptance, string directoryMode, ResourceSettings? resource, int priority)
    {
        EnsureTaskCapacity(book, 1);
        var task = new TaskCard
        {
            Id = Guid.NewGuid(), Revision = 1, Name = (name ?? "").Trim(), Project = (project ?? "").Trim(), AgentId = agentId, Profile = profile.Copy(),
            Description = description ?? "",
            Acceptance = (acceptance ?? []).Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => new AcceptanceItem { Id = Guid.NewGuid().ToString("N"), Text = text.Trim() }).ToList(),
            DirectoryMode = directoryMode, Resource = resource?.Copy(), Priority = priority, UpdatedAt = DateTimeOffset.UtcNow
        };
        task.Validate();
        book.Tasks.Add(task);
        return task;
    }

    /// <summary>
    /// Creates one queued task per enabled draft slot from frozen resolved inputs. The whole import is validated and staged
    /// before the book changes, so a bad later item, capacity limit, or duplicate active import leaves the book untouched.
    /// </summary>
    public static IReadOnlyList<Guid> EnqueueQuickLaunch(TaskBook book, Draft draft, IReadOnlyList<QuickLaunchItem> items, string worktreeRoot, bool resetConcurrency = true) =>
        QuickLaunchQueue.Enqueue(book, draft, items, worktreeRoot, resetConcurrency);

    public static void EnsureTaskCapacity(TaskBook book, int additional)
    {
        if (book.Tasks.Count + additional > TaskSchema.MaxStoredTasks) throw new InvalidDataException("任务历史已达保留上限，请导出历史并联系维护者迁移；不会删除身份记录以放行。");
        if (book.Tasks.Count(task => !task.Archived) + additional > TaskSchema.MaxTasks) throw new InvalidDataException("活动任务已达 200 张，请先归档已结束或尚未执行的任务。");
    }

    public static void SetArchived(TaskBook book, Guid taskId, bool archived)
    {
        var task = book.Tasks.SingleOrDefault(candidate => candidate.Id == taskId) ?? throw new InvalidDataException("任务不存在。");
        if (task.Archived == archived) return;
        if (book.Queue.Any(entry => entry.TaskId == taskId) || book.Attempts.Any(run => run.TaskId == taskId && !run.Terminal))
            throw new InvalidDataException("任务仍在排队、运行或结果未确认，核对结束后才能归档。");
        if (!archived) EnsureTaskCapacity(book, 0);
        if (!archived && book.Tasks.Count(candidate => !candidate.Archived) >= TaskSchema.MaxTasks) throw new InvalidDataException("活动任务已满，请先归档其他任务。");
        task.Archived = archived;
        task.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void SetDeferred(TaskBook book, Guid entryId, bool deferred)
    {
        var entry = book.Queue.SingleOrDefault(candidate => candidate.Id == entryId) ?? throw new InvalidDataException("排队项已变化，请刷新后重试。");
        if (book.Attempts.Any(run => run.TaskId == entry.TaskId && !run.Terminal)) throw new InvalidDataException("任务已经执行或结果未确认，请先核对运行记录。");
        entry.Deferred = deferred;
    }

    /// <summary>Edits bump the revision. Launch-relevant edits drop the queued entry because its frozen command no longer matches the task.</summary>
    public static TaskCard Edit(TaskBook book, Guid taskId, Action<TaskCard> change)
    {
        var index = book.Tasks.FindIndex(task => task.Id == taskId);
        if (index < 0) throw new InvalidDataException("任务不存在。");
        var current = book.Tasks[index];
        if (current.Archived) throw new InvalidDataException("请先从归档恢复任务，再编辑。");
        var draft = current.Copy();
        change(draft);
        var launchRelevant = draft.Project != current.Project || draft.AgentId != current.AgentId || draft.DirectoryMode != current.DirectoryMode
            || draft.Priority != current.Priority || !draft.DependsOn.SequenceEqual(current.DependsOn)
            || draft.Profile.Executable != current.Profile.Executable || !draft.Profile.Arguments.SequenceEqual(current.Profile.Arguments)
            || !SameResource(draft.Resource, current.Resource);
        draft.Revision = launchRelevant ? checked(current.Revision + 1) : current.Revision;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        draft.Validate();
        if (launchRelevant)
        {
            // The frozen import no longer describes this launch; a new quick start must freeze fresh metadata.
            draft.QuickLaunch = null;
        }
        book.Tasks[index] = draft;
        if (launchRelevant) book.Queue.RemoveAll(entry => entry.TaskId == taskId);
        return draft;
    }

    public static QueueEntry Enqueue(TaskBook book, Guid taskId, CommandSpec command, string programFingerprint, Guid? resumeOf = null, string? sessionRef = null)
    {
        var task = book.Tasks.SingleOrDefault(candidate => candidate.Id == taskId) ?? throw new InvalidDataException("任务不存在。");
        if (task.Archived) throw new InvalidDataException("请先从归档恢复任务，再加入队列。");
        if (!QueuePolicy.CanEnqueue(book.Queue, taskId)) throw new InvalidDataException("该任务已在队列中。");
        if (book.Queue.Count >= TaskSchema.MaxQueueEntries) throw new InvalidDataException("队列已满。");
        if (programFingerprint.Length != 64) throw new InvalidDataException("程序指纹无效，请重新检查 Agent。");
        var session = string.IsNullOrWhiteSpace(sessionRef) ? null : sessionRef.Trim();
        if (session is { Length: > 200 } || session?.Contains('\0') == true) throw new InvalidDataException("会话标识过长或含 NUL。");
        if (resumeOf is null != session is null) throw new InvalidDataException("续接必须同时给出上次尝试和会话标识。");
        if (resumeOf is { } attemptId)
        {
            var previous = book.Attempts.SingleOrDefault(run => run.AttemptId == attemptId) ?? throw new InvalidDataException("续接引用的上次尝试不存在。");
            if (previous.TaskId != taskId) throw new InvalidDataException("续接引用的尝试不属于同一任务。");
            if (!previous.Terminal) throw new InvalidDataException("只能续接已经结束的尝试。");
            if (!string.Equals(previous.SessionRef, session, StringComparison.Ordinal)) throw new InvalidDataException("续接会话与尝试登记不一致，请重新核对。");
        }
        if (command.Executable.Length == 0 || command.Arguments.Any(argument => argument.Contains('\0'))) throw new InvalidDataException("任务命令无效，请重新检查 Agent。");
        task.Validate();
        var entry = new QueueEntry
        {
            Id = Guid.NewGuid(), TaskId = taskId, TaskRevision = task.Revision, Priority = task.Priority, Sequence = checked(book.NextSequence + 1),
            EnqueuedAt = DateTimeOffset.UtcNow, Project = task.Project, AgentId = task.AgentId,
            Command = command with { Arguments = [.. command.Arguments] }, ProgramFingerprint = programFingerprint,
            DirectoryMode = task.DirectoryMode, Resource = (task.Resource ?? new ResourceSettings()).Copy(), DependsOn = [.. task.DependsOn],
            ResumeOf = resumeOf, SessionRef = session, QuickLaunch = task.QuickLaunch
        };
        book.NextSequence = entry.Sequence;
        book.Queue.Add(entry);
        return entry;
    }

    public static void Dequeue(TaskBook book, Guid taskId) => book.Queue.RemoveAll(entry => entry.TaskId == taskId);

    public static TaskRun StartRun(TaskBook book, QueueEntry entry, Guid attemptId, Guid batchId, string workspaceKey, Guid? resumeOf = null, string? sessionRef = null)
    {
        if (book.Attempts.Count(run => !run.Terminal) >= book.MaxRunning) throw new InvalidDataException("并发上限已满，稍后再试。");
        if (book.Attempts.Any(run => run.AttemptId == attemptId)) throw new InvalidDataException("尝试身份重复。");
        var queued = book.Queue.SingleOrDefault(candidate => candidate.Id == entry.Id) ?? throw new InvalidDataException("队列项已被移除，不能再次派发。");
        if (queued.Deferred) throw new InvalidDataException("该排队项已暂缓，不能派发。");
        if (!SameIntent(queued, entry)) throw new InvalidDataException("队列项已变化，请重新确认。");
        var task = book.Tasks.SingleOrDefault(candidate => candidate.Id == entry.TaskId) ?? throw new InvalidDataException("任务不存在。");
        if (task.Revision != entry.TaskRevision) throw new InvalidDataException("任务已被修改，请重新入队。");
        if (resumeOf != entry.ResumeOf || !string.Equals(sessionRef, entry.SessionRef, StringComparison.Ordinal)) throw new InvalidDataException("续接参数与队列项不一致，请重新确认。");
        if (entry.ResumeOf is not null || entry.SessionRef is not null)
        {
            var (allowed, reason) = QueuePolicy.CanDispatchResume(book, entry, workspaceKey, entry.ProgramFingerprint);
            if (!allowed) throw new InvalidDataException(reason);
        }
        var run = new TaskRun
        {
            TaskId = entry.TaskId, TaskRevision = entry.TaskRevision, AttemptId = attemptId, BatchId = batchId, State = "Running",
            At = DateTimeOffset.UtcNow, ResumeOf = entry.ResumeOf, SessionRef = entry.SessionRef, WorkspaceKey = workspaceKey, ProgramFingerprint = entry.ProgramFingerprint
        };
        book.Attempts.Add(run);
        book.Queue.RemoveAll(candidate => candidate.Id == entry.Id);
        return run;
    }

    /// <summary>Records the Agent session of a settled attempt after checking its physical identity, live leases, and queued resume intent.</summary>
    public static TaskRun RegisterSession(TaskBook book, Guid attemptId, string sessionRef, string workspaceKey, string programFingerprint)
    {
        var session = (sessionRef ?? "").Trim();
        if (session.Length == 0 || session.Length > 200 || session.Contains('\0')) throw new InvalidDataException("会话标识缺失、过长或含 NUL。");
        var run = book.Attempts.SingleOrDefault(candidate => candidate.AttemptId == attemptId) ?? throw new InvalidDataException("尝试不存在。");
        if (!run.Terminal) throw new InvalidDataException("只能为已经结束的尝试登记会话。");
        if (!string.Equals(run.WorkspaceKey, workspaceKey, StringComparison.Ordinal)) throw new InvalidDataException("工作目录物理身份已改变，不能登记会话。");
        if (!string.Equals(run.ProgramFingerprint, programFingerprint, StringComparison.Ordinal)) throw new InvalidDataException("Agent 程序已升级或更换，不能登记会话。");
        if (book.Attempts.Any(candidate => candidate.AttemptId != attemptId && !candidate.Terminal && string.Equals(candidate.SessionRef, session, StringComparison.Ordinal)))
            throw new InvalidDataException("该会话仍被其他尝试占用。");
        if (book.Queue.Any(candidate => candidate.ResumeOf == attemptId && !string.Equals(candidate.SessionRef, session, StringComparison.Ordinal)))
            throw new InvalidDataException("该尝试仍被排队续接引用，不能改写会话标识。");
        run.SessionRef = session;
        return run;
    }

    public static void FinishRun(TaskBook book, Guid attemptId, string state, string detail)
    {
        if (state is not ("Review" or "Failed" or "Cancelled")) throw new InvalidDataException("尝试终态无效。");
        var run = book.Attempts.SingleOrDefault(candidate => candidate.AttemptId == attemptId) ?? throw new InvalidDataException("尝试不存在。");
        if (run.Terminal) return;
        run.State = state;
        run.Detail = ClampDetail(detail);
    }

    /// <summary>The user confirms completion; acceptance items are a checklist, not an automatic gate.</summary>
    public static TaskRun Complete(TaskBook book, Guid taskId)
    {
        var run = book.Attempts.LastOrDefault(candidate => candidate.TaskId == taskId && candidate.State == "Review") ?? throw new InvalidDataException("没有等待验收的尝试。");
        run.State = "Completed";
        run.Detail = string.IsNullOrEmpty(run.Detail) ? "人工验收完成" : run.Detail + "；人工验收完成";
        return run;
    }

    public static void SetAcceptance(TaskBook book, Guid taskId, string itemId, bool value)
    {
        var task = book.Tasks.SingleOrDefault(candidate => candidate.Id == taskId) ?? throw new InvalidDataException("任务不存在。");
        var item = task.Acceptance.SingleOrDefault(candidate => candidate.Id == itemId) ?? throw new InvalidDataException("验收项不存在。");
        if (item.Checked == value) return;
        item.Checked = value;
        task.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static void SetRunDetail(TaskBook book, Guid attemptId, string detail)
    {
        var run = book.Attempts.SingleOrDefault(candidate => candidate.AttemptId == attemptId) ?? throw new InvalidDataException("尝试不存在。");
        if (!run.Terminal) run.Detail = ClampDetail(detail);
    }

    public static string ClampDetail(string? detail)
    {
        var text = detail ?? "";
        return text.Length > TaskSchema.MaxDetailLength ? text[..TaskSchema.MaxDetailLength] + "…" : text;
    }

    public static TaskRun? LastRun(TaskBook book, Guid taskId) => book.Attempts.LastOrDefault(run => run.TaskId == taskId);

    private static bool SameIntent(QueueEntry left, QueueEntry right) =>
        left.TaskId == right.TaskId && left.TaskRevision == right.TaskRevision && left.ProgramFingerprint == right.ProgramFingerprint
        && left.ResumeOf == right.ResumeOf && string.Equals(left.SessionRef, right.SessionRef, StringComparison.Ordinal)
        && left.QuickLaunch == right.QuickLaunch
        && string.Equals(left.Command.Executable, right.Command.Executable, StringComparison.Ordinal)
        && string.Equals(left.Command.Adapter, right.Command.Adapter, StringComparison.Ordinal)
        && left.Command.Arguments.SequenceEqual(right.Command.Arguments);

    private static bool SameResource(ResourceSettings? left, ResourceSettings? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Mode == right.Mode && left.JobMemoryLimit == right.JobMemoryLimit && left.PlanningBytes == right.PlanningBytes
            && left.PhysicalFloorOverride == right.PhysicalFloorOverride && left.CommitFloorOverride == right.CommitFloorOverride && left.CpuRate == right.CpuRate
            && string.Equals(ResourcePolicy.PoolModeOf(left), ResourcePolicy.PoolModeOf(right), StringComparison.Ordinal)
            && left.PoolMemoryLimit == right.PoolMemoryLimit;
    }
}
