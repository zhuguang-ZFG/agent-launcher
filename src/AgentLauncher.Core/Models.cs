using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

public sealed record AgentDefinition(string Id, string Name, string Command);
public static class Agents
{
    public static readonly AgentDefinition[] All = [new("pi", "Pi", "pi"), new("codex", "Codex", "codex"), new("claude", "Claude Code", "claude"), new("gemini", "Gemini CLI", "gemini"), new("opencode", "OpenCode", "opencode"), new("hermes", "Hermes", "hermes"), new("custom", "自定义", "")];
    public static AgentDefinition Get(string id) => All.Single(a => a.Id == id);
}
public sealed class AgentProfile
{
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public AgentProfile Copy() => new() { Executable = Executable, Arguments = [.. Arguments] };
}
public sealed class Slot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; }
    public string AgentId { get; set; } = "pi";
    public string Directory { get; set; } = "";
    public string Note { get; set; } = "";
    public Dictionary<string, AgentProfile> Profiles { get; set; } = [];
    [JsonIgnore] public AgentProfile Current => Profiles.GetValueOrDefault(AgentId) ?? new();
    public Slot Copy() => new() { Id = Id, Enabled = Enabled, AgentId = AgentId, Directory = Directory, Note = Note, Profiles = Profiles.ToDictionary(p => p.Key, p => p.Value.Copy()) };
}
public sealed class Draft
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public string Project { get; set; } = "";
    [JsonRequired] public string DirectoryMode { get; set; } = "isolated";
    [JsonRequired] public string Layout { get; set; } = "vertical";
    [JsonRequired] public List<Slot> Slots { get; set; } = [new() { Enabled = true }, new() { AgentId = "codex" }, new(), new() { AgentId = "hermes" }];
    public Draft Copy() => new() { SchemaVersion = SchemaVersion, Revision = Revision, Project = Project, DirectoryMode = DirectoryMode, Layout = Layout, Slots = Slots.Select(s => s.Copy()).ToList() };
    public void Validate()
    {
        if (SchemaVersion != 1 || Slots == null || Slots.Count is < 1 or > 4 || Slots.Any(s => s == null) || Slots.Select(s => s.Id).Distinct().Count() != Slots.Count || !new[] { "isolated", "prepared", "shared" }.Contains(DirectoryMode) || !new[] { "vertical", "horizontal" }.Contains(Layout)) throw new InvalidDataException("配置版本或结构不支持，未修改原配置。");
        foreach (var slot in Slots)
        {
            if(!Agents.All.Any(a=>a.Id==slot.AgentId)||slot.Profiles==null||slot.Directory==null||slot.Note==null)throw new InvalidDataException("Agent 配置不完整。");
            foreach (var p in slot.Profiles.Values) if(p==null||p.Executable==null||p.Arguments==null||p.Arguments.Any(a=>a==null||a.Contains('\0')))throw new InvalidDataException("程序/参数缺失或含 NUL。");
        }
    }
}
public sealed class Configuration
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public Draft Draft { get; set; } = new();
    public Draft? LastEdited { get; set; }
    public bool ManuallyEdited { get; set; } = true;
    public Dictionary<string, Draft> Templates { get; set; } = [];
    public string TerminalPath { get; set; } = "";
    public bool MinimizeAfterLaunch { get; set; }
    public string Theme { get; set; } = "system";
    public List<string> RecentProjects { get; set; } = [];
    public void Edit(Action<Draft> change)
    {
        var next = Draft.Copy(); change(next); next.Validate(); next.Revision++; Draft = next; ManuallyEdited = true;
    }
    public void SaveTemplate(string name) { if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入组合名称。"); if (Templates.ContainsKey(name)) throw new ArgumentException("名称已存在，请另存为新名称。"); Templates.Add(name, Draft.Copy()); }
    public void Restore(Draft snapshot)
    {
        snapshot.Validate(); var next = snapshot.Copy();
        if (ManuallyEdited) LastEdited = Draft.Copy();
        next.Revision = Draft.Revision + 1; Draft = next; ManuallyEdited = false;
    }
}
public sealed record MemorySnapshot(ulong Total, ulong Available, ulong CommitLimit, ulong CommitAvailable, double CpuPercent, DateTimeOffset At);
public sealed record Admission(bool Allowed, string Reason);
public static class ResourcePolicy
{
    public const ulong GiB = 1024UL * 1024 * 1024;
    public const ulong AgentBudget = 2 * GiB;
    public const ulong GitBudget = GiB;
    public static ulong Reserve(ulong total) => Math.Max(3 * GiB, total / 5);
    public static Admission Check(MemorySnapshot sample, ulong reserved, ulong requested)
    {
        // Conservatively reserve the full budget again. Never subtract paged-out commit from physical headroom.
        if (!double.IsFinite(sample.CpuPercent) || DateTimeOffset.UtcNow - sample.At > TimeSpan.FromSeconds(3)) return new(false, "资源采样无效，请重新检查。");
        if (reserved + requested > Math.Min(8 * GiB, sample.Total * 35 / 100)) return new(false, "超过受管内存总预算，请减少启动项。");
        if (sample.Available < Reserve(sample.Total) + reserved + requested) return new(false, "可用内存不足以保留系统余量和本次预算，请减少启动项或关闭其他应用。");
        if (sample.CommitAvailable < Math.Max(2 * GiB, sample.CommitLimit / 20) + reserved + requested) return new(false, "系统提交余量不足，暂缓新增任务。");
        return sample.CpuPercent >= 80 ? new(false, "整机 CPU 繁忙，暂缓新增任务。") : new(true, "资源检查通过");
    }
}
public sealed record CommandSpec(string Executable, string[] Arguments, string Adapter);
public sealed record WorkspaceIdentity(string Path, string Key);
public sealed record LaunchItem(Guid Id, Guid BatchId, int Position, string Name, string Cwd, WorkspaceIdentity Workspace, bool Shared, CommandSpec Command, ulong Budget, string Kind = "agent");
public sealed class ControlEntry
{
    public required LaunchItem Plan { get; set; }
    public string State { get; set; } = "Pending";
    public string Epoch { get; set; } = "";
    public string Digest { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public string Detail { get; set; } = "等待终端启动确认";
}
public sealed class Ledger { public int SchemaVersion { get; set; } = 1; public string BootId { get; set; } = ""; public List<ControlEntry> Entries { get; set; } = []; }
public sealed record Receipt(string Phase, int Pid, long ProcessTime, int? ExitCode, string Detail, DateTimeOffset At);
public sealed record Request(string Operation, Guid RequestId, LaunchItem[]? Items = null, Guid? Attempt = null, string? Epoch = null, string? Digest = null);
public sealed record Response(bool Ok, string Message, ControlEntry[]? Entries = null, MemorySnapshot? Memory = null, string? Epoch = null);
