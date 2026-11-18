using System.ComponentModel;
using System.Text;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>只读工作区改动列表（git status --porcelain=v1 -b）；Ok=false 时其余字段为默认值且 Error 给出可读原因。</summary>
public sealed record WorkspaceChangeList(bool Ok,string Branch,string[] Entries,string Error)
{
    public static WorkspaceChangeList Fail(string error)=>new(false,"",[],error);
}

/// <summary>单文件只读 diff（B1）；Ok=false 时 Text 为空且 Error 给出可读原因。未跟踪文件 Ok=true 并给出提示文本。</summary>
public sealed record WorkspaceFileDiff(bool Ok,string Text,string Error)
{
    public static WorkspaceFileDiff Fail(string error)=>new(false,"",error);
}

/// <summary>远程同步健康状态；就绪判据与 WorkspaceSync.WaitReadyAsync 完全一致（Watching 且无 Transition problems）。
/// Synced/SyncProblem 是终态回退（A1）：会话已终止，依据 attempts/<id>/sync-final.json 显示「已同步」/「同步有问题」。</summary>
public enum SyncHealthState { NotSynced, Syncing, Ready, Problems, Synced, SyncProblem }

/// <summary>同步健康结果；SessionName 为匹配到的 Mutagen 会话名（未匹配时为空），Detail 是可读说明。</summary>
public sealed record SyncHealth(SyncHealthState State,string SessionName,string Detail)
{
    public string Label=>State switch{SyncHealthState.Ready=>"就绪",SyncHealthState.Problems=>"有问题",SyncHealthState.Syncing=>"同步中",SyncHealthState.Synced=>"已同步",SyncHealthState.SyncProblem=>"同步有问题",_=>"未同步"};
}

/// <summary>实用只读助手：本机改动列表 + Mutagen 同步健康。外部命令一律结构化参数、重定向输出、有界超时、超时杀进程；失败返回可读错误，不抛未处理异常。</summary>
public static class WorkspaceInsights
{
    private const int TimeoutSeconds=10;
    /// <summary>B1 单文件 diff 行数上限；对话框累计上限由 UI 层用 TruncateLines 另行约束（2000 行）。</summary>
    public const int MaxDiffLines=400;

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

    /// <summary>B1 单文件只读 diff：git diff -- &lt;file&gt;（core.quotepath=false，10s 有界，超时杀进程树）。
    /// 未跟踪文件返回「未跟踪，无 diff」提示；仅暂存的改动回退 git diff --cached；超过 MaxDiffLines 行截断并提示。绝不写入。</summary>
    public static async Task<WorkspaceFileDiff> ReadDiffAsync(string directory,string file,CancellationToken cancellation=default)
    {
        if(string.IsNullOrWhiteSpace(directory))return WorkspaceFileDiff.Fail("工作目录为空，请先选择项目或为此项指定工作目录。");
        if(string.IsNullOrWhiteSpace(file))return WorkspaceFileDiff.Fail("文件路径为空，无法查看 diff。");
        string root;
        try{root=Path.GetFullPath(directory);}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return WorkspaceFileDiff.Fail("工作目录路径无效。");}
        if(!Directory.Exists(root))return WorkspaceFileDiff.Fail("工作目录不存在或尚未创建："+root);
        var git=Commands.FindExe("git.exe");
        if(git is null||!File.Exists(git))return WorkspaceFileDiff.Fail("未找到 Git，无法查看文件 diff。");
        // 先核对该文件的状态行：未跟踪文件没有 diff；X 位非空说明改动已暂存，工作区 diff 为空时回退 --cached。
        var status=await ReviewGit.CaptureAsync(git,root,["-C",root,"status","--porcelain=v1","--",file],cancellation,TimeoutSeconds,64*1024);
        if(status.TimedOut)return WorkspaceFileDiff.Fail($"Git 状态读取超时（{TimeoutSeconds} 秒），已终止；请稍后重试。");
        if(status.Code!=0)return WorkspaceFileDiff.Fail("读取文件状态失败："+ReviewGit.Clean(status.Error));
        var line=Encoding.UTF8.GetString(status.Output).Replace("\r\n","\n").Split('\n',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"";
        if(line.StartsWith("?? ",StringComparison.Ordinal))return new(true,"未跟踪，无 diff（新文件尚未纳入 Git；git diff 只覆盖已跟踪文件）。","");
        var (text,diffError)=await CaptureDiffAsync(git,root,file,cancellation);
        if(text is null)return WorkspaceFileDiff.Fail(diffError);
        if(text.Length==0&&line.Length>=2&&line[0]is not (' ' or '?'))
        {
            var (cachedText,cachedError)=await CaptureDiffAsync(git,root,file,cancellation,cached:true);
            if(cachedText is null)return WorkspaceFileDiff.Fail(cachedError);
            text=cachedText;
        }
        if(text.Trim().Length==0)return new(true,"该文件没有可显示的 diff（内容未变化或改动已提交）。","");
        var (bounded,truncated)=TruncateLines(text,MaxDiffLines);
        return new(true,bounded+(truncated?$"\n…（单文件 diff 超过 {MaxDiffLines} 行已截断，请在终端运行 git diff -- {file} 查看完整内容）":""),"");
    }

