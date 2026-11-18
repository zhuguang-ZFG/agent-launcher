namespace AgentLauncher.Core;

// 远程执行配置：定义如何在远程主机上启动 agent
// 独立于 CommandSpec，避免污染本地启动/队列/指纹/审阅验证
public sealed record RemoteExecutionSpec(
    string Host,                    // 远程主机地址（如 "100.85.114.65"）
    int SshPort,                    // SSH 端口（通常 22）
    string SshUser,                 // SSH 登录用户（如 "root"）
    string AgentUser,               // agent 运行用户（如 "agent"）
    string AgentType,               // agent 类型（如 "pi", "kimi", "qodercli", "omp", "codex"）
    string RemoteWorkingDirectory,  // 远程工作目录（如 "/home/agent/work"）
    string[] Arguments,             // agent 参数
    long? MemoryLimitBytes,         // 内存限制（可选，远程 cgroup）
    string? SshKeyPath,             // SSH 私钥路径（可选）
    WorkspaceSyncSpec? WorkspaceSync = null  // 工作区同步配置（可选，默认不同步）
)
{
    public void Validate()
    {
        static bool User(string value) => !string.IsNullOrWhiteSpace(value) && value[0] != '-' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        if (string.IsNullOrWhiteSpace(Host) || Host[0] == '-' || !Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':' or '_') || SshPort is < 1 or > 65535 || !User(SshUser) || !User(AgentUser))
            throw new InvalidDataException("远程主机、端口或用户名无效。");
        if (!RemoteWorkspaceMappingHelper.IsValidRemoteDirectory(RemoteWorkingDirectory) || RemoteWorkingDirectory.Any(char.IsControl) || Arguments is null || Arguments.Any(a => a is null || a.Contains('\0')))
            throw new InvalidDataException("远程目录或参数无效。");
        // 远程是 Linux 环境：盘符/UNC 路径只可能来自误用的本地 profile，拒绝而不是静默传给远端。
        if (Arguments.Any(a => System.Text.RegularExpressions.Regex.IsMatch(a, @"(^|[^A-Za-z0-9])[A-Za-z]:[\\/]") || a.StartsWith("\\\\")))
            throw new InvalidDataException("远程参数包含 Windows 本地路径，远程主机无法访问；请改用远程目录或移除该参数。");
        if (RemoteAgentMaintenance.AgentCommand(AgentType) is null)
            throw new InvalidDataException("远程 Agent 类型不支持。");
        if (MemoryLimitBytes is not null) throw new InvalidDataException("尚未实现远程内存限制，请在远程主机管理资源。");
        if (WorkspaceSync is {} sync && (sync.RemotePath != RemoteWorkingDirectory || !Path.IsPathFullyQualified(sync.LocalPath) || !Enum.IsDefined(sync.Mode)))
            throw new InvalidDataException("远程同步目录与启动目录不一致。");
    }
}

// 远程工作区映射：本地 Windows 路径 → 远程 Linux 路径
public sealed record RemoteWorkspaceMapping(
    string LocalPath,               // 本地路径（如 "D:\work\project"）
    string RemotePath,              // 远程路径（如 "/home/agent/work/project"）
    bool SyncOnLaunch               // 启动时是否同步（rsync/scp）
);

// 工作区同步模式
public enum WorkspaceSyncMode
{
    None,           // 不同步（远程 agent 无法访问本机文件）
    Mutagen,        // Mutagen 持续同步（高性能，推荐）
    SshfsMount      // SSHFS 挂载（透明访问，需要 WinFsp/SSHFS-Win）
}

// 工作区同步配置
public sealed record WorkspaceSyncSpec(
    WorkspaceSyncMode Mode,         // 同步模式
    string LocalPath,               // 本地路径
    string RemotePath,              // 远程路径
    string? MutagenSessionName,     // Mutagen 会话名称（可选，默认自动生成）
    string? SshfsMountPoint         // SSHFS 挂载点（可选，Windows 驱动器号如 "Z:"）
);
