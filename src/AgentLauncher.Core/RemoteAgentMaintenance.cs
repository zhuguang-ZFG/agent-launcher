using System.Globalization;
using System.Text.RegularExpressions;

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

    public static string? HelpCommandFor(string agentType)=>AgentCommand(agentType) is {} command?command+" --help":null;

    // 主机覆盖优先；无覆盖回退默认表；未知 agent 返回 null（调用方 fail-closed 报错）。
    public static string? UpgradeCommandFor(RemoteHost host,string agentType)
    {
        if(host.AgentUpgradeCommands?.GetValueOrDefault(agentType) is {Length:>0} overrideCommand)return overrideCommand;
        return DefaultUpgradeCommands.GetValueOrDefault(agentType);
    }

    // C1 远程续接：实测 pi/kimi/qodercli 的 --help 均含 --continue（qoder 是 qodercli 的目录别名）。
    // 其余 agent 未实测，返回 null（fail-closed）；使用前仍须经远程 --help 核对（HelpContainsFlag）。
    public static string[]? ResumeFlagsFor(string agentType)=>agentType switch
    {
        "pi" or "kimi" or "qodercli" or "qoder"=>["--continue"],_=>null
    };

    // 单词边界核对：--continue 不匹配 --continue-session / --no-continue，-c 不匹配 x-c 或 --continue 内部。
    public static bool HelpContainsFlag(string helpOutput,string flag)=>
        flag.Length>0&&Regex.IsMatch(helpOutput,@"(?<![\w-])"+Regex.Escape(flag)+@"(?![\w-])");

    // 续接参数推导：仅支持 -p 任务模式；末尾旧任务说明被新说明替换，--no-session 替换为续接 flag（无则追加）。
    // 原参数已含续接/会话参数时拒绝（避免双重续接），fail-closed。
    public static string[] ResumeArguments(string[] original,string[] resumeFlags,string task)
    {
        if(string.IsNullOrWhiteSpace(task)||task.Contains('\0'))throw new InvalidDataException("新任务说明不能为空或含控制字符。");
        if(resumeFlags.Length==0)throw new InvalidDataException("没有可用的续接参数。");
        if(!original.Contains("-p"))throw new InvalidDataException("远程续接仅支持 -p 任务模式的会话；交互式会话请在终端内自行续接。");
        string[] resumeTokens=["--continue","-c","--resume","-r","--session"];
        if(original.Any(a=>resumeTokens.Contains(a.Split('=')[0])))throw new InvalidDataException("原启动参数已包含续接/会话参数，无法再次续接；请先在此项「详情」中移除后再试。");
        var source=original;
        // 末尾非 flag 参数是旧任务说明；但其前一项若是取值 flag（--model 等），末尾是 flag 值而非说明，保留。
        string[] valueFlags=["--model","-m","--name","-n","--session-dir","--agent"];
        if(source.Length>0&&!source[^1].StartsWith('-')&&(source.Length<2||!valueFlags.Contains(source[^2])))source=source[..^1];
        var result=new List<string>();var replaced=false;
        foreach(var arg in source){if(arg=="--no-session"){if(!replaced){result.AddRange(resumeFlags);replaced=true;}}else result.Add(arg);}
        if(!replaced)result.AddRange(resumeFlags);
        result.Add(task);
        return [..result];
    }

    // B2 资源解析：free -b 的 Mem 总/可用 + nproc + /proc/loadavg 的 15 分钟负载。
    // 任一分量缺失/畸形即整体返回 null（fail-closed），绝不显示部分猜测值。
    public static RemoteResourceSample? ParseResourceSample(string output)
    {
        long total=0,available=0;var cores=0;double load=-1;var haveMem=false;
        foreach(var raw in output.Split('\n'))
        {
            var parts=raw.Trim().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length==0)continue;
            if(parts[0]=="Mem:")
            {
                if(haveMem)return null;
                if(parts.Length<7||!long.TryParse(parts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out total)||!long.TryParse(parts[6],NumberStyles.Integer,CultureInfo.InvariantCulture,out available))return null;
                if(total<=0||available<0||available>total)return null;
                haveMem=true;continue;
            }
            if(parts.Length==1&&cores==0){if(!int.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out cores)||cores is <1 or >4096)return null;continue;}
            if(load<0&&parts.Length>=4&&parts[3].Contains('/'))
            {
                if(!double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out var l1)||!double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out var l5)||!double.TryParse(parts[2],NumberStyles.Float,CultureInfo.InvariantCulture,out var l15)||l1<0||l5<0||l15<0)return null;
                load=l15;
            }
        }
        return haveMem&&cores>0&&load>=0?new RemoteResourceSample(total,available,cores,load):null;
    }

    // C2 版本解析：--version 输出首个非空行中的语义版本（如 "pi 1.5.2"→"1.5.2"）；无则 null（不记录）。
    public static string? ParseVersion(string versionOutput)
    {
        var first=versionOutput.Split('\n').Select(l=>l.Trim()).FirstOrDefault(l=>l.Length>0);
        if(first is null)return null;
        var match=Regex.Match(first,@"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?");
        return match.Success?match.Value:null;
    }

    // 版本历史追加并裁剪：每主机每 agent 上限 50 条，保留最新。
    public static RemoteVersionEntry[] AppendVersion(RemoteVersionEntry[] history,RemoteVersionEntry entry)=>
        [..history.Append(entry).TakeLast(50)];

    // 回滚目标：历史中上一个与最新版本不同的版本；无历史或全部同版本返回 null（回滚选项禁用）。
    public static string? PreviousVersion(RemoteVersionEntry[] history)
    {
        if(history.Length==0)return null;
        var latest=history[^1].Version;
        for(var i=history.Length-2;i>=0;i--)if(history[i].Version!=latest)return history[i].Version;
        return null;
    }

    // 版本号必须是安全 token（字母数字开头，仅含 . - +），拒绝 shell 元字符/空白/换行，fail-closed（\z 而非 $，拒绝尾随换行）。
    public static bool IsSafeVersion(string version)=>
        version.Length is >0 and <=64&&Regex.IsMatch(version,@"^[0-9A-Za-z][0-9A-Za-z.\-+]*\z");

    private static readonly Regex PipUpgradePattern=new(@"^(?<pre>.*?\bpip\s+install\s+)--upgrade\s+(?<pkg>\S+)(?<post>.*)\z",RegexOptions.Compiled);

    // 只有默认形态（npm @latest / pip install --upgrade）可推导固定版本命令；主机覆盖原样命令不支持。
    public static bool SupportsVersionPinning(string command)=>
        command.Contains("@latest",StringComparison.Ordinal)||PipUpgradePattern.IsMatch(command);

    // npm：@latest→@<version>；pip：install --upgrade <pkg>→install <pkg>==<version>。不支持或版本不安全返回 null。
    public static string? PinUpgradeCommand(string command,string version)
    {
        if(!IsSafeVersion(version))return null;
        if(command.Contains("@latest",StringComparison.Ordinal))return command.Replace("@latest","@"+version,StringComparison.Ordinal);
        var match=PipUpgradePattern.Match(command);
        return match.Success?match.Groups["pre"].Value+match.Groups["pkg"].Value+"=="+version+match.Groups["post"].Value:null;
    }
}

// B2 远端资源采样结果（free -b / nproc / loadavg，15 分钟负载）。
public sealed record RemoteResourceSample(long TotalBytes,long AvailableBytes,int Cores,double Load15);

// C2 版本历史条目：agent、版本、时间、当时执行的命令（检查或升级）。
public sealed record RemoteVersionEntry(string Agent,string Version,DateTimeOffset At,string Command);