using System.Text.RegularExpressions;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public static class TaskDelivery
{
    public static string Prompt(TaskCard task)
    {
        var copy=task.Copy();copy.Project="当前启动工作目录（若为独立目录，请在该目录内完成修改）";
        return TaskUsability.Brief(copy);
    }

    public static async Task<AgentProfile> PrepareAsync(TaskCard task,CancellationToken ct=default)
    {
        var profile=task.Profile.Copy();
        if(task.DeliveryMode=="manual")return profile;
        if(task.AgentId is not ("codex" or "claude" or "omp"))
            throw new IOException("该 Agent 暂无任务交付适配，请选择手动交付或支持的 Agent。");
        if(profile.Arguments.Any(arg=>arg.Split('=')[0] is "resume" or "exec" or "review" or "--resume" or "--continue" or "-c" or "-r" or "--print" or "--" or "--session" or "--session-id" or "--fork" or "--restore" or "--goal" or "--mode" or "--export" or "--help" or "-h" or "--version" || (task.AgentId!="codex"&&arg=="-p")))
            throw new IOException("任务参数已指定执行模式或续接方式，请先移除这些参数，再使用自动交付。");
        var command=Commands.ProbeCommand(task.AgentId,profile);
        var fingerprint=AgentProbe.Fingerprint(command);
        var bare=OmpMemoryGuard.Detach(command);
        var probe=await AgentProbe.RunAsync(bare with{Arguments=[..bare.Arguments,"--help"]},task.Project,TimeSpan.FromSeconds(8),ct:ct);
        RequireHelp(probe);
        var help=probe.Output+"\n"+probe.Error;
        if(!Regex.IsMatch(help,@"\[(?:PROMPT|MESSAGE|MESSAGES|prompt|message|messages|query)\b|(?m)^\s*MESSAGES\s+Messages to send",RegexOptions.CultureInvariant))
            throw new IOException("当前程序帮助未确认初始任务参数，未发送任务说明。请改为手动交付并核对该程序。");
        if(task.DeliveryMode=="automatic")
        {
            if(task.AgentId=="codex")
            {
                var execHelp=await AgentProbe.RunAsync(bare with{Arguments=[..bare.Arguments,"exec","--help"]},task.Project,TimeSpan.FromSeconds(8),ct:ct);
                RequireHelp(execHelp);
                if(!(execHelp.Output+execHelp.Error).Contains("[PROMPT]",StringComparison.Ordinal))throw new IOException("当前 Codex 未确认 exec 任务参数。");
                var programArguments=Commands.ProfileProgramArgumentCount(profile);
                profile.Arguments=[..profile.Arguments.Take(programArguments),"exec",..profile.Arguments.Skip(programArguments)];
            }
            else
            {
                if(!Regex.IsMatch(help,@"(?m)^\s*(?:-p\s*,?\s*)?--print\b|(?m)^\s*-p,?\s+.*(?:print|non-interactive)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
                    throw new IOException("当前 Agent 帮助未确认非交互执行方式，请选择交互交付或手动交付。");
                profile.Arguments=[..profile.Arguments,"--print"];
            }
        }
        if(fingerprint!=AgentProbe.Fingerprint(command))throw new IOException("检查期间程序已变化，请重新入队。");
        profile.Arguments=[..profile.Arguments,"--",Prompt(task)];
        var resolved=Commands.Resolve(task.AgentId,profile);
        if(Commands.QuotedLength(resolved.Executable)+resolved.Arguments.Sum(arg=>1L+Commands.QuotedLength(arg))>ResourcePolicy.CommandLineLimit)
            throw new IOException("任务说明和参数超过 Windows 命令行上限，请缩短说明，或改用手动交付。");
        return profile;
    }

    private static void RequireHelp(ProbeResult result)
    {if(result.ExitCode!=0||result.TimedOut||result.Truncated)throw new IOException("程序帮助检查失败、超时或输出不完整，未发送任务说明。");}
}
