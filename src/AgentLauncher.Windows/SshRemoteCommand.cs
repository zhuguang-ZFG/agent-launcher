using System.Diagnostics;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

// 非交互远程 SSH 命令：免密自检 / 检查版本 / 升级 Agent 等短生命周期维护操作。
// 与 SshRunnerBackend 的交互式会话（-tt + 继承控制台）不同：不带 -tt，重定向并读取 stdout/stderr，
// 有界超时（超时杀进程树，绝不挂死），BatchMode=yes 保证绝不提示输入密码。
public sealed record SshCommandResult(int ExitCode,string StdOut,string StdErr,bool TimedOut);

public static class SshRemoteCommand
{
    // 单层 shell quoting（与 SshRunnerBackend 完全一致）：只转义单引号，不嵌套双引号。
    public static string QuoteArg(string arg)=>$"'{arg.Replace("'","'\\''")}'";

    // su - <agentUser> -c "<命令>"：双引号包裹并转义内部双引号/$/反引号（与 runner 的远程命令包装一致）。
    public static string WrapSu(string agentUser,string remoteCmd)=>$"su - {agentUser} -c \"{remoteCmd.Replace("\"","\\\"").Replace("$","\\$").Replace("`","\\`")}\"";

    // 构造非交互 ssh 参数（ArgumentList，避免引号嵌套）：-p/-i 按需，BatchMode + ConnectTimeout 兜底。
    // SshKeyPath 为空时依赖 SSH config IdentityFile / ssh-agent / 默认密钥（BatchMode 下 ssh 自动尝试）。
    public static ProcessStartInfo CreateStartInfo(string host,int port,string user,string? keyPath,string remoteCommand)
    {
        SshAgent.PreferGitSshTools();
        var psi=new ProcessStartInfo
        {
            FileName="ssh",
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardInput=true,
            RedirectStandardOutput=true,
            RedirectStandardError=true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString());
        if(!string.IsNullOrEmpty(keyPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(keyPath);
        }
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add($"{user}@{host}");
        psi.ArgumentList.Add(remoteCommand);
        return psi;
    }

    // 运行短生命周期 SSH 命令并捕获输出/退出码；超时或被取消时杀掉本地 ssh 进程树并返回 TimedOut。
    public static async Task<SshCommandResult> RunAsync(ProcessStartInfo startInfo,TimeSpan timeout,CancellationToken cancellation=default)
    {
        using var process=Process.Start(startInfo)??throw new IOException("无法启动 ssh 进程；请确认已安装 OpenSSH 客户端（ssh 在 PATH 中）。");
        try{process.StandardInput.Close();}catch{/* 确定性 EOF：远端命令不会等待 stdin */}
        using var timeoutCts=new CancellationTokenSource(timeout);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,timeoutCts.Token);
        var stdoutTask=process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask=process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch(OperationCanceledException)
        {
            try{process.Kill(entireProcessTree:true);}catch{/* 进程可能已退出 */}
            return new SshCommandResult(-1,await ReadSafeAsync(stdoutTask),await ReadSafeAsync(stderrTask),true);
        }
        return new SshCommandResult(process.ExitCode,await stdoutTask,await stderrTask,false);
    }

    private static async Task<string> ReadSafeAsync(Task<string> task)
    {
        try{return await task;}catch(Exception e)when(e is not OutOfMemoryException){return "";}
    }

    // 免密连接自检：ssh -o BatchMode=yes -o ConnectTimeout=10 [-i key] [-p port] user@host whoami。
    // 成功返回「免密连接正常」；失败返回原因（认证/网络/主机密钥），不存在任何密码提示路径。
    public static async Task<(bool Ok,string Message)> TestConnectionAsync(RemoteHost host,CancellationToken cancellation=default)
    {
        var keyWarning=await PrepareKeyAsync(host,cancellation);
        var result=await RunAsync(CreateStartInfo(host.Address,host.SshPort,host.SshUser,host.SshKeyPath,"whoami"),TimeSpan.FromSeconds(25),cancellation);
        if(result.TimedOut)return(false,AppendWarning("免密连接失败：超时（25 秒）。请检查地址/端口/网络与远程 SSH 服务。",keyWarning));
        if(result.ExitCode==0)return(true,$"免密连接正常（远程用户：{result.StdOut.Trim()}）。");
        return(false,AppendWarning($"免密连接失败（退出码 {result.ExitCode}）：{Reason(result)}\n本工具只使用 密钥/ssh-agent/SSH config 免密登录，不会提示输入密码；请核对私钥、ssh-agent 或远程 ~/.ssh/config 与 authorized_keys。",keyWarning));
    }

    // 检查版本：在远程工作目录以 agent 用户执行 <agent> --version（60 秒上限）。
    public static Task<SshCommandResult> CheckVersionAsync(RemoteHost host,string agentType,string remoteDirectory,CancellationToken cancellation=default)=>
        RunMaintenanceAsync(host,agentType,remoteDirectory,RemoteAgentMaintenance.VersionCommandFor(agentType),"版本检查",TimeSpan.FromSeconds(60),cancellation);

    // 升级 Agent：执行该 agent 的升级命令（command 显式指定时优先，其次主机覆盖，10 分钟上限）。
    public static Task<SshCommandResult> UpgradeAgentAsync(RemoteHost host,string agentType,string remoteDirectory,CancellationToken cancellation=default,string? command=null)=>
        RunMaintenanceAsync(host,agentType,remoteDirectory,command??RemoteAgentMaintenance.UpgradeCommandFor(host,agentType),"升级",TimeSpan.FromMinutes(10),cancellation);

    // B2 远端资源采样：单条 ssh 取 free -b / nproc / loadavg（15 秒有界）。
    // 以 SSH 用户执行即可（资源是主机整体指标，无需 su）；超时/非零退出/解析失败一律返回 null（fail-closed）。
    public static async Task<RemoteResourceSample?> SampleResourcesAsync(RemoteHost host,CancellationToken cancellation=default)
    {
        if(await PrepareKeyAsync(host,cancellation) is not null)return null;
        var result=await RunAsync(CreateStartInfo(host.Address,host.SshPort,host.SshUser,host.SshKeyPath,"free -b && nproc && cat /proc/loadavg"),TimeSpan.FromSeconds(15),cancellation);
        if(result.TimedOut||result.ExitCode!=0)return null;
        return RemoteAgentMaintenance.ParseResourceSample(result.StdOut);
    }

    // C1 续接前帮助核对：远程 <agent> --help（60 秒上限）。
    public static Task<SshCommandResult> CheckHelpAsync(RemoteHost host,string agentType,string remoteDirectory,CancellationToken cancellation=default)=>
        RunMaintenanceAsync(host,agentType,remoteDirectory,RemoteAgentMaintenance.HelpCommandFor(agentType),"帮助核对",TimeSpan.FromSeconds(60),cancellation);

    // C1 fail-closed 核对：help 读取失败或输出不含全部续接 flag（单词边界）都拒绝续接。
    public static async Task<(bool Ok,string Message)> VerifyResumeFlagsAsync(RemoteHost host,string agentType,string remoteDirectory,string[] flags,CancellationToken cancellation=default)
    {
        var result=await CheckHelpAsync(host,agentType,remoteDirectory,cancellation);
        if(result.TimedOut)return(false,"远程 --help 核对超时，本地 SSH 连接已终止；无法确认续接参数，已拒绝续接。");
        if(result.ExitCode!=0)return(false,$"无法读取远程 {agentType} --help（退出码 {result.ExitCode}）；无法确认续接参数，已拒绝续接。");
        var help=result.StdOut+"\n"+result.StdErr;
        foreach(var flag in flags)if(!RemoteAgentMaintenance.HelpContainsFlag(help,flag))return(false,$"远程 {agentType} --help 输出未包含 {flag}（按单词边界核对）；已拒绝续接，请核对远程 Agent 版本。");
        return(true,"");
    }

    private static async Task<SshCommandResult> RunMaintenanceAsync(RemoteHost host,string agentType,string remoteDirectory,string? command,string action,TimeSpan timeout,CancellationToken cancellation)
    {
        if(command is null)throw new InvalidDataException($"Agent「{agentType}」没有可用的{action}命令；请在远程主机配置中填写升级命令覆盖。");
        if(!RemoteWorkspaceMappingHelper.IsValidRemoteDirectory(remoteDirectory)||remoteDirectory.Any(char.IsControl))throw new InvalidDataException("远程工作目录无效：必须是绝对路径或 ~/ 开头，且不含控制字符。");
        // fail-closed：密钥加载失败直接报错（不挂起、不提示密码）。
        if(await PrepareKeyAsync(host,cancellation) is {} warning)throw new IOException(warning);
        var remoteCmd=$"cd {QuoteArg(remoteDirectory)} && {command}";
        // SSH 用户即 agent 用户时无需 su；否则与 runner 相同以 su - 切换到 agent 身份。
        var wrapped=string.Equals(host.SshUser,host.AgentUser,StringComparison.Ordinal)?remoteCmd:WrapSu(host.AgentUser,remoteCmd);
        return await RunAsync(CreateStartInfo(host.Address,host.SshPort,host.SshUser,host.SshKeyPath,wrapped),timeout,cancellation);
    }

    // 自检用：密钥加载失败不阻断（-i 仍可能直接成功），把警告并入结果；维护命令则 fail-closed。
    private static async Task<string?> PrepareKeyAsync(RemoteHost host,CancellationToken cancellation)
    {
        if(string.IsNullOrEmpty(host.SshKeyPath))return null;
        try{await SshAgent.EnsureKeyInAgentAsync(host.SshKeyPath,cancellation);return null;}
        catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception){return $"SSH 密钥加载进 ssh-agent 失败：{e.Message}";}
    }

    private static string AppendWarning(string message,string? warning)=>warning is null?message:message+"\n"+warning;

    private static string Reason(SshCommandResult result)
    {
        var text=(result.StdErr.Trim().Length>0?result.StdErr:result.StdOut).Trim();
        return text.Length>800?text[..800]+"…":text;
    }
}