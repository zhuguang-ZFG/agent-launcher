using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

public static class TaskSchema
{
    public const int Current = 1;
    public const int MaxNameLength = 120;
    public const int MaxDescriptionLength = 20000;
    public const int MaxAcceptanceItems = 32;
    public const int MaxAcceptanceTextLength = 500;
    public const int MaxTasks = 200;
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

    public TaskCard Copy() => new()
    {
        Id = Id, Revision = Revision, Name = Name, Project = Project, AgentId = AgentId, Profile = Profile.Copy(),
        Description = Description, Acceptance = Acceptance.Select(item => item.Copy()).ToList(), DirectoryMode = DirectoryMode,
        Resource = Resource?.Copy(), BaseCommit = BaseCommit, Branch = Branch, DependsOn = [.. DependsOn], Priority = Priority, UpdatedAt = UpdatedAt
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ResumeOf { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SessionRef { get; set; }
    [JsonRequired] public ResourceSettings Resource { get; set; } = new();
    [JsonRequired] public List<Guid> DependsOn { get; set; } = [];
    public QueueEntry Copy() => new()
    {
        Id = Id, TaskId = TaskId, TaskRevision = TaskRevision, Priority = Priority, Sequence = Sequence, EnqueuedAt = EnqueuedAt,
        Project = Project, AgentId = AgentId, Command = Command with { Arguments = [.. Command.Arguments] }, ProgramFingerprint = ProgramFingerprint,
        DirectoryMode = DirectoryMode, Resource = Resource.Copy(), DependsOn = [.. DependsOn],
        Detail = Detail, ResumeOf = ResumeOf, SessionRef = SessionRef
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

    public TaskBook Copy() => new()
    {
        SchemaVersion = SchemaVersion, Revision = Revision, Tasks = Tasks.Select(task => task.Copy()).ToList(),
        Queue = Queue.Select(entry => entry.Copy()).ToList(), Attempts = Attempts.Select(run => run.Copy()).ToList(),
        MaxRunning = MaxRunning, NextSequence = NextSequence
    };

    public void Validate()
    {
        if (SchemaVersion != TaskSchema.Current) throw new InvalidDataException("任务文件版本不支持，禁止覆盖。");
        if (Tasks.Count > TaskSchema.MaxTasks || Queue.Count > TaskSchema.MaxQueueEntries || Attempts.Count > TaskSchema.MaxAttempts) throw new InvalidDataException("任务文件超出大小边界。");
        if (MaxRunning is < 1 or > ResourcePolicy.MaxSlots) throw new InvalidDataException("并发上限无效。");
        if (Queue.Count > 0 && NextSequence <= 0) throw new InvalidDataException("队列序号无效。");
        if (Tasks.Select(task => task.Id).Distinct().Count() != Tasks.Count) throw new InvalidDataException("任务身份重复。");
        foreach (var task in Tasks) task.Validate();
        foreach (var entry in Queue)
        {
            if (entry.Id == Guid.Empty || entry.Sequence <= 0 || !Tasks.Any(task => task.Id == entry.TaskId && task.Revision == entry.TaskRevision)) throw new InvalidDataException("队列项与任务修订不一致。");
            if (Queue.Count(existing => existing.TaskId == entry.TaskId) > 1) throw new InvalidDataException("同一任务重复入队。");
            if (entry.Command.Executable.Length == 0 || entry.Command.Arguments.Any(argument => argument.Contains('\0'))) throw new InvalidDataException("队列项命令无效。");
        }
        if (Attempts.Select(run => run.AttemptId).Distinct().Count() != Attempts.Count) throw new InvalidDataException("尝试身份重复。");
    }
}

/// <summary>Pure queue ordering and admission-free scheduling. Resource admission stays with the monitor.</summary>
public sealed record QueueCandidate(Guid EntryId, Guid TaskId, long TaskRevision, int Priority, long Sequence, DateTimeOffset EnqueuedAt, Guid[] DependsOn);
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
}

/// <summary>Book mutations shared by the app and tests. Callers persist the result through the task store.</summary>
public static class TaskBookOps
{
    public static TaskCard Create(TaskBook book, string name, string project, string agentId, AgentProfile profile, string description, IEnumerable<string> acceptance, string directoryMode, ResourceSettings? resource, int priority)
    {
        if (book.Tasks.Count >= TaskSchema.MaxTasks) throw new InvalidDataException("任务数量已达上限。");
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

    /// <summary>Edits bump the revision. Launch-relevant edits drop the queued entry because its frozen command no longer matches the task.</summary>
    public static TaskCard Edit(TaskBook book, Guid taskId, Action<TaskCard> change)
    {
        var index = book.Tasks.FindIndex(task => task.Id == taskId);
        if (index < 0) throw new InvalidDataException("任务不存在。");
        var current = book.Tasks[index];
        var draft = current.Copy();
        change(draft);
        var launchRelevant = draft.Project != current.Project || draft.AgentId != current.AgentId || draft.DirectoryMode != current.DirectoryMode
            || draft.Priority != current.Priority || !draft.DependsOn.SequenceEqual(current.DependsOn)
            || draft.Profile.Executable != current.Profile.Executable || !draft.Profile.Arguments.SequenceEqual(current.Profile.Arguments)
            || !SameResource(draft.Resource, current.Resource);
        draft.Revision = launchRelevant ? checked(current.Revision + 1) : current.Revision;
        draft.UpdatedAt = DateTimeOffset.UtcNow;
        draft.Validate();
        book.Tasks[index] = draft;
        if (launchRelevant) book.Queue.RemoveAll(entry => entry.TaskId == taskId);
        return draft;
    }

    public static QueueEntry Enqueue(TaskBook book, Guid taskId, CommandSpec command, string programFingerprint, Guid? resumeOf = null, string? sessionRef = null)
    {
        var task = book.Tasks.SingleOrDefault(candidate => candidate.Id == taskId) ?? throw new InvalidDataException("任务不存在。");
        if (!QueuePolicy.CanEnqueue(book.Queue, taskId)) throw new InvalidDataException("该任务已在队列中。");
        if (book.Queue.Count >= TaskSchema.MaxQueueEntries) throw new InvalidDataException("队列已满。");
        if (programFingerprint.Length != 64) throw new InvalidDataException("程序指纹无效，请重新检查 Agent。");
        if (sessionRef is { Length: > 200 } || sessionRef?.Contains('\0') == true) throw new InvalidDataException("会话标识过长或含 NUL。");
        if (resumeOf is null != string.IsNullOrWhiteSpace(sessionRef)) throw new InvalidDataException("续接必须同时给出上次尝试和会话标识。");
        if (command.Executable.Length == 0 || command.Arguments.Any(argument => argument.Contains('\0'))) throw new InvalidDataException("任务命令无效，请重新检查 Agent。");
        task.Validate();
        var entry = new QueueEntry
        {
            Id = Guid.NewGuid(), TaskId = taskId, TaskRevision = task.Revision, Priority = task.Priority, Sequence = checked(book.NextSequence + 1),
            EnqueuedAt = DateTimeOffset.UtcNow, Project = task.Project, AgentId = task.AgentId,
            Command = command with { Arguments = [.. command.Arguments] }, ProgramFingerprint = programFingerprint,
            DirectoryMode = task.DirectoryMode, Resource = (task.Resource ?? new ResourceSettings()).Copy(), DependsOn = [.. task.DependsOn],
            ResumeOf = resumeOf, SessionRef = string.IsNullOrWhiteSpace(sessionRef) ? null : sessionRef
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
        var run = new TaskRun
        {
            TaskId = entry.TaskId, TaskRevision = entry.TaskRevision, AttemptId = attemptId, BatchId = batchId, State = "Running",
            At = DateTimeOffset.UtcNow, ResumeOf = resumeOf, SessionRef = sessionRef, WorkspaceKey = workspaceKey, ProgramFingerprint = entry.ProgramFingerprint
        };
        book.Attempts.Add(run);
        book.Queue.RemoveAll(candidate => candidate.Id == entry.Id);
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

    private static bool SameResource(ResourceSettings? left, ResourceSettings? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Mode == right.Mode && left.JobMemoryLimit == right.JobMemoryLimit && left.PlanningBytes == right.PlanningBytes
            && left.PhysicalFloorOverride == right.PhysicalFloorOverride && left.CommitFloorOverride == right.CommitFloorOverride && left.CpuRate == right.CpuRate;
    }
}
