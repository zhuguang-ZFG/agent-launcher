using System.Diagnostics;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

// 工作区同步：Mutagen 持续同步（推荐）或 SSHFS 挂载（备选）
public static class WorkspaceSync
{
    // 启动器会话名前缀（完整名 = 前缀 + 随机 GUID）；同步健康核对（WorkspaceInsights）用它识别启动器创建的会话。
    internal const string SessionNamePrefix="agent-launcher-";
    // 启动工作区同步（在 agent 启动前调用）
    public static async Task<IWorkspaceSyncSession> StartAsync(
        WorkspaceSyncSpec spec,
        RemoteExecutionSpec remote,
        CancellationToken cancellation = default)
    {
        return spec.Mode switch
        {
            WorkspaceSyncMode.Mutagen => await MutagenSyncSession.StartAsync(spec, remote, cancellation),
            WorkspaceSyncMode.SshfsMount => await SshfsMountSession.StartAsync(spec, remote, cancellation),
            WorkspaceSyncMode.None => new NoOpSyncSession(),
            _ => throw new ArgumentException($"未知的同步模式：{spec.Mode}")
        };
    }
}

// 工作区同步会话（生命周期管理）
public interface IWorkspaceSyncSession : IAsyncDisposable
{
    // Mutagen 会话名（B3 持久化到 attempts/<id>/sync-session.json，App 重启后按名匹配）；非 Mutagen 模式为 null。
    string? SessionName { get; }

    // 等待同步就绪（初始同步完成）
    Task WaitReadyAsync(CancellationToken cancellation = default);
    
    // 获取远程工作目录（agent 应该使用的路径）
    string GetRemoteWorkingDirectory();
    
    // 尝试清理会话（返回错误，不抛出异常）
    Task<string?> TryDisposeAsync();
}

// Mutagen 持续同步会话
internal sealed class MutagenSyncSession : IWorkspaceSyncSession
{
    private readonly string _sessionName;
    private readonly string _remotePath;
    private readonly Process? _daemonProcess;
    
    private MutagenSyncSession(string sessionName, string remotePath, Process? daemonProcess)
    {
        _sessionName = sessionName;
        _remotePath = remotePath;
        _daemonProcess = daemonProcess;
    }
    
    public static async Task<MutagenSyncSession> StartAsync(
        WorkspaceSyncSpec spec,
        RemoteExecutionSpec remote,
        CancellationToken cancellation)
    {
        var sessionName = spec.MutagenSessionName ?? $"{WorkspaceSync.SessionNamePrefix}{Guid.NewGuid():N}";
        
        // 配置真实 SSH 路径（避免 fake SSH 拦截）
        // Mutagen 支持 MUTAGEN_SSH_PATH 环境变量（指向包含 ssh 和 scp 的目录）
        // Windows OpenSSH 用不了 MSYS agent 套接字，必须与 ssh-agent 同源于 Git 安装目录（按 git.exe 位置推导）。
        var gitSshDir=SshAgent.GitSshBinDirectory();
        if(gitSshDir!=null&&File.Exists(Path.Combine(gitSshDir,"ssh.exe"))&&File.Exists(Path.Combine(gitSshDir,"scp.exe")))
        {
            Environment.SetEnvironmentVariable("MUTAGEN_SSH_PATH",gitSshDir);
        }
        
        // 启动 Mutagen daemon（如果未运行）
        // mutagen 没有 per-session 密钥参数，靠 ssh-agent / SSH config 认证。先把 SshKeyPath 加载进 ssh-agent，
        // 并确保 daemon 继承 SSH_AUTH_SOCK，否则自定义密钥的主机会认证失败（上层 fail-closed 阻止启动）。
        var agentNewlyStarted=false;
        if(!string.IsNullOrEmpty(remote.SshKeyPath))
            agentNewlyStarted=await EnsureSshKeyInAgentAsync(remote.SshKeyPath,cancellation);
        var daemonProcess = await EnsureMutagenDaemonAsync(cancellation,agentNewlyStarted);
        
        // 创建同步会话；Mutagen 以 AgentUser 连接并创建文件，与远程 agent 使用同一身份。
        var createArgs = new[]
        {
            "sync", "create",
            "--name", sessionName,
            "--sync-mode", "two-way-resolved",  // 双向同步，冲突时保留两个版本
            "--ignore-vcs",  // 忽略 .git 目录（避免冲突）
            "--ignore", "node_modules/",  // 忽略 node_modules
            "--ignore", "bin/",
            "--ignore", "obj/",
            "--default-file-mode", "0664",  // 文件权限：owner/group 可读写，other 可读
            "--default-directory-mode", "0775",  // 目录权限：owner/group 可读写执行，other 可读执行
            spec.LocalPath,
            $"{remote.AgentUser}@{remote.Host}:{spec.RemotePath}"  // 用 AgentUser（agent 可写）
        };
        
        await RunMutagenCommandAsync(createArgs, cancellation);
        
        // Mutagen 已切到 AgentUser，不需要 root 权限修复。
        return new MutagenSyncSession(sessionName, spec.RemotePath, daemonProcess);
    }
    
