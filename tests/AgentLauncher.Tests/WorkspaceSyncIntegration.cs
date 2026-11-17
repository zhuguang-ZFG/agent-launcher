using AgentLauncher.Core;
using AgentLauncher.Windows;

// 跳过异常（环境不具备时抛出）
internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}

// 工作区同步集成测试（R26-R32：真实 Mutagen 生命周期 + 实时双向同步；opt-in，不进 CI）
// 真实 VPS 凭据一律由操作者通过环境变量提供，缺失即失败关闭（不静默通过，也没有硬编码回退值）：
//   AGENT_LAUNCHER_SYNC_LIVE_HOST / AGENT_LAUNCHER_SYNC_LIVE_SSH_USER / AGENT_LAUNCHER_SYNC_LIVE_AGENT_USER /
//   AGENT_LAUNCHER_SYNC_LIVE_KEY_PATH（私钥路径，填 - 表示用 ssh-agent/默认凭据）/
//   AGENT_LAUNCHER_SYNC_LIVE_REMOTE_DIR_BASE（agent 用户可写的远端目录前缀，如 /home/<agentUser>/work）
// 要求：Mutagen 已安装；全新隔离 AGENT_LAUNCHER_TEST_ROOT（含 AgentLauncher-tests-）；不要设置 AGENT_LAUNCHER_FAKE_SAMPLE。
internal static class WorkspaceSyncIntegration
{
    public static async Task<int> RunAsync()
    {
        // 与 RemoteLiveChecks 相同的活测护栏：全新隔离根目录、真实资源读数、真实 ssh。
        var testRoot = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "";
        if (!testRoot.Contains("AgentLauncher-tests-") || Directory.Exists(testRoot))
            throw new IOException("请使用全新隔离的 AgentLauncher-tests- 测试目录（AGENT_LAUNCHER_TEST_ROOT）。");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") == "1")
            throw new IOException("真实同步验收必须使用真实资源读数；请取消设置 AGENT_LAUNCHER_FAKE_SAMPLE。");
        // Windows 进程搜索优先应用目录：发布输出里的集成测试假 ssh.exe 会顶替真实 ssh，制造假 PASS。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ssh.exe")))
            throw new IOException("运行目录存在假 ssh.exe，会顶替真实 SSH 客户端；请先移除后再做真实同步验收。");
        string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new IOException($"缺少真实 VPS 凭据：请设置环境变量 {name}。本测试需要 AGENT_LAUNCHER_SYNC_LIVE_HOST / AGENT_LAUNCHER_SYNC_LIVE_SSH_USER / AGENT_LAUNCHER_SYNC_LIVE_AGENT_USER / AGENT_LAUNCHER_SYNC_LIVE_KEY_PATH（- 表示 ssh-agent）/ AGENT_LAUNCHER_SYNC_LIVE_REMOTE_DIR_BASE；凭据缺失时失败关闭，不提供任何硬编码回退值。");
            return value.Trim();
        }
        var host = Required("AGENT_LAUNCHER_SYNC_LIVE_HOST");
        var sshUser = Required("AGENT_LAUNCHER_SYNC_LIVE_SSH_USER");
        var agentUser = Required("AGENT_LAUNCHER_SYNC_LIVE_AGENT_USER");
        var keyPath = Required("AGENT_LAUNCHER_SYNC_LIVE_KEY_PATH");
        var remoteDirBase = Required("AGENT_LAUNCHER_SYNC_LIVE_REMOTE_DIR_BASE").TrimEnd('/');
        var key = keyPath == "-" ? null : keyPath;
        if (key is not null && !File.Exists(key)) throw new IOException("找不到 SSH 私钥文件：" + key);
        Directory.CreateDirectory(testRoot);

