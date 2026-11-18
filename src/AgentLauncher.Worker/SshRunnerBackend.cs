using System.Diagnostics;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Worker;

// SSH 远程 runner 后端：通过 ssh -tt 在远程主机上启动 agent
// 明确：远程进程不受本地 Windows Job 限制，资源管理由远程主机负责
// 状态映射：SSH 连接建立 = Started，断开 = Exited，网络错误 = Disconnected
public sealed class SshRunnerBackend : IDisposable
{
    public required RemoteExecutionSpec Spec { get; init; }
    public required ProcessStartInfo StartInfo { get; init; }  // 未启动的 ProcessStartInfo
    public IWorkspaceSyncSession? SyncSession { get; set; }  // 工作区同步会话（如果启用，Dispose 时清理）

    private Process? _process;  // 本地 SSH 客户端进程（Start 后才有值）
    private bool _disposed;
    private bool _started;
    private long _creationTime;  // Start 后才有值

    public Process Process => _process ?? throw new InvalidOperationException("Process not started yet");
    public long CreationTime => _started ? _creationTime : throw new InvalidOperationException("Process not started yet");

    // 创建 SSH 远程会话配置（不启动进程，只构造 ProcessStartInfo）
    // 与 Worker 的 resume gate 对齐：Create 只构造，Start 才真正启动
    // stdio 一律继承 worker/窗格控制台：重定向而无人读取会丢输出，远端写满管道缓冲还会挂死 agent。
    public static SshRunnerBackend Create(RemoteExecutionSpec spec, Guid? attemptId = null)
    {
        // 构建远程命令：su - agent -c 'cd <workdir> && <agent> <args>'
        var agentCmd = spec.AgentType switch
        {
            "pi" => $"pi {string.Join(" ", spec.Arguments.Select(EscapeShellArg))}",
            "kimi" => $"kimi {string.Join(" ", spec.Arguments.Select(EscapeShellArg))}",
            "qodercli" => $"qodercli {string.Join(" ", spec.Arguments.Select(EscapeShellArg))}",
            "omp" => $"omp {string.Join(" ", spec.Arguments.Select(EscapeShellArg))}",
            "codex" => $"codex {string.Join(" ", spec.Arguments.Select(EscapeShellArg))}",
            _ => throw new ArgumentException($"Unknown agent type: {spec.AgentType}")
        };

        // V8 默认堆上限随物理内存增长（4 GiB 机器约 2 GiB），小 VPS 会被 agent 吃满触发 OOM。
        // 注入 Node 堆上限（非 Node 进程忽略 NODE_OPTIONS）；1024 MiB 适配 2–4 GiB 级 VPS。
        // trap '' HUP 让远程前台在本地 SSH 断开后仍可留存（SIG_IGN 会随 exec 继承）。断开仍不能当作进程已退出的证明。
        var attemptPrefix = attemptId is Guid attempt ? $"export {RemoteReattach.AttemptToken(attempt)}; " : "";
        var remoteCmd = $"{attemptPrefix}trap '' HUP; export NODE_OPTIONS=--max-old-space-size=1024; cd {EscapeShellArg(spec.RemoteWorkingDirectory)} && {agentCmd}";
        return new SshRunnerBackend { Spec = spec, StartInfo = CreateStartInfo(spec, remoteCmd) };
    }

    // 重新挂接：同一主机、同一远程目录的新本地 SSH。远程命令只检查本次尝试是否仍在，然后进入该目录，不重新启动 Agent。
    public static SshRunnerBackend CreateAttachment(RemoteExecutionSpec spec, Guid attemptId)
    {
        var remoteCmd = RemoteReattach.AttachShell(attemptId, spec.RemoteWorkingDirectory);
        return new SshRunnerBackend { Spec = spec, StartInfo = CreateStartInfo(spec, remoteCmd) };
    }