    public string? SessionName => _sessionName;

    public async Task WaitReadyAsync(CancellationToken cancellation)
    {
        // 就绪判据：整体 Watching 且无 Transition problems。只看 Watching 会误判——远端目录创建失败
        // （如 work 父目录属 root、agent 无写权）时 Mutagen 仍报 Watching 但带 Transition problems、
        // Beta 端 0 directories；此时启动会让远端 agent cd 进不存在的目录而退出。fail-closed：有问题就继续等。
        var deadline = DateTime.UtcNow.AddMinutes(2);
        var lastOutput = "";
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();

            lastOutput = await RunMutagenCommandAsync(new[] { "sync", "list", _sessionName }, cancellation);
            if (lastOutput.Contains("Status: Watching for changes") && !lastOutput.Contains("Transition problems:")) return;  // 就绪

            await Task.Delay(500, cancellation);
        }

        var hint = lastOutput.Contains("Transition problems:")
            ? "远端端点存在同步问题（常见原因：远程工作目录的父目录不属于 AgentUser，agent 无写权创建目录；请在远程主机把该父目录 chown 给 agent）。"
            : "未观察到 Watching 状态（可能 SSH 认证或连接失败）。";
        throw new TimeoutException($"Mutagen 同步就绪超时（2 分钟）。{hint} 会话 {_sessionName} 已保留，可用 mutagen sync list {_sessionName} 查看。");
    }
    
    public string GetRemoteWorkingDirectory() => _remotePath;
    
    public async ValueTask DisposeAsync()
    {
        // Dispose 不能抛出异常（会导致进程退出），用 TryDisposeAsync 返回错误
        var error = await TryDisposeAsync();
        if (error is not null)
        {
            // 记录到 stderr（调用者应该捕获并写入回执）
            Console.Error.WriteLine($"工作区同步清理失败：{error}");
        }
    }
    
    public async Task<string?> TryDisposeAsync()
    {
        // 等待同步收敛（最后一批 VPS 改动落回本机）
        // mutagen sync flush <sessionName>
        // flush 失败/超时必须保留会话（不 terminate），返回错误供调用者写入回执
        Exception? flushError=null;
        try
        {
            await RunMutagenCommandAsync(new[] { "sync", "flush", _sessionName }, CancellationToken.None);
        }
        catch(Exception ex)
        {
            flushError=ex;
        }
        
        if(flushError is not null)
        {
            // flush 失败：保留会话（不 terminate），返回错误（调用者记录到回执/状态）
            return $"工作区同步收敛失败（Mutagen flush 失败）：{flushError.Message}。会话 {_sessionName} 已保留，请手动检查并终止（mutagen sync terminate {_sessionName}）。";
        }
        
        // flush 成功：终止 Mutagen 会话
        // mutagen sync terminate <sessionName>
        try
        {
            await RunMutagenCommandAsync(new[] { "sync", "terminate", _sessionName }, CancellationToken.None);
        }
        catch { /* 忽略终止失败（会话已收敛，终止失败不影响数据） */ }
        
        return null;  // 成功
    }
    
    private static async Task<Process?> EnsureMutagenDaemonAsync(CancellationToken cancellation,bool agentNewlyStarted)
    {
        // 刚新启动 ssh-agent 时，已在运行的 daemon 没有 SSH_AUTH_SOCK，需重启继承（仅影响 mutagen 会话）。
        // 若 socket 本就存在（继承自父进程），daemon 通常已有，直接 start（已运行则 no-op），避免每次启动都断开既有同步。
        if(agentNewlyStarted)
        {
            try{await RunMutagenCommandAsync(new[]{"daemon","stop"},cancellation);}catch{/* 未运行 */}
        }
        try
        {
            await RunMutagenCommandAsync(new[] { "daemon", "start" }, cancellation);
            return null;  // daemon 在后台运行
        }
        catch
        {
            // daemon 已在运行
            return null;
        }
    }

    // 把远程密钥加载进 ssh-agent（共享助手 SshAgent，runner 也用同一逻辑），返回是否新启动了 agent。
    private static Task<bool> EnsureSshKeyInAgentAsync(string keyPath,CancellationToken cancellation)=>SshAgent.EnsureKeyInAgentAsync(keyPath,cancellation);
    
    private static async Task<string> RunMutagenCommandAsync(string[] args, CancellationToken cancellation)
    {
        return await RunCommandAsync("mutagen", args, cancellation);
    }
    
    private static Task<string> RunCommandAsync(string fileName, string[] args, CancellationToken cancellation)=>SshAgent.RunCommandAsync(fileName,args,cancellation);
}

