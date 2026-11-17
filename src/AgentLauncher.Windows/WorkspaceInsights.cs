using System.ComponentModel;
using System.Text;

namespace AgentLauncher.Windows;

/// <summary>只读工作区改动列表（git status --porcelain=v1 -b）；Ok=false 时其余字段为默认值且 Error 给出可读原因。</summary>
public sealed record WorkspaceChangeList(bool Ok,string Branch,string[] Entries,string Error)
{
    public static WorkspaceChangeList Fail(string error)=>new(false,"",[],error);
}

/// <summary>远程同步健康状态；就绪判据与 WorkspaceSync.WaitReadyAsync 完全一致（Watching 且无 Transition problems）。</summary>
public enum SyncHealthState { NotSynced, Syncing, Ready, Problems }

/// <summary>同步健康结果；SessionName 为匹配到的 Mutagen 会话名（未匹配时为空），Detail 是可读说明。</summary>
public sealed record SyncHealth(SyncHealthState State,string SessionName,string Detail)
{
    public string Label=>State switch{SyncHealthState.Ready=>"就绪",SyncHealthState.Problems=>"有问题",SyncHealthState.Syncing=>"同步中",_=>"未同步"};
}

/// <summary>实用只读助手：本机改动列表 + Mutagen 同步健康。外部命令一律结构化参数、重定向输出、有界超时、超时杀进程；失败返回可读错误，不抛未处理异常。</summary>
public static class WorkspaceInsights
{
    private const int TimeoutSeconds=10;

