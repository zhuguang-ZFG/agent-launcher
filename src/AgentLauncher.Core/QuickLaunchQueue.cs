using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentLauncher.Core;

/// <summary>Frozen provenance of one quick-launch slot. The key identifies the whole semantic import; no draft copy is stored.</summary>
public sealed record QuickLaunchSource(string Key, Guid SlotId, int Position, string WorkspaceKey, string WorktreeRoot, bool IsolateWorkspace)
{
    public void Validate()
    {
        if (Key is null || Key.Length != 64 || !Key.All(Uri.IsHexDigit)) throw new InvalidDataException("快捷启动来源标识无效。");
        if (SlotId == Guid.Empty) throw new InvalidDataException("快捷启动来源缺少启动项身份。");
        if (Position is < 1 or > ResourcePolicy.MaxSlots) throw new InvalidDataException("快捷启动来源位置超出范围。");
        if (string.IsNullOrWhiteSpace(WorkspaceKey) || WorkspaceKey.Contains('\0')) throw new InvalidDataException("快捷启动来源工作目录标识无效。");
        if (WorktreeRoot is null || WorktreeRoot.Contains('\0')) throw new InvalidDataException("快捷启动来源工作目录根无效。");
    }
}

/// <summary>Resolved quick-launch input for one enabled slot. The command is stored verbatim and never re-resolved.</summary>
public sealed record QuickLaunchItem(Guid SlotId, string Project, WorkspaceIdentity Workspace, CommandSpec Command, string ProgramFingerprint)
{
    public void Validate()
    {
        if (SlotId == Guid.Empty) throw new InvalidDataException("快捷启动项缺少启动项身份。");
        if (string.IsNullOrWhiteSpace(Project) || Project.Contains('\0')) throw new InvalidDataException("快捷启动项目目录缺失。");
        if (Workspace is null || string.IsNullOrWhiteSpace(Workspace.Path) || Workspace.Path.Contains('\0')) throw new InvalidDataException("快捷启动工作目录无效。");
        if (string.IsNullOrWhiteSpace(Workspace.Key) || Workspace.Key.Contains('\0')) throw new InvalidDataException("快捷启动工作目录标识无效。");
        if (Command is null || string.IsNullOrEmpty(Command.Executable) || Command.Executable.Contains('\0')) throw new InvalidDataException("快捷启动命令无效。");
        if (Command.Arguments is null || Command.Adapter is null || Command.Adapter.Contains('\0')) throw new InvalidDataException("快捷启动命令适配器或参数无效。");
        if (Command.Arguments.Any(argument => argument is null || argument.Contains('\0'))) throw new InvalidDataException("快捷启动命令参数含 NUL。");
        if (ProgramFingerprint is null || ProgramFingerprint.Length != 64) throw new InvalidDataException("快捷启动程序指纹无效。");
    }
}

/// <summary>Stages a whole quick-launch import so a later invalid item, capacity, or duplicate failure never mutates the book.</summary>
internal static class QuickLaunchQueue
{
    private readonly record struct SlotImport(Slot Slot, int Position, QuickLaunchItem Item, bool Isolate);