    // Text=null 表示失败（Error 给出可读原因）；Text="" 表示无 diff。只读，超时杀进程树由 ReviewProcess 保证。
    private static async Task<(string? Text,string Error)> CaptureDiffAsync(string git,string root,string file,CancellationToken cancellation,bool cached=false)
    {
        string[] args=cached?["-C",root,"diff","--cached","--",file]:["-C",root,"diff","--",file];
        var capture=await ReviewGit.CaptureAsync(git,root,args,cancellation,TimeoutSeconds,1024*1024);
        if(capture.TimedOut)return(null,$"Git diff 读取超时（{TimeoutSeconds} 秒），已终止；请稍后重试。");
        if(capture.Truncated)return(null,"diff 超出读取边界，已停止；请直接在终端运行 git diff 查看。");
        if(capture.Code!=0)return(null,"读取 diff 失败："+ReviewGit.Clean(capture.Error));
        return(Encoding.UTF8.GetString(capture.Output).Replace("\r\n","\n"),"");
    }

    /// <summary>行数截断（纯函数）：最多保留 maxLines 行内容（末尾换行不计行），并报告是否发生截断。单文件 400 行与对话框累计 2000 行共用。</summary>
    public static (string Text,bool Truncated) TruncateLines(string text,int maxLines)
    {
        if(maxLines<0)throw new ArgumentOutOfRangeException(nameof(maxLines),"行数上限不能为负。");
        var lines=text.Replace("\r\n","\n").Split('\n');
        var count=lines.Length;if(count>0&&lines[^1].Length==0)count--;
        if(count<=maxLines)return(text,false);
        return(string.Join("\n",lines.Take(maxLines)),true);
    }

    /// <summary>porcelain v1 行取文件路径：XY&lt;空格&gt;路径；重命名/复制取 " -&gt; " 之后的新路径；去掉外层引号（quotepath=false 已避免八进制转义）。</summary>
    public static string ChangePath(string line)
    {
        if(line.Length<4)return "";
        var path=line[3..];
        var arrow=path.IndexOf(" -> ",StringComparison.Ordinal);
        if(arrow>=0)path=path[(arrow+4)..];
        if(path.Length>=2&&path[0]=='"'&&path[^1]=='"')path=path[1..^1];
        return path;
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

    /// <summary>A1 徽章状态机（纯函数）：有活会话（Ready/Syncing/Problems）原样沿用；无活会话（NotSynced）时回退
    /// Worker 在远程会话终态写入的 sync-final 记录——synced→已同步，其余一律 fail-closed→同步有问题；无记录→未同步。
    /// 调用方只在条目终态时传入 final；Disconnected 等非终态传 null，仍按活会话评估。</summary>
    public static SyncHealth EvaluateSyncBadge(SyncHealth live,SyncFinalRecord? final)
    {
        if(live.State!=SyncHealthState.NotSynced||final is null)return live;
        return final.State==SyncFinalRecord.Synced
            ?new(SyncHealthState.Synced,"",LaunchSurface.TerminalFlushDetail(final.At))
            :new(SyncHealthState.SyncProblem,"",LaunchSurface.TerminalProblemDetail(final.At));
    }

    /// <summary>读取同步证据（sync-session.json / sync-final.json）；缺失、损坏或不可读一律返回 null，不抛出。
    /// 调用方须再核对 SchemaVersion==1（未知版本按无记录处理，fail-closed 显示未同步）。</summary>
    public static T? ReadSyncEvidence<T>(string path) where T:class
    {
        try{return Store.Read<T>(path);}
        catch(Exception e)when(e is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException){return null;}
    }

    // 与 MutagenSyncSession.WaitReadyAsync 相同的就绪判据：只看 Watching 会误判（远端目录无写权时仍 Watching 但带 Transition problems）。
    private static SyncHealth EvaluateBlock(string name,string block)
    {
        var watching=block.Contains("Status: Watching for changes");
        var problems=block.Contains("Transition problems:");
        if(watching&&!problems)return new(SyncHealthState.Ready,name,"Mutagen 会话处于 Watching，且没有 Transition problems。这不是已收敛，文件也不是实时一致。");
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