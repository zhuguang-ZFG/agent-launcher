namespace AgentLauncher.Core;

// 远程主机配置界面的纯校验逻辑：与 WPF 解耦，便于边界测试与复用。
public static class RemoteHostEditor
{
    public static string[] ParseAgents(string? text) =>
        (text ?? "").Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray();

    public static WorkspaceSyncMode ParseSyncMode(string? text, WorkspaceSyncMode fallback) =>
        (text ?? "").Trim().ToLowerInvariant() switch
        {
            "mutagen" => WorkspaceSyncMode.Mutagen,
            "sshfs" => WorkspaceSyncMode.SshfsMount,
            "none" => WorkspaceSyncMode.None,
            _ => fallback
        };

    public static string SyncModeText(WorkspaceSyncMode mode) => mode switch
    {
        WorkspaceSyncMode.SshfsMount => "sshfs",
        WorkspaceSyncMode.None => "none",
        _ => "mutagen"
    };

    // 返回 null 表示合法；否则返回可直接展示给用户的错误说明。
    public static string? Validate(string? name, string? address, string? portText, string? agentsText)
    {
        if (string.IsNullOrWhiteSpace(name)) return "名称不能为空。";
        if (string.IsNullOrWhiteSpace(address)) return "地址不能为空。";
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535) return "端口必须是 1-65535 的数字。";
        if (ParseAgents(agentsText).Length == 0) return "至少指定一个可用 agent 类型。";
        return null;
    }

    // 名称和地址各自唯一，避免槽位配置出现同名歧义；exceptId 用于编辑时排除自身。
    public static string? Duplicate(IEnumerable<RemoteHost> hosts, string name, string address, Guid? exceptId = null)
    {
        foreach (var host in hosts)
        {
            if (exceptId is { } id && host.Id == id) continue;
            if (string.Equals(host.Name, name, StringComparison.OrdinalIgnoreCase)) return $"已存在同名主机「{host.Name}」。";
            if (string.Equals(host.Address, address, StringComparison.OrdinalIgnoreCase)) return $"地址「{address}」已被主机「{host.Name}」使用。";
        }
        return null;
    }

    // 槽位以稳定 Id 引用主机：重命名不会孤立槽位，删除前必须核对引用。
    public static string[] ReferencingSlotNames(IEnumerable<(string Name, Guid? HostId)> slots, Guid id) =>
        slots.Where(s => s.HostId == id).Select(s => s.Name).ToArray();
}