    /// <summary>读取目录的分支与改动条目（porcelain v1 原始行，含 ## 分支行解析）。只读，不修改任何内容。</summary>
    public static WorkspaceChangeList ReadChanges(string directory)
    {
        if(string.IsNullOrWhiteSpace(directory))return WorkspaceChangeList.Fail("工作目录为空，请先选择项目或为此项指定工作目录。");
        string root;
        try{root=Path.GetFullPath(directory);}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return WorkspaceChangeList.Fail("工作目录路径无效。");}
        if(!Directory.Exists(root))return WorkspaceChangeList.Fail("工作目录不存在或尚未创建："+root);
        var git=Commands.FindExe("git.exe");
        if(git is null||!File.Exists(git))return WorkspaceChangeList.Fail("未找到 Git，无法查看工作区改动。");
        var capture=ReviewGit.CaptureAsync(git,root,["-C",root,"status","--porcelain=v1","-b"],CancellationToken.None,TimeoutSeconds,1024*1024).GetAwaiter().GetResult();
        if(capture.TimedOut)return WorkspaceChangeList.Fail($"Git 状态读取超时（{TimeoutSeconds} 秒），已终止；请稍后重试。");
        if(capture.Truncated)return WorkspaceChangeList.Fail("改动列表超出读取边界，已停止；请直接在终端运行 git status 查看。");
        var text=Encoding.UTF8.GetString(capture.Output).Replace("\r\n","\n");
        if(capture.Code!=0)
        {
            var error=ReviewGit.Clean(capture.Error);
            return WorkspaceChangeList.Fail(error.Contains("not a git repository",StringComparison.OrdinalIgnoreCase)?"该目录不是 Git 仓库，无法查看改动。":"读取工作区改动失败："+error);
        }
        var lines=text.Split('\n',StringSplitOptions.RemoveEmptyEntries);
        var hasBranch=lines.Length>0&&lines[0].StartsWith("## ",StringComparison.Ordinal);
        var branch=hasBranch?lines[0][3..]:"未知分支";
        return new(true,branch,hasBranch?lines[1..]:lines,"");
    }

    /// <summary>运行 mutagen sync list（可指定会话名）；mutagen 缺失/失败/超时返回 Ok=false 与可读原因，不抛出。</summary>
    public static async Task<(bool Ok,string Output,string Error)> ListMutagenSessionsAsync(CancellationToken cancellation,string? sessionName=null)
    {
        try
        {
            var args=sessionName is null?new[]{"sync","list"}:new[]{"sync","list",sessionName};
            var capture=await ReviewProcess.CaptureAsync("mutagen",Environment.CurrentDirectory,args,cancellation,TimeoutSeconds,512*1024);
            if(capture.TimedOut)return(false,"",$"mutagen sync list 超时（{TimeoutSeconds} 秒），已终止；同步状态未知。");
            var output=Encoding.UTF8.GetString(capture.Output);
            if(capture.Code!=0)return(false,output,"mutagen sync list 失败（会话可能不存在）："+ReviewGit.Clean(capture.Error));
            return(true,output,"");
        }
        catch(OperationCanceledException){throw;}
        catch(Exception e)when(e is Win32Exception or IOException or InvalidOperationException)
        {return(false,"","未找到 mutagen 或无法运行："+e.Message);}
    }

    /// <summary>从 sync list 输出中按本地目录 + 远程主机/目录匹配会话并判定健康。会话名由 WorkspaceSync 自动生成（agent-launcher- 前缀 + 随机 GUID），无法从配置反推，因此按端点匹配、优先启动器前缀的会话。</summary>
    public static SyncHealth EvaluateSync(string listOutput,string localPath,string remoteHost,string remotePath,string? sessionName=null)
    {
        if(string.IsNullOrWhiteSpace(localPath)||string.IsNullOrWhiteSpace(remotePath))
            return new(SyncHealthState.NotSynced,"","缺少本地或远程工作目录，无法核对同步会话；请先在「详情」中填写远程工作目录。");
        string local;
        try{local=Path.GetFullPath(localPath).TrimEnd('\\','/');}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return new(SyncHealthState.NotSynced,"","本地工作目录路径无效。");}
        var remote=remotePath.TrimEnd('/');
        var remoteTail=remote.StartsWith("~/",StringComparison.Ordinal)?remote[2..]:null;
        string? fallbackName=null,fallbackBlock=null;
        foreach(var block in SplitSessions(listOutput))
        {
            var name=Field(block,"Name:");
            if(sessionName is not null){if(!string.Equals(name,sessionName,StringComparison.Ordinal))continue;}
            if(!block.Contains(local,StringComparison.OrdinalIgnoreCase))continue;
            if(!block.Contains(remoteHost,StringComparison.OrdinalIgnoreCase))continue;
            if(!block.Contains(remote,StringComparison.Ordinal)&&(remoteTail is null||!block.Contains(remoteTail,StringComparison.Ordinal)))continue;
            if(sessionName is not null||name.StartsWith(WorkspaceSync.SessionNamePrefix,StringComparison.Ordinal))return EvaluateBlock(name,block);
            fallbackName??=name;fallbackBlock??=block;
        }
        if(fallbackBlock is not null)return EvaluateBlock(fallbackName!,fallbackBlock);
        return new(SyncHealthState.NotSynced,"","未找到匹配的 Mutagen 同步会话（可能尚未启动或已终止）；远端改动不会落回本机。");
    }

    // 与 MutagenSyncSession.WaitReadyAsync 相同的就绪判据：只看 Watching 会误判（远端目录无写权时仍 Watching 但带 Transition problems）。
    private static SyncHealth EvaluateBlock(string name,string block)
    {
        var watching=block.Contains("Status: Watching for changes");
        var problems=block.Contains("Transition problems:");
        if(watching&&!problems)return new(SyncHealthState.Ready,name,"Mutagen 双向同步就绪（Watching for changes，无 Transition problems）；远端 agent 的改动会持续落回本机目录。");
        if(problems)return new(SyncHealthState.Problems,name,"Mutagen 报告 Transition problems（常见原因：远程工作目录的父目录不属于 agent 用户，无写权）；改动可能无法落回本机，请检查远程目录归属。");
        return new(SyncHealthState.Syncing,name,"同步会话存在但尚未就绪（连接/扫描/冲突处理中）；请稍后刷新。");
    }

    // mutagen sync list 用整行短横线分隔会话；单会话时可能没有分隔线。
    private static List<string> SplitSessions(string output)
    {
        var blocks=new List<string>();var current=new StringBuilder();
        foreach(var line in output.Replace("\r\n","\n").Split('\n'))
        {
            if(line.Length>=10&&line.All(c=>c=='-')){if(current.Length>0)blocks.Add(current.ToString());current.Clear();continue;}
            current.AppendLine(line);
        }
        if(current.Length>0)blocks.Add(current.ToString());
        return blocks;
    }

    private static string Field(string block,string prefix)
    {
        foreach(var line in block.Split('\n')){var trimmed=line.Trim();if(trimmed.StartsWith(prefix,StringComparison.Ordinal))return trimmed[prefix.Length..].Trim();}
        return "";
    }
}