namespace AgentLauncher.Core;

// 远程 Agent 维护（纯逻辑，无进程/磁盘访问）：版本检查与升级命令解析。
// 升级默认值按各 CLI 的已知安装渠道给出；主机可通过 RemoteHost.AgentUpgradeCommands 逐项覆盖。
public static class RemoteAgentMaintenance
{
    // 每-AgentType 默认升级命令（npm/pip 包管理器更新；qodercli 兼容目录 Id "qoder"）。
    public static readonly IReadOnlyDictionary<string,string> DefaultUpgradeCommands=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
    {
        ["pi"]="npm install -g @earendil-works/pi-coding-agent@latest",
        ["kimi"]="python3 -m pip install --upgrade kimi-cli",
        ["qodercli"]="npm install -g @qoder-ai/qodercli@latest",
        ["qoder"]="npm install -g @qoder-ai/qodercli@latest",
        ["omp"]="npm install -g @oh-my-pi/pi-coding-agent@latest",
        ["codex"]="npm install -g @openai/codex@latest"
    };

    // 远程主机上的 agent CLI 命令名（与 SshRunnerBackend.Create 的 AgentType 键一致）。
    public static string? AgentCommand(string agentType)=>agentType switch
    {
        "pi"=>"pi","kimi"=>"kimi","qodercli"=>"qodercli","qoder"=>"qodercli","omp"=>"omp","codex"=>"codex",_=>null
    };

    public static string? VersionCommandFor(string agentType)=>AgentCommand(agentType) is {} command?command+" --version":null;

    // 主机覆盖优先；无覆盖回退默认表；未知 agent 返回 null（调用方 fail-closed 报错）。
    public static string? UpgradeCommandFor(RemoteHost host,string agentType)
    {
        if(host.AgentUpgradeCommands?.GetValueOrDefault(agentType) is {Length:>0} overrideCommand)return overrideCommand;
        return DefaultUpgradeCommands.GetValueOrDefault(agentType);
    }
}