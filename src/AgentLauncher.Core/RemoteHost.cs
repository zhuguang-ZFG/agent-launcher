using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

// 远程主机配置：定义一个可通过 SSH 访问的 VPS，上面装有 agent CLI
public sealed record RemoteHost(
    Guid Id,                        // 稳定 ID（重命名主机不会孤立槽位）
    string Name,                    // 显示名称，如 "京东云 VPS"
    string Address,                 // IP 或域名，如 "100.85.114.65"
    int SshPort,                    // SSH 端口，通常 22
    string SshUser,                 // SSH 登录用户，如 "root"
    string AgentUser,               // agent 运行用户，如 "agent"
    string[] AvailableAgents,       // 可用的 agent 类型，如 ["pi", "kimi", "qodercli", "omp"]
    string? SshKeyPath,             // SSH 私钥路径（可选；免密登录走 密钥/ssh-agent/SSH config，绝不提示密码）
    WorkspaceSyncMode WorkspaceSyncMode = WorkspaceSyncMode.Mutagen,  // 工作区同步模式（默认 Mutagen，用户明确要求远程 agent 编辑本地工作区）
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] Dictionary<string,string>? AgentUpgradeCommands = null  // 每-agent 升级命令覆盖（可选；缺省用 RemoteAgentMaintenance.DefaultUpgradeCommands）
)
{
    public bool Supports(string agentId) => RemoteAgentMaintenance.AgentCommand(agentId) is {} command
        && AvailableAgents.Any(id => RemoteAgentMaintenance.AgentCommand(id) == command);
}
