namespace AgentLauncher.Core;

// 工作区映射：本地 Windows 路径 → 远程 Linux 路径推导
public static class RemoteWorkspaceMappingHelper
{
    // 自动推导远程工作目录（基于本地路径）
    // 策略：
    //   1. 提取项目名（路径最后一段）
    //   2. 拼接远程基础目录（默认 ~/work）
    //   3. 转义特殊字符（空格、中文）
    public static string DeriveRemoteDirectory(string localPath, string remoteBase = "~/work")
    {
        var projectName = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(projectName)) projectName = "workspace";
        
        // 转义特殊字符（空格 → -，中文 → 拼音或保留）
        projectName = projectName.Replace(" ", "-");
        
        return $"{remoteBase}/{projectName}";
    }
    
    // 验证远程工作目录格式（必须是绝对路径或 ~/ 开头）
    public static bool IsValidRemoteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.StartsWith("/") || path.StartsWith("~/");
    }
    
    // 构建同步命令参数（返回 ArgumentList，不是 shell 字符串）
    // 参数：
    //   - localPath: 本地路径
    //   - host: 远程主机配置
    //   - remotePath: 远程路径
    //   - delete: 是否删除远程多余文件（默认 false，需显式 opt-in）
    //   - dryRun: 是否干跑（只显示将同步的文件，不实际执行）
    // 返回：rsync 参数列表（可直接用于 ProcessStartInfo.ArgumentList）
    public static List<string> BuildSyncArguments(string localPath, RemoteHost host, string remotePath, bool delete = false, bool dryRun = false)
    {
        var args = new List<string> { "-avz" };
        
        // 干跑模式（只显示，不执行）
        if (dryRun) args.Add("--dry-run");
        
        // 删除远程多余文件（危险操作，需显式 opt-in）
        if (delete) args.Add("--delete");
        
        // SSH 选项（端口、密钥）
        var sshCommand = $"ssh -p {host.SshPort}";
        if (!string.IsNullOrEmpty(host.SshKeyPath)) sshCommand += $" -i {host.SshKeyPath}";
        args.Add("-e");
        args.Add(sshCommand);
        
        // 源和目标（确保本地路径以 / 结尾，表示同步目录内容而不是目录本身）
        args.Add(localPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        args.Add($"{host.SshUser}@{host.Address}:{remotePath}/");
        
        return args;
    }
    
    // 估算同步大小（用 du 或 PowerShell 计算本地目录大小）
    public static long EstimateSyncSize(string localPath)
    {
        if (!Directory.Exists(localPath)) return 0;
        
        try
        {
            return new DirectoryInfo(localPath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;  // 访问失败返回 0
        }
    }
    
    // 格式化同步大小（用于 UI 显示）
    public static string FormatSyncSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.1} GB";
    }
}