// SSHFS 挂载会话
internal sealed class SshfsMountSession : IWorkspaceSyncSession
{
    private readonly string _mountPoint;
    private readonly string _remotePath;
    private readonly Process? _mountProcess;
    
    private SshfsMountSession(string mountPoint, string remotePath, Process? mountProcess)
    {
        _mountPoint = mountPoint;
        _remotePath = remotePath;
        _mountProcess = mountProcess;
    }
    
    public static async Task<SshfsMountSession> StartAsync(
        WorkspaceSyncSpec spec,
        RemoteExecutionSpec remote,
        CancellationToken cancellation)
    {
        var mountPoint = spec.SshfsMountPoint ?? FindAvailableDriveLetter();
        
        // Windows: sshfs-win user@host:/remote/path Z:
        // 需要 WinFsp + SSHFS-Win
        var startInfo = new ProcessStartInfo
        {
            FileName = "sshfs",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        startInfo.ArgumentList.Add($"{remote.SshUser}@{remote.Host}:{spec.RemotePath}");
        startInfo.ArgumentList.Add(mountPoint);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("reconnect,ServerAliveInterval=15,ServerAliveCountMax=3");
        
        if (remote.SshKeyPath is not null)
        {
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add($"IdentityFile={remote.SshKeyPath}");
        }
        
        var mountProcess = Process.Start(startInfo) ?? throw new IOException("无法启动 sshfs 进程");
        
        // 等待挂载完成（检查挂载点是否可访问）
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            
            if (Directory.Exists(mountPoint + "\\"))
            {
                return new SshfsMountSession(mountPoint, spec.RemotePath, mountProcess);
            }
            
            await Task.Delay(500, cancellation);
        }
        
        throw new TimeoutException($"SSHFS 挂载超时（30 秒）：{mountPoint}");
    }
    
    public string? SessionName => null;  // SSHFS 挂载无 Mutagen 会话名

    public Task WaitReadyAsync(CancellationToken cancellation)
    {
        // SSHFS 挂载在 StartAsync 中已等待
        return Task.CompletedTask;
    }
    
    public string GetRemoteWorkingDirectory() => _remotePath;
    
    public async ValueTask DisposeAsync()
    {
        var error = await TryDisposeAsync();
        if (error is not null)
        {
            Console.Error.WriteLine($"SSHFS 卸载失败：{error}");
        }
    }
    
    public async Task<string?> TryDisposeAsync()
    {
        // 卸载 SSHFS
        // Windows: umount Z:
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "umount",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(_mountPoint);
            
            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            return $"SSHFS 卸载失败：{ex.Message}";
        }
        
        // 终止挂载进程
        if (_mountProcess is not null && !_mountProcess.HasExited)
        {
            try
            {
                _mountProcess.Kill();
                await _mountProcess.WaitForExitAsync(CancellationToken.None);
            }
            catch { /* 忽略终止失败 */ }
        }
        
        return null;  // 成功
    }
    
    private static string FindAvailableDriveLetter()
    {
        // 查找可用的驱动器号（Z: 到 D:）
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            var drive = $"{letter}:";
            if (!Directory.Exists(drive + "\\"))
            {
                return drive;
            }
        }
        
        throw new IOException("没有可用的驱动器号（Z: 到 D: 都已占用）");
    }
}

// 无操作同步会话（不同步）
internal sealed class NoOpSyncSession : IWorkspaceSyncSession
{
    public string? SessionName => null;
    public Task WaitReadyAsync(CancellationToken cancellation) => Task.CompletedTask;
    public string GetRemoteWorkingDirectory() => throw new InvalidOperationException("同步模式为 None，无远程工作目录");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<string?> TryDisposeAsync() => Task.FromResult<string?>(null);  // 成功
}