    private static ProcessStartInfo CreateStartInfo(RemoteExecutionSpec spec, string remoteCmd)
    {
        // 用 ArgumentList 而不是字符串拼接，避免引号嵌套问题
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // SSH 参数：-tt（强制 PTY）-p port -i key -o options user@host remote-command
        // 免密连接：BatchMode=yes 禁止任何密码/交互提示；SshKeyPath 为空时依赖 SSH config IdentityFile /
        // ssh-agent / 默认密钥（ssh 在 BatchMode 下自动尝试）。配置了密钥时 Start() 还会把它加载进 ssh-agent。
        // stdin 必须用独立管道而不是继承：若继承到控制台句柄，ssh -tt 会把本地控制台切到 raw 模式，
        // 窗格/重定向输出会被吞掉（实测：继承控制台 stdin 时远端输出全部丢失，管道 stdin 则正常）。
        psi.RedirectStandardInput = true;
        psi.ArgumentList.Add("-tt");  // 强制 PTY（交互式）
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(spec.SshPort.ToString());
        if (!string.IsNullOrEmpty(spec.SshKeyPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(spec.SshKeyPath);
        }
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add($"{spec.SshUser}@{spec.Host}");
        // 远程命令：su - agent -c "<remote-command>"（双引号，因为内部已有单引号 quoting；共享 SshRemoteCommand 的包装逻辑）
        psi.ArgumentList.Add(SshRemoteCommand.WrapSu(spec.AgentUser, remoteCmd));
        return psi;
    }

    // 启动远程 agent（resume 成功后调用）
    // 与 ProtectedProcess.Resume 对齐：Create 只构造，Start 才真正启动
    public void Start(bool keepStandardInput = false)
    {
        if (_started) throw new InvalidOperationException("Already started");
        // 免密保障：配置了自定义密钥时先加载进 ssh-agent（与工作区同步共用 SshAgent 助手），
        // 使 ssh 除 -i 外还能经 agent 认证。加载失败仅警告不阻断：-i 仍让 ssh 直接尝试密钥，
        // BatchMode=yes 保证绝不提示密码；认证失败会由 ssh 以非零退出码 fail-closed。
        if (!string.IsNullOrEmpty(Spec.SshKeyPath))
        {
            try { SshAgent.EnsureKeyInAgent(Spec.SshKeyPath); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
            { Console.Error.WriteLine($"警告：SSH 密钥加载进 ssh-agent 失败（{ex.Message}）；将仅用 -i 直接尝试免密连接。"); }
        }
        _process = Process.Start(StartInfo) ?? throw new InvalidOperationException("Failed to start SSH");
        if (!keepStandardInput)
        {
            // 远程 agent 不需要本地 stdin；立即关闭管道给 ssh 一个确定的 EOF，
            // 避免它等待一个永远不会来的输入（与手工 `ssh ... <&-` 的已验证行为一致）。
            try { _process.StandardInput.Close(); } catch { }
        }
        else PumpConsoleInput(_process);
        _started = true;
        // 从 Process.StartTime 计算 CreationTime（与 ProtectedProcess.CreationTime 相同的 long 格式）
        _creationTime = _process.StartTime.ToFileTimeUtc();
    }
    
    // 启动远程 agent 并启动工作区同步（如果启用）
    public async Task StartWithSyncAsync(CancellationToken cancellation = default)
    {
        if (_started) throw new InvalidOperationException("Already started");
        
        // 启动工作区同步（如果启用）
        if (Spec.WorkspaceSync is not null && Spec.WorkspaceSync.Mode != WorkspaceSyncMode.None)
        {
            SyncSession = await WorkspaceSync.StartAsync(Spec.WorkspaceSync, Spec, cancellation);
            await SyncSession.WaitReadyAsync(cancellation);
            
            // 不需要 root 递归修权（Mutagen 已切到 AgentUser，agent 创建的文件/目录是 agent:agent）
        }
        
        // 启动 SSH 进程
        Start();
    }

    // 等待远程 agent 退出（监控本地 SSH 客户端进程）
    public bool WaitForExit(int milliseconds)
    {
        if (!_started) throw new InvalidOperationException("Not started");
        return Process.WaitForExit(milliseconds);
    }

    // 获取退出码
    // SSH 退出码：0 = 正常退出，255 = SSH 连接错误，其他 = agent 退出码（通过远程 shell 传递）
    public int ExitCode
    {
        get
        {
            if (!_started) throw new InvalidOperationException("Not started");
            if (!Process.HasExited) throw new InvalidOperationException("Process has not exited");
            return Process.ExitCode;
        }
    }

    // 检查是否已退出
    public bool HasExited => _started && Process.HasExited;

    // 检查是否是 SSH 连接错误（退出码 255）
    public bool IsSshConnectionError => _started && Process.HasExited && Process.ExitCode == 255;

    // 中止远程 agent（杀死本地 SSH 客户端，远程进程随会话断开而终止）
    public void Abort()
    {
        if (!_started) return;
        try
        {
            if (!Process.HasExited)
                Process.Kill();
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_started)
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                    Process.WaitForExit(5000);
                }
            }
            catch { }
            finally
            {
                Process.Dispose();
            }
        }
        
        // 清理工作区同步会话
        if (SyncSession is not null)
        {
            try
            {
                var task = SyncSession.TryDisposeAsync();
                if (task.Wait(5000))
                {
                    var error = task.Result;
                    if (error is not null)
                    {
                        // flush 失败：记录到 stderr（调用者应该捕获并写入回执）
                        Console.Error.WriteLine($"工作区同步清理失败：{error}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("工作区同步清理超时（5 秒）");
                }
            }
            catch (Exception ex)
            {
                // TryDisposeAsync 不应该抛出异常（返回错误），但如果抛出，记录
                Console.Error.WriteLine($"工作区同步清理异常：{ex.Message}");
            }
        }
    }

    // 重新挂接的 shell 需要键盘。stdout 仍继承窗格；stdin 保持管道，由本进程把控制台字节写进去，避免 -tt 直接抢占控制台。
    private static void PumpConsoleInput(Process process)
    {
        var writer = process.StandardInput;
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = Console.OpenStandardInput();
                var buffer = new byte[256];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    writer.BaseStream.Write(buffer, 0, read);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            try { writer.Close(); } catch { }
        });
    }

    // 单层 shell quoting：只转义单引号，不嵌套双引号（共享助手，维护命令用同一实现）
    private static string EscapeShellArg(string arg) => SshRemoteCommand.QuoteArg(arg);
}
