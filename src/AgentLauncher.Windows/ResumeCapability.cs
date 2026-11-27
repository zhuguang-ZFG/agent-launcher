using System.Text.RegularExpressions;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public static class ResumeCapability
{
    public static async Task<ArgumentPreset?> CheckAsync(string agentId,AgentProfile profile,string cwd,AgentCapability capability)
    {
        var known=capability.Presets.FirstOrDefault(p=>p.Semantic=="resume-session"&&p.NeedsValue);
        if(known is not null)return known;
        if(capability.Version is null||!Agents.IsSupported(agentId))return null;
        var command=Commands.ProbeCommand(agentId,profile);
        if(AgentProbe.Fingerprint(command)!=capability.Fingerprint)throw new IOException("Agent 程序已变化，请重新检查。");
        var bare=OmpMemoryGuard.Detach(command);
        var result=await AgentProbe.RunAsync(bare with{Arguments=[..bare.Arguments,..(agentId=="codex"?new[]{"resume","--help"}:new[]{"--help"})]},cwd,TimeSpan.FromSeconds(8));
        if(result.ExitCode!=0||result.TimedOut||result.Truncated)return null;
        var help=result.Output+"\n"+result.Error;
        var valid=agentId=="codex"?Regex.IsMatch(help,@"\[SESSION_ID\]|<SESSION_ID>"):
            Regex.IsMatch(help,@"--resume(?:\s+|=)[<\[]",RegexOptions.CultureInvariant);
        if(!valid)return null;
        if(AgentProbe.Fingerprint(command)!=capability.Fingerprint)throw new IOException("检查期间 Agent 程序已变化，请重新重试。");
        return new(agentId,agentId+"-resume-local","恢复指定会话（本机已核对）","resume-session",[agentId=="codex"?"resume":"--resume",ArgumentPreset.ValueToken],agentId=="omp"?"会话文件路径":"会话 ID",capability.Version);
    }
}
