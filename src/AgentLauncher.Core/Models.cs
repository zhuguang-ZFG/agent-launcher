using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

public sealed record AgentDefinition(string Id, string Name, string Command);
public static class Agents
{
    public static readonly AgentDefinition[] All = [new("omp", "Omp", "omp"), new("codex", "Codex", "codex"), new("claude", "Claude Code", "claude")];
    // Retired identities remain readable for old configuration and task history; they are never launchable.
    public static readonly AgentDefinition[] Retired =
    [
        new("pi", "Pi", "pi"), new("gemini", "Gemini CLI", "gemini"), new("opencode", "OpenCode", "opencode"),
        new("hermes", "Hermes", "hermes"), new("kimi", "Kimi", "kimi"), new("qoder", "Qoder", "qodercli"),
        new("qodercli", "Qoder", "qodercli"), new("custom", "自定义", "")
    ];
    public static bool IsSupported(string? id) => All.Any(agent => agent.Id == id);
    public static bool IsKnown(string? id) => IsSupported(id) || Retired.Any(agent => agent.Id == id);
    public static AgentDefinition Get(string id) => All.FirstOrDefault(agent => agent.Id == id)
        ?? Retired.FirstOrDefault(agent => agent.Id == id)
        ?? throw new InvalidDataException("未知 Agent。");
    public static int RetiredCount(Draft draft) => draft?.Slots?.Count(slot => slot != null && !IsSupported(slot.AgentId)) ?? 0;
}
public sealed class AgentProfile
{
    [JsonRequired] public string Executable { get; set; } = "";
    [JsonRequired] public string[] Arguments { get; set; } = [];
    public AgentProfile Copy() => new() { Executable = Executable, Arguments = [.. Arguments] };
}
public sealed class Slot
{
    [JsonRequired] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonRequired] public bool Enabled { get; set; }
    [JsonRequired] public string AgentId { get; set; } = "omp";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? CustomName { get; set; }
    [JsonIgnore] public string DisplayName=>!string.IsNullOrWhiteSpace(CustomName)?CustomName:Agents.Get(AgentId).Name;
    [JsonRequired] public string Directory { get; set; } = "";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public bool RequiresDirectory { get; set; }
    public string Note { get; set; } = "";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public Guid? RemoteHostId { get; set; }  // 远程主机 ID（引用 Configuration.RemoteHosts，稳定 ID 不会孤立）
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? RemoteDirectory { get; set; }  // 远程工作目录（如 /home/agent/work）
    [JsonRequired] public Dictionary<string, AgentProfile> Profiles { get; set; } = [];
    [JsonIgnore] public AgentProfile Current => Profiles.GetValueOrDefault(AgentId) ?? new();
    public Slot Copy() => new() { Id = Id, Enabled = Enabled, AgentId = AgentId, CustomName = CustomName, Directory = Directory, RequiresDirectory = RequiresDirectory, Note = Note, RemoteHostId = RemoteHostId, RemoteDirectory = RemoteDirectory, Profiles = Profiles.ToDictionary(p => p.Key, p => p.Value.Copy()) };
    public Slot Duplicate()
    {
        var duplicate = Copy();
        duplicate.Id = Guid.NewGuid();
        duplicate.Enabled = false;
        duplicate.CustomName = DisplayName + " 副本";
        return duplicate;
    }
}
public sealed class Draft
{
    [JsonRequired] public int SchemaVersion { get; set; } = ConfigurationCompatibility.CurrentSchemaVersion;
    [JsonRequired] public long Revision { get; set; }
    [JsonRequired] public string Project { get; set; } = "";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] public bool IsPortableTemplate { get; set; }
    [JsonRequired] public string DirectoryMode { get; set; } = "isolated";
    [JsonRequired] public string Layout { get; set; } = "vertical";
    [JsonRequired] public List<Slot> Slots { get; set; } = [new() { Enabled = true }, new() { AgentId = "codex" }, new() { AgentId = "claude" }, new()];
    public ResourceSettings Resource { get; set; } = new();
    public Draft Copy() => new() { SchemaVersion = SchemaVersion, Revision = Revision, Project = Project, IsPortableTemplate = IsPortableTemplate, DirectoryMode = DirectoryMode, Layout = Layout, Slots = Slots.Select(s => s.Copy()).ToList(), Resource = (Resource ?? new ResourceSettings()).Copy() };
    public void Validate()
    {
        if (SchemaVersion is not (1 or 2) || Slots == null || Slots.Count is < 1 or > ResourcePolicy.MaxSlots || Slots.Any(s => s == null) || Slots.Select(s => s.Id).Distinct().Count() != Slots.Count || !new[] { "isolated", "prepared", "shared" }.Contains(DirectoryMode) || !new[] { "vertical", "horizontal" }.Contains(Layout)) throw new InvalidDataException("配置版本或结构不支持，未修改原配置。");
        if(Project==null)throw new InvalidDataException("项目目录字段缺失，未修改原配置。");
        if (Resource == null || Resource.Mode is not ("conservative" or "practical" or "custom")) throw new InvalidDataException("资源配置不完整或不支持。");
        if (Resource.JobMemoryLimit < ResourcePolicy.MinJobMemoryLimit) throw new InvalidDataException("Job 内存上限不能低于 1 GiB。");
        if (Resource.JobMemoryLimit > ResourcePolicy.MaxJobMemoryLimit) throw new InvalidDataException("Job 内存上限不能超过 8 GiB。");
        if (Resource.PlanningBytes < ResourcePolicy.MinPlanningBytes || Resource.PlanningBytes > Resource.JobMemoryLimit) throw new InvalidDataException("规划估算必须在允许范围内且不超过 Job 上限。");
        if (ResourcePolicy.PoolModeOf(Resource) is not (ResourcePolicy.PoolIndependent or ResourcePolicy.PoolShared)) throw new InvalidDataException("资源池模式不支持。");
        if (ResourcePolicy.PoolModeOf(Resource) == ResourcePolicy.PoolShared)
        {
            var poolLimit = ResourcePolicy.PoolLimitOf(Resource);
            if (poolLimit < ResourcePolicy.MinJobMemoryLimit || poolLimit > ResourcePolicy.MaxJobMemoryLimit) throw new InvalidDataException("资源池上限不能低于 1 GiB 或超过 8 GiB。");
            if (poolLimit < Resource.JobMemoryLimit) throw new InvalidDataException("资源池上限不能低于单项 Job 上限。");
        }
        if (Resource.CpuRate < ResourcePolicy.MinCpuRate || Resource.CpuRate > ResourcePolicy.MaxCpuRate) throw new InvalidDataException("CPU 封顶必须在 15% 到 80% 之间。");
        if (Resource.PhysicalFloorOverride is {} physicalFloor && physicalFloor < ResourcePolicy.MinCustomPhysicalFloor) throw new InvalidDataException("物理临界余量不能低于下限，也不能为 0。");
        if (Resource.PhysicalFloorOverride is {} physicalCeiling && physicalCeiling > ResourcePolicy.MaxCustomPhysicalFloor) throw new InvalidDataException("物理临界余量不能超过 8 GiB。");
        if (Resource.CommitFloorOverride is {} commitFloor && commitFloor < ResourcePolicy.MinCustomCommitFloor) throw new InvalidDataException("提交临界余量不能低于下限，也不能为 0。");
        if (Resource.CommitFloorOverride is {} commitCeiling && commitCeiling > ResourcePolicy.MaxCustomCommitFloor) throw new InvalidDataException("提交临界余量不能超过 16 GiB。");
        foreach (var slot in Slots)
        {
            if(!Agents.IsKnown(slot.AgentId)||(slot.Enabled&&!Agents.IsSupported(slot.AgentId))||slot.Profiles==null||slot.Directory==null||slot.Note==null)throw new InvalidDataException("Agent 配置不完整或包含已移除的类型。");
            if(slot.CustomName?.Contains('\0')==true)throw new InvalidDataException("Agent 名称不能包含 NUL。");
            foreach (var p in slot.Profiles.Values) if(p==null||p.Executable==null||p.Arguments==null||p.Arguments.Any(a=>a==null||a.Contains('\0')))throw new InvalidDataException("程序/参数缺失或含 NUL。");
        }
    }
}
public sealed class Configuration
{
    [JsonRequired] public int SchemaVersion { get; set; } = ConfigurationCompatibility.CurrentSchemaVersion;
    [JsonRequired] public Draft Draft { get; set; } = new();
    public Draft? LastEdited { get; set; }
    public bool ManuallyEdited { get; set; } = true;
    public Dictionary<string, Draft> Templates { get; set; } = [];
    public bool EmbeddedTerminal { get; set; } = true;
    public string TerminalPath { get; set; } = "";
    public string WorktreeDirectory { get; set; } = "";
    public bool MinimizeAfterLaunch { get; set; }
    public string Theme { get; set; } = "system";
    public int TextScalePercent { get; set; } = 100;
    public List<string> RecentProjects { get; set; } = [];
    public List<string> RecentAgents { get; set; } = [];
    public List<RemoteHost> RemoteHosts { get; set; } = [];  // 远程主机配置（VPS）
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? NotifyOnSessionEvents { get; set; }
    [JsonIgnore] public bool NotifyOnSessionEventsEnabled => NotifyOnSessionEvents ?? true;
    public void Edit(Action<Draft> change)
    {
        var next = Draft.Copy(); change(next); next.Validate(); next.Revision++; Draft = next; ManuallyEdited = true;
    }
    public void SaveTemplate(string name,bool portable=false)
    {
        if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("请输入组合名称。");
        if(Templates.ContainsKey(name))throw new ArgumentException("名称已存在，请另存为新名称。");
        var snapshot=Draft.Copy();snapshot.IsPortableTemplate=portable;
        if(portable){snapshot.Project="";foreach(var slot in snapshot.Slots){slot.RequiresDirectory|=slot.Directory.Length>0||snapshot.DirectoryMode=="prepared";slot.Directory="";}}
        Templates.Add(name,snapshot);
    }
    public void Restore(Draft snapshot)
    {
        snapshot.Validate(); var next = snapshot.Copy();
        if(next.IsPortableTemplate){next.Project=Draft.Project;next.IsPortableTemplate=false;}
        if (ManuallyEdited) LastEdited = Draft.Copy();
        next.Revision = Draft.Revision + 1; Draft = next; ManuallyEdited = false;
    }
}
public sealed record MemorySnapshot(ulong Total, ulong Available, ulong CommitLimit, ulong CommitAvailable, double CpuPercent, DateTimeOffset At);
public sealed record Admission(bool Allowed, string Reason);
public sealed record CommandSpec(string Executable, string[] Arguments, string Adapter);
public sealed record WorkspaceIdentity(string Path, string Key);
public sealed record LaunchItem(Guid Id, Guid BatchId, int Position, string Name, string Cwd, WorkspaceIdentity Workspace, bool Shared, CommandSpec Command, ulong Budget, string Kind = "agent", [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? RetryOf = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ulong? PlanningBytes = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ResourceMode = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? SourceSlotId = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? AgentId = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Guid? PoolId = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] RemoteExecutionSpec? RemoteExecution = null);
public sealed class ControlEntry
{
    public required LaunchItem Plan { get; set; }
    public string State { get; set; } = "Pending";
    public string Epoch { get; set; } = "";
    public string Digest { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public string Detail { get; set; } = "等待终端启动确认";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? ExitCode { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ulong? JobMemoryLimit { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ulong? PlanningBytes { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ulong? PhysicalFloor { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ulong? CommitFloor { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public int? CpuRate { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public string? Pool { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] public ulong? PoolMemoryLimit { get; set; }
}
// Single source of truth for "this attempt can no longer be running": the monitor, the ledger migration and every
// resource preview must agree, so the state set is never repeated by hand.
public static class ControlEntryStates
{
    public static bool IsTerminal(ControlEntry entry) => entry.State is "Revoked" or "Settled" or "FailedBeforeStart";
}
public sealed class PoolRecord
{
    public required Guid Id { get; set; }
    public required ulong MemoryLimit { get; set; }
    public required string PolicyHash { get; set; }
    public required Guid BatchId { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record TaskReservationClaim(Guid EntryId, Guid AttemptId);
public sealed class Ledger : IJsonOnDeserialized
{
    public int SchemaVersion { get; set; } = LedgerMigration.CurrentSchemaVersion;
    public string BootId { get; set; } = "";
    public List<ControlEntry> Entries { get; set; } = [];
    public List<PoolRecord> Pools { get; set; } = [];
    private List<TaskReservationClaim> taskClaims = [];
    private bool taskClaimsSpecified;
    public List<TaskReservationClaim> TaskClaims
    {
        get => taskClaims;
        set { taskClaims = value; taskClaimsSpecified = true; }
    }
    // Schema 3 must carry the claims field; presence (not a null/empty default) distinguishes it from a legacy file.
    internal bool TaskClaimsAbsentFromJson { get; private set; }
    void IJsonOnDeserialized.OnDeserialized() => TaskClaimsAbsentFromJson = !taskClaimsSpecified;
    internal void ConfirmTaskClaimsMigrated() => TaskClaimsAbsentFromJson = false;
}
public sealed record Receipt(string Phase, int Pid, long ProcessTime, int? ExitCode, string Detail, DateTimeOffset At, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JobMemoryEvidence? JobMemory = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JobMemoryEvidence? PoolMemory = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? HandshakeAt = null, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? HandshakeWaitMs = null);
public sealed record Request(string Operation, Guid RequestId, LaunchItem[]? Items = null, Guid? Attempt = null, string? Epoch = null, string? Digest = null, string? StatusEpoch = null, string? StatusSignature = null, long? DraftRevision = null, string? PolicyHash = null, string? PlanDigest = null, IReadOnlyList<Guid>? EnabledSlotIds = null, ResourcePolicySnapshot? Policy = null, Guid? TaskEntryId = null, OperatorRelease? Release = null, OperatorReattach? Reattach = null);
public sealed record Response(bool Ok, string Message, ControlEntry[]? Entries = null, MemorySnapshot? Memory = null, string? Epoch = null, string? AdmissionBlockedReason = null, bool More = false, bool Unchanged = false, string? Decision = null, string? StatusSignature = null, int? RootCpuRate = null, bool? ReconcileOffered = null, PoolRecord[]? Pools = null, BudgetItem[]? Budgets = null, string? AdmissionHardBlockedReason = null, bool AdmissionPressure = false, [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool ReattachProbePending = false);

public sealed record ArchivedAttempt(int SchemaVersion,Guid AttemptId,string Digest,ControlEntry Entry);

// R28 操作员核对释放：Judgment 取值 Running（仍在）/ Gone（已不在）/ Unknown（无法判断）。
// 结束本地 SSH 不能证明远程进程已退出；只有 Gone + 释放原因 + Confirmed（二次确认）才允许释放 Disconnected 远程条目。
public sealed record OperatorRelease(string Judgment,string Reason,bool Confirmed);
// 释放成功时写入 attempts/<id>/operator-release.json 的身份绑定证据，先于账本终态化发布。
public sealed record OperatorReleaseRecord(int SchemaVersion,Guid AttemptId,string Host,string RemoteDirectory,string Judgment,string Reason,bool Confirmed,DateTimeOffset At,string Digest);
// R33 操作员确认重新挂接。与 OperatorRelease 分开：确认释放不得变成挂接，挂接也不得把尝试终态化。
// Confirmed 为 false 时监视器拒绝且不探活。成功证据在 attempts/<id>/operator-reattach.json，账本保持非终态。
public sealed record OperatorReattach(bool Confirmed);
public sealed record OperatorReattachRecord(int SchemaVersion,Guid AttemptId,string Host,string RemoteDirectory,string SyncMode,string? SyncSession,string Probe,bool Confirmed,DateTimeOffset At,string Digest);

// A1/B3 工作区同步证据（attempts/<id>/）：sync-session.json 在 Mutagen 会话创建后持久化会话名（名字含随机 GUID，
// 无法从配置反推；App 重启后据此按名匹配 mutagen sync list）。sync-final.json 在远程会话终态时（flush 成功/失败后）
// 写入；会话终止后徽章不再显示「未同步」，而是回退到该记录（synced→已同步，其余 fail-closed→同步有问题）。
public sealed record SyncSessionRecord(int SchemaVersion,string Name,string LocalPath,string RemotePath,DateTimeOffset At);
public sealed record SyncFinalRecord(int SchemaVersion,string State,string Detail,DateTimeOffset At)
{
    public const string Synced="synced";
    public const string Problem="problem";
}

public sealed record PoolLimitNotice(Guid PoolId,int Pid,DateTimeOffset At);
