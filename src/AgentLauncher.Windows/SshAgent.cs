using System.Diagnostics;

namespace AgentLauncher.Windows;

// ssh-agent 共享助手：远程 runner（SshRunnerBackend）与工作区同步（Mutagen）共用同一套密钥加载逻辑。
// 免密原则：只走 密钥/ssh-agent/SSH config；ssh 侧由 BatchMode=yes 保证绝不提示输入密码。
public static class SshAgent
{
    private static readonly object PathGate=new();
    private static bool pathPrepared;

    // Explorer 启动的 PATH 中 System32\OpenSSH 优先：其 ssh-agent 依赖被禁用的 Windows 服务（错误 1058），
    // 且原生 ssh 用不了 MSYS agent 套接字。把 Git 的 usr/bin 置顶，ssh/ssh-add 与 mutagen 内部 ssh 同源。
    public static void PreferGitSshTools()
    {
        lock(PathGate)
        {
            if(pathPrepared)return;
            pathPrepared=true;
            var bin=GitSshBinDirectory();
            if(bin==null)return;
            var path=Environment.GetEnvironmentVariable("PATH")??"";
            if(path.Split(';').Contains(bin,StringComparer.OrdinalIgnoreCase))return;
            Environment.SetEnvironmentVariable("PATH",bin+";"+path);
        }
    }

    internal static string? GitSshBinDirectory()
    {
        var git=Commands.FindExe("git.exe");
        if(git==null)return null;
        var dir=Path.GetDirectoryName(git);
        for(var depth=0;depth<3&&dir!=null;depth++,dir=Path.GetDirectoryName(dir))
        {
            var candidate=Path.Combine(dir,"usr","bin");
            if(File.Exists(Path.Combine(candidate,"ssh-agent.exe")))return candidate;
        }
        return null;
    }

    // 把密钥加载进 ssh-agent，返回是否新启动了 agent（调用者据此决定既有 daemon 是否需重启继承 socket）。
    // 失败抛 IOException：同步侧 fail-closed 沿用原行为；runner 侧捕获后降级为警告（-i 仍让 ssh 直接尝试密钥）。
    public static async Task<bool> EnsureKeyInAgentAsync(string keyPath,CancellationToken cancellation)
    {
        PreferGitSshTools();
        var sock=Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        var newlyStarted=false;
        if(string.IsNullOrEmpty(sock))
        {
            var output=await RunCommandAsync("ssh-agent",["-s"],cancellation);
            var match=System.Text.RegularExpressions.Regex.Match(output,@"SSH_AUTH_SOCK=([^;]+);");
            if(!match.Success)throw new IOException("无法启动 ssh-agent 以加载远程密钥。");
            sock=match.Groups[1].Value;
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK",sock);
            newlyStarted=true;
        }
        await RunCommandAsync("ssh-add",[keyPath],cancellation);  // 幂等：已加载会提示 identity added
        return newlyStarted;
    }

    // 同步入口（Worker pane runner 在后台线程调用）；语义与异步版完全一致。
    public static bool EnsureKeyInAgent(string keyPath)=>EnsureKeyInAgentAsync(keyPath,CancellationToken.None).GetAwaiter().GetResult();

    // 结构化参数数组 + 重定向输出读取；非零退出码抛 IOException（携带 stderr）。
    public static async Task<string> RunCommandAsync(string fileName,string[] args,CancellationToken cancellation)
    {
        var startInfo=new ProcessStartInfo
        {
            FileName=fileName,
            UseShellExecute=false,
            RedirectStandardOutput=true,
            RedirectStandardError=true,
            CreateNoWindow=true
        };
        foreach(var arg in args)startInfo.ArgumentList.Add(arg);

        using var process=Process.Start(startInfo)??throw new IOException($"无法启动 {fileName} 进程");

        var stdoutTask=process.StandardOutput.ReadToEndAsync(cancellation);
        var stderrTask=process.StandardError.ReadToEndAsync(cancellation);

        try{await process.WaitForExitAsync(cancellation);}
        catch(OperationCanceledException)
        {
            try{process.Kill(entireProcessTree:true);}catch(InvalidOperationException){}
            throw;
        }

        var stdout=await stdoutTask;
        var stderr=await stderrTask;

        if(process.ExitCode!=0)throw new IOException($"{fileName} 失败（退出码 {process.ExitCode}）：{stderr}");

        return stdout;
    }
}