    public static IReadOnlyList<Guid> Enqueue(TaskBook book, Draft draft, IReadOnlyList<QuickLaunchItem> items, string worktreeRoot)
    {
        if (items is null) throw new InvalidDataException("快捷启动项缺失。");
        if (worktreeRoot is null || worktreeRoot.Contains('\0')) throw new InvalidDataException("快捷启动工作目录根无效。");
        draft.Validate();
        var active = draft.Slots.Where(slot => slot.Enabled).ToArray();
        if (active.Length == 0) throw new InvalidDataException("先勾选本次启动项。");
        if (items.Count != active.Length) throw new InvalidDataException("快捷启动项与已勾选启动项数量不一致。");
        var bySlot = new Dictionary<Guid, QuickLaunchItem>(active.Length);
        foreach (var item in items)
        {
            if (item is null) throw new InvalidDataException("快捷启动项缺失。");
            item.Validate();
            if (!bySlot.TryAdd(item.SlotId, item)) throw new InvalidDataException("快捷启动项重复。");
        }
        var positions = new Dictionary<Guid, int>(draft.Slots.Count);
        for (var index = 0; index < draft.Slots.Count; index++) positions[draft.Slots[index].Id] = index + 1;
        foreach (var slot in active) if (!bySlot.ContainsKey(slot.Id)) throw new InvalidDataException("快捷启动项与已勾选启动项不匹配。");
        var duplicateWorkspace = active.GroupBy(slot => bySlot[slot.Id].Workspace.Key, StringComparer.Ordinal).Any(group => group.Count() > 1);
        if (duplicateWorkspace && draft.DirectoryMode == "prepared") throw new InvalidDataException("已有目录模式下必须为每一项指定不同的工作目录。");
        var root = worktreeRoot.Trim();
        var imports = active.Select(slot => new SlotImport(
            slot,
            positions[slot.Id],
            bySlot[slot.Id],
            draft.DirectoryMode == "isolated" && active.Count(other => string.Equals(bySlot[other.Id].Workspace.Key, bySlot[slot.Id].Workspace.Key, StringComparison.Ordinal)) > 1)).ToArray();
        var key = ComputeKey(draft, imports, root);
        if (HasActiveKey(book, key)) throw new InvalidDataException("相同的快捷启动仍在队列中或正在运行，未重复加入。");
        if (book.Tasks.Count + imports.Length > TaskSchema.MaxTasks) throw new InvalidDataException("任务数量已达上限。");
        if (book.Queue.Count + imports.Length > TaskSchema.MaxQueueEntries) throw new InvalidDataException("队列已满。");

        // New quick tasks are billed independently: a shared draft pool only describes the pre-freeze configuration.
        var resource = draft.Resource.Copy();
        resource.Pool = ResourcePolicy.PoolIndependent;
        resource.PoolMemoryLimit = null;
        var sequence = book.NextSequence;
        var now = DateTimeOffset.UtcNow;
        var staged = new List<(TaskCard Task, QueueEntry Entry)>(imports.Length);
        foreach (var import in imports)
        {
            var source = new QuickLaunchSource(key, import.Slot.Id, import.Position, import.Item.Workspace.Key, root, import.Isolate);
            source.Validate();
            var task = new TaskCard
            {
                Id = Guid.NewGuid(), Revision = 1, Name = BoundedName(import.Slot.DisplayName, import.Position), Project = import.Item.Project.Trim(),
                AgentId = import.Slot.AgentId, Profile = import.Slot.Current.Copy(), Description = import.Slot.Note,
                Acceptance = [], DirectoryMode = draft.DirectoryMode, Resource = resource.Copy(), DependsOn = [], Priority = 0,
                UpdatedAt = now, QuickLaunch = source
            };
            task.Validate();
            sequence = checked(sequence + 1);
            var entry = new QueueEntry
            {
                Id = Guid.NewGuid(), TaskId = task.Id, TaskRevision = task.Revision, Priority = task.Priority, Sequence = sequence,
                EnqueuedAt = now, Project = task.Project, AgentId = task.AgentId,
                Command = import.Item.Command with { Arguments = [.. import.Item.Command.Arguments] },
                ProgramFingerprint = import.Item.ProgramFingerprint, DirectoryMode = task.DirectoryMode,
                Resource = resource.Copy(), DependsOn = [], QuickLaunch = source
            };
            staged.Add((task, entry));
        }

        var ids = new List<Guid>(staged.Count);
        foreach (var (task, entry) in staged)
        {
            book.Tasks.Add(task);
            book.Queue.Add(entry);
            ids.Add(task.Id);
        }
        book.NextSequence = sequence;
        book.MaxRunning = 1;
        return ids;
    }

    /// <summary>An import whose exact frozen key is queued or has a nonterminal run cannot be added again.</summary>
    private static bool HasActiveKey(TaskBook book, string key) =>
        book.Tasks.Any(task => task.QuickLaunch is { } source && string.Equals(source.Key, key, StringComparison.Ordinal)
            && (book.Queue.Any(entry => entry.TaskId == task.Id) || book.Attempts.Any(run => run.TaskId == task.Id && !run.Terminal)));

    /// <summary>Canonical hash of the enabled draft slots, resolved inputs, and destination; the revision is normalized away.</summary>
    private static string ComputeKey(Draft draft, IReadOnlyList<SlotImport> imports, string worktreeRoot)
    {
        var canonical = new
        {
            Schema = 1,
            Mode = draft.DirectoryMode,
            Resource = new object?[]
            {
                draft.Resource.Mode, draft.Resource.JobMemoryLimit, draft.Resource.PlanningBytes,
                draft.Resource.PhysicalFloorOverride, draft.Resource.CommitFloorOverride, draft.Resource.CpuRate,
                ResourcePolicy.PoolModeOf(draft.Resource), draft.Resource.PoolMemoryLimit
            },
            Root = NormalizeRoot(worktreeRoot),
            Slots = imports.Select(import => new
            {
                import.Position,
                SlotId = import.Slot.Id,
                import.Slot.AgentId,
                import.Slot.CustomName,
                import.Slot.Directory,
                import.Slot.RequiresDirectory,
                import.Slot.Note,
                Profiles = import.Slot.Profiles.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { Agent = pair.Key, pair.Value.Executable, Arguments = pair.Value.Arguments }),
                Project = import.Item.Project.Trim(),
                WorkspacePath = import.Item.Workspace.Path,
                WorkspaceKey = import.Item.Workspace.Key,
                Command = new { import.Item.Command.Executable, Arguments = import.Item.Command.Arguments, import.Item.Command.Adapter },
                import.Item.ProgramFingerprint,
                import.Isolate
            }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    private static string NormalizeRoot(string root) => root.Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    private static string BoundedName(string displayName, int position)
    {
        var suffix = " · " + position.ToString(CultureInfo.InvariantCulture);
        var limit = TaskSchema.MaxNameLength - suffix.Length;
        var prefix = displayName.Length <= limit ? displayName : Truncate(displayName, limit);
        return prefix + suffix;
    }

    private static string Truncate(string text, int limit)
    {
        if (limit <= 0) return "";
        if (limit < text.Length && char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }
}