        var evidence = new List<object>();
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); evidence.Add(new { name, passed = true }); }
            catch (SkipException e) { Console.WriteLine("SKIP " + name + ": " + e.Message); evidence.Add(new { name, skipped = true, reason = e.Message }); }
            catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); evidence.Add(new { name, passed = false, error = e.ToString() }); }
        }

        // VPS 配置全部来自操作者环境变量（见文件头）；远端目录用每次测试专用的临时目录（agent 用户可写）
        string VpsRemoteDir() => $"{remoteDirBase}/agent-launcher-test-{Guid.NewGuid():N}";
        string[] SshArgs(string command) => key is null
            ? ["-o", "BatchMode=yes", $"{sshUser}@{host}", command]
            : ["-i", key, "-o", "BatchMode=yes", $"{sshUser}@{host}", command];

        await Test("R26 Mutagen 同步启动并等待就绪", async () =>
        {
            var localDir = Path.Combine(Path.GetTempPath(), $"agent-launcher-test-{Guid.NewGuid():N}");
            var remoteDir = VpsRemoteDir();  // 每次测试专用的临时目录
            Directory.CreateDirectory(localDir);
            try
            {
                var spec = new WorkspaceSyncSpec(
                    Mode: WorkspaceSyncMode.Mutagen,
                    LocalPath: localDir,
                    RemotePath: remoteDir,
                    MutagenSessionName: $"test-r26-{Guid.NewGuid():N}",
                    SshfsMountPoint: null
                );
                var remote = new RemoteExecutionSpec(
                    Host: host,
                    SshPort: 22,
                    SshUser: sshUser,
                    AgentUser: agentUser,
                    AgentType: "pi",
                    RemoteWorkingDirectory: remoteDir,
                    Arguments: [],
                    MemoryLimitBytes: null,
                    SshKeyPath: key,
                    WorkspaceSync: spec
                );

                await using var session = await WorkspaceSync.StartAsync(spec, remote, CancellationToken.None);
                await session.WaitReadyAsync(CancellationToken.None);
                Assert(session.GetRemoteWorkingDirectory() == remoteDir, "Remote working directory should match");
            }
            finally
            {
                Directory.Delete(localDir, recursive: true);
                // 清理 VPS 上的测试目录
                await RunCommandAsync(
                    "ssh",
                    SshArgs($"rm -rf {remoteDir}"),
                    CancellationToken.None
                );
            }
        });

        await Test("R27 Mutagen 会话终止清理", async () =>
        {
            var localDir = Path.Combine(Path.GetTempPath(), $"agent-launcher-test-{Guid.NewGuid():N}");
            var remoteDir = VpsRemoteDir();  // 每次测试专用的临时目录
            Directory.CreateDirectory(localDir);
            try
            {
                var sessionName = $"test-r27-{Guid.NewGuid():N}";
                var spec = new WorkspaceSyncSpec(
                    Mode: WorkspaceSyncMode.Mutagen,
                    LocalPath: localDir,
                    RemotePath: remoteDir,
                    MutagenSessionName: sessionName,
                    SshfsMountPoint: null
                );
                var remote = new RemoteExecutionSpec(
                    Host: host,
                    SshPort: 22,
                    SshUser: sshUser,
                    AgentUser: agentUser,
                    AgentType: "pi",
                    RemoteWorkingDirectory: remoteDir,
                    Arguments: [],
                    MemoryLimitBytes: null,
                    SshKeyPath: key,
                    WorkspaceSync: spec
                );

                var session = await WorkspaceSync.StartAsync(spec, remote, CancellationToken.None);
                await session.WaitReadyAsync(CancellationToken.None);
                
                // 终止会话
                var error = await session.TryDisposeAsync();
                Assert(error is null, $"TryDisposeAsync should succeed, got: {error}");
                
                // 验证会话已终止（mutagen sync list 不包含 sessionName）
                var listOutput = await RunCommandAsync("mutagen", new[] { "sync", "list" }, CancellationToken.None);
                Assert(!listOutput.Contains(sessionName), $"Session {sessionName} should be terminated");
            }
            finally
            {
                Directory.Delete(localDir, recursive: true);
                // 清理 VPS 上的测试目录
                await RunCommandAsync(
                    "ssh",
                    SshArgs($"rm -rf {remoteDir}"),
                    CancellationToken.None
                );
            }
        });

        await Test("R28 SSHFS 挂载并等待就绪", async () =>
        {
            // SSHFS 在 Windows 上需要 WinFsp/SSHFS-Win（暂不可用）
            // 验证：StartAsync 抛出 IOException（SSHFS 不可用）
            // 检查 SSHFS 是否可用
            const string SshfsPath = @"C:\Program Files\SSHFS-Win\bin\sshfs.exe";
            if (!File.Exists(SshfsPath))
            {
                throw new SkipException($"SSHFS not found at {SshfsPath} (WinFsp/SSHFS-Win not installed)");
            }
            
            var remoteDir = VpsRemoteDir();  // 每次测试专用的临时目录
            var spec = new WorkspaceSyncSpec(
                Mode: WorkspaceSyncMode.SshfsMount,
                LocalPath: @"D:\test\local",
                RemotePath: remoteDir,
                MutagenSessionName: null,
                SshfsMountPoint: "Z:"
            );
            var remote = new RemoteExecutionSpec(
                Host: host,
                SshPort: 22,
                SshUser: sshUser,
                AgentUser: agentUser,
                AgentType: "pi",
                RemoteWorkingDirectory: remoteDir,
                Arguments: [],
                MemoryLimitBytes: null,
                SshKeyPath: key,
                WorkspaceSync: spec
            );

            await using var session = await WorkspaceSync.StartAsync(spec, remote, CancellationToken.None);
            await session.WaitReadyAsync(CancellationToken.None);
            Assert(session.GetRemoteWorkingDirectory().StartsWith("Z:"), "SSHFS mount point should be Z:");
        });

        await Test("R29 SSHFS 卸载清理", async () =>
        {
            // SSHFS 在 Windows 上需要 WinFsp/SSHFS-Win（暂不可用）
            const string SshfsPath = @"C:\Program Files\SSHFS-Win\bin\sshfs.exe";
            if (!File.Exists(SshfsPath))
            {
                throw new SkipException($"SSHFS not found at {SshfsPath} (WinFsp/SSHFS-Win not installed)");
            }
            
            var remoteDir = VpsRemoteDir();  // 每次测试专用的临时目录
            var spec = new WorkspaceSyncSpec(
                Mode: WorkspaceSyncMode.SshfsMount,
                LocalPath: @"D:\test\local",
                RemotePath: remoteDir,
                MutagenSessionName: null,
                SshfsMountPoint: "Z:"
            );
            var remote = new RemoteExecutionSpec(
                Host: host,
                SshPort: 22,
                SshUser: sshUser,
                AgentUser: agentUser,
                AgentType: "pi",
                RemoteWorkingDirectory: remoteDir,
                Arguments: [],
                MemoryLimitBytes: null,
                SshKeyPath: key,
                WorkspaceSync: spec
            );

            var session = await WorkspaceSync.StartAsync(spec, remote, CancellationToken.None);
            await session.WaitReadyAsync(CancellationToken.None);
            
            // 卸载
            var error = await session.TryDisposeAsync();
            Assert(error is null, $"TryDisposeAsync should succeed, got: {error}");
            
            // 验证已卸载（Z: 不存在）
            Assert(!Directory.Exists("Z:\\"), "Z: should be unmounted");
        });

        await Test("R30 本地编辑实时同步到 VPS（Mutagen）", async () =>
        {
            // 用真实 SSH 路径（绕过 fake SSH）
            const string RealSshPath = @"C:\Windows\System32\OpenSSH\ssh.exe";
            if (!File.Exists(RealSshPath))
            {
                throw new SkipException($"Real SSH not found at {RealSshPath} (fake SSH in PATH would intercept)");
            }
            
            var localDir = Path.Combine(Path.GetTempPath(), $"agent-launcher-test-{Guid.NewGuid():N}");
            var remoteDir = VpsRemoteDir();  // 每次测试专用的临时目录
            Directory.CreateDirectory(localDir);
            try
            {
                var spec = new WorkspaceSyncSpec(
                    Mode: WorkspaceSyncMode.Mutagen,
                    LocalPath: localDir,
                    RemotePath: remoteDir,
                    MutagenSessionName: $"test-r30-{Guid.NewGuid():N}",
                    SshfsMountPoint: null
                );
                var remote = new RemoteExecutionSpec(
                    Host: host,
                    SshPort: 22,
                    SshUser: sshUser,
                    AgentUser: agentUser,
                    AgentType: "pi",
                    RemoteWorkingDirectory: remoteDir,
                    Arguments: [],
                    MemoryLimitBytes: null,
                    SshKeyPath: key,
                    WorkspaceSync: spec
                );

                await using var session = await WorkspaceSync.StartAsync(spec, remote, CancellationToken.None);
                await session.WaitReadyAsync(CancellationToken.None);

                // 本地编辑
                var testFile = Path.Combine(localDir, "local-edit.txt");
                await File.WriteAllTextAsync(testFile, "local edit");

                // 等待同步（最多 10 秒）
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var synced = false;
                var lastContent = "";
                while (DateTime.UtcNow < deadline)
                {
                    var remoteContent = await RunCommandAsync(
                        RealSshPath,  // 用真实 SSH 路径
                        SshArgs($"cat {remoteDir}/local-edit.txt 2>/dev/null || echo ''"),
                        CancellationToken.None
                    );
                    lastContent = remoteContent.Trim();
                    if (lastContent == "local edit")
                    {
                        synced = true;
                        break;
                    }
                    await Task.Delay(500);
                }
                Assert(synced, $"Local edit should sync to VPS within 10 seconds (remoteDir: {remoteDir}, lastContent: '{lastContent}')");
            }
            finally
            {
                Directory.Delete(localDir, recursive: true);
                // 清理 VPS 上的测试目录
                await RunCommandAsync(
                    RealSshPath,  // 用真实 SSH 路径
                    SshArgs($"rm -rf {remoteDir}"),
                    CancellationToken.None
                );
            }
        });

        await Test("R31 VPS 编辑实时同步到本机（Mutagen）", async () =>
        {
            // 用真实 SSH 路径（绕过 fake SSH）
            const string RealSshPath = @"C:\Windows\System32\OpenSSH\ssh.exe";
            if (!File.Exists(RealSshPath))
            {
                throw new SkipException($"Real SSH not found at {RealSshPath} (fake SSH in PATH would intercept)");
            }
            
            var localDir = Path.Combine(Path.GetTempPath(), $"agent-launcher-test-{Guid.NewGuid():N}");
            var remoteDir = VpsRemoteDir();  // 每次测试专用的临时目录
            Directory.CreateDirectory(localDir);
            try
            {
                var spec = new WorkspaceSyncSpec(
                    Mode: WorkspaceSyncMode.Mutagen,
                    LocalPath: localDir,
                    RemotePath: remoteDir,
                    MutagenSessionName: $"test-r31-{Guid.NewGuid():N}",
                    SshfsMountPoint: null
                );
                var remote = new RemoteExecutionSpec(
                    Host: host,
                    SshPort: 22,
                    SshUser: sshUser,
                    AgentUser: agentUser,
                    AgentType: "pi",
                    RemoteWorkingDirectory: remoteDir,
                    Arguments: [],
                    MemoryLimitBytes: null,
                    SshKeyPath: key,
                    WorkspaceSync: spec
                );

                await using var session = await WorkspaceSync.StartAsync(spec, remote, CancellationToken.None);
                await session.WaitReadyAsync(CancellationToken.None);

                // VPS 编辑
                await RunCommandAsync(
                    RealSshPath,  // 用真实 SSH 路径
                    SshArgs($"echo 'remote edit' > {remoteDir}/remote-edit.txt"),
                    CancellationToken.None
                );

                // 等待同步（最多 10 秒）
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var synced = false;
                var testFile = Path.Combine(localDir, "remote-edit.txt");
                var lastContent = "";
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(testFile))
                    {
                        var content = await File.ReadAllTextAsync(testFile);
                        lastContent = content.Trim();
                        if (lastContent == "remote edit")
                        {
                            synced = true;
                            break;
                        }
                    }
                    await Task.Delay(500);
                }
                Assert(synced, $"VPS edit should sync to local within 10 seconds (remoteDir: {remoteDir}, testFile: {testFile}, lastContent: '{lastContent}')");
            }
            finally
            {
                Directory.Delete(localDir, recursive: true);
                // 清理 VPS 上的测试目录
                await RunCommandAsync(
                    RealSshPath,  // 用真实 SSH 路径
                    SshArgs($"rm -rf {remoteDir}"),
                    CancellationToken.None
                );
            }
        });

        await Test("R32 agent 用户可写验证（su - agent）", async () =>
        {
            // 用真实 SSH 路径（绕过 fake SSH）
            const string RealSshPath = @"C:\Windows\System32\OpenSSH\ssh.exe";
            if (!File.Exists(RealSshPath))
            {
                throw new SkipException($"Real SSH not found at {RealSshPath} (fake SSH in PATH would intercept)");
            }
            
            var localDir = Path.Combine(Path.GetTempPath(), $"agent-launcher-test-{Guid.NewGuid():N}");
            var remoteDir = VpsRemoteDir();  // 每次测试专用的临时目录
            Directory.CreateDirectory(localDir);
            try
            {
                var spec = new WorkspaceSyncSpec(
                    Mode: WorkspaceSyncMode.Mutagen,
                    LocalPath: localDir,
                    RemotePath: remoteDir,
                    MutagenSessionName: $"test-r32-{Guid.NewGuid():N}",
                    SshfsMountPoint: null
                );
                var remote = new RemoteExecutionSpec(
                    Host: host,
                    SshPort: 22,
                    SshUser: sshUser,
                    AgentUser: agentUser,
                    AgentType: "pi",
                    RemoteWorkingDirectory: remoteDir,
                    Arguments: [],
                    MemoryLimitBytes: null,
                    SshKeyPath: key,
                    WorkspaceSync: spec
                );

                await using var session = await WorkspaceSync.StartAsync(spec, remote, CancellationToken.None);
                await session.WaitReadyAsync(CancellationToken.None);

                // 用 agent 用户创建文件（su - agent）
                await RunCommandAsync(
                    RealSshPath,
                    SshArgs($"su - {agentUser} -c 'echo agent edit > {remoteDir}/agent-edit.txt'"),
                    CancellationToken.None
                );

                // 等待同步（最多 10 秒）
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var synced = false;
                var testFile = Path.Combine(localDir, "agent-edit.txt");
                var lastContent = "";
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(testFile))
                    {
                        var content = await File.ReadAllTextAsync(testFile);
                        lastContent = content.Trim();
                        if (lastContent == "agent edit")
                        {
                            synced = true;
                            break;
                        }
                    }
                    await Task.Delay(500);
                }
                Assert(synced, $"Agent edit should sync to local within 10 seconds (remoteDir: {remoteDir}, testFile: {testFile}, lastContent: '{lastContent}')");
            }
            finally
            {
                Directory.Delete(localDir, recursive: true);
                // 清理 VPS 上的测试目录
                await RunCommandAsync(
                    RealSshPath,
                    SshArgs($"rm -rf {remoteDir}"),
                    CancellationToken.None
                );
            }
        });

        // skipped 不算失败（环境不具备）
        return evidence.Any(e => e.GetType().GetProperty("passed")?.GetValue(e) is false) ? 1 : 0;
    }

    private static async Task<string> RunCommandAsync(string fileName, string[] args, CancellationToken cancellation)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo) ?? throw new IOException($"无法启动 {fileName} 进程");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new IOException($"{fileName} 失败（退出码 {process.ExitCode}）：{stderr}");
        }

        return stdout;
    }
}
