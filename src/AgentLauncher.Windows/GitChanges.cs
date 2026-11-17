using System.Text;

namespace AgentLauncher.Windows;

/// <summary>工作区相对基线的只读变更概览；Ok=false 时其余字段为默认值且 Error 给出可读原因。</summary>
public sealed record WorktreeChanges(bool Ok,string Branch,string Baseline,bool HasUpstream,int UncommittedCount,string DiffStat,int Ahead,int Behind,string Error)
{
    public string[] UntrackedFiles { get; init; } = [];
    public bool PinnedBaseline { get; init; }
    public static WorktreeChanges Fail(string error)=>new(false,"","",false,0,"",0,0,error);
}

/// <summary>GitHub CLI 可用性与认证状态；Detail 是不含完整路径与凭据的可读说明。</summary>
public sealed record GhStatus(bool Available,bool Authenticated,string Detail);

/// <summary>创建 PR 的结构化结果；失败时 Url 为空且 Error 为命令原文（已做单行截断）。</summary>

/// <summary>只读工作区变更核对与显式的 push / PR 操作。所有外部命令走结构化参数，绝不拼接 shell 字符串。</summary>
public static class GitChanges
{
    private const int ReadTimeoutSeconds=10;
    private const int WriteTimeoutSeconds=120;
    private static readonly TimeSpan GhCacheTtl=TimeSpan.FromSeconds(60);
    private static GhStatus? ghCache;
    private static DateTimeOffset ghCacheAt;
    private static string? ghCacheSignature;

    /// <summary>只读核对当前分支、上游基线、未提交条目、diff --stat 与 ahead/behind。</summary>
    public static WorktreeChanges GetWorktreeChanges(string worktreePath,string? gitPath=null,string? baselineCommit=null)
    {
        var invalid=Resolve(worktreePath,gitPath,out var root,out var git);
        if(invalid is not null)return WorktreeChanges.Fail(invalid);

        var inside=Git(git,root,["rev-parse","--is-inside-work-tree"]);
        if(!inside.Ok||!string.Equals(inside.Text,"true",StringComparison.OrdinalIgnoreCase))return WorktreeChanges.Fail("不是有效的 Git 工作目录。");
        var bare=Git(git,root,["rev-parse","--is-bare-repository"]);
        if(bare.Ok&&string.Equals(bare.Text,"true",StringComparison.OrdinalIgnoreCase))return WorktreeChanges.Fail("裸仓库不支持工作区变更核对。");

        var branchResult=Git(git,root,["rev-parse","--abbrev-ref","HEAD"]);
        if(!branchResult.Ok||branchResult.Text.Length==0)return WorktreeChanges.Fail("无法读取当前分支："+branchResult.Error);
        var branch=branchResult.Text;

        var upstream=Git(git,root,["rev-parse","--abbrev-ref","--symbolic-full-name","@{u}"]);
        string baseline;
        var hasUpstream=upstream.Ok&&upstream.Text.Length>0;
        if(baselineCommit is not null)
        {
            if(baselineCommit.Length is not (40 or 64)||!baselineCommit.All(Uri.IsHexDigit))return WorktreeChanges.Fail("任务基线提交无效，不能改用其他分支替代。");
            baseline=baselineCommit;
            if(!Git(git,root,["cat-file","-e",baseline+"^{commit}"]).Ok)return WorktreeChanges.Fail("任务启动时的基线提交已不可读取，请恢复该提交后再核对。");
        }
        else if(hasUpstream)baseline=upstream.Text;
        else
        {
            var head=Git(git,root,["symbolic-ref","--short","refs/remotes/origin/HEAD"]);
            if(head.Ok&&head.Text.Length>0)baseline=head.Text;
            else if(Git(git,root,["rev-parse","--verify","--quiet","main"]).Ok)baseline="main";
            else if(Git(git,root,["rev-parse","--verify","--quiet","master"]).Ok)baseline="master";
            else return WorktreeChanges.Fail("无法确定对比基线：仓库没有上游，也没有 main / master 默认分支。");
        }

        var status=Git(git,root,["status","--porcelain=v1","-z","--untracked-files=all"]);
        if(!status.Ok)return WorktreeChanges.Fail("读取未提交条目失败："+status.Error);
        var uncommitted=0;var entries=status.Text.Split('\0',StringSplitOptions.RemoveEmptyEntries);
        for(var index=0;index<entries.Length;index++){uncommitted++;if(entries[index].Length>=2&&(entries[index][0] is 'R' or 'C'||entries[index][1] is 'R' or 'C'))index++;}
        var untracked=Git(git,root,["ls-files","--others","--exclude-standard","-z"]);
        if(!untracked.Ok)return WorktreeChanges.Fail("读取新增文件失败："+untracked.Error);

        var diff=Git(git,root,["diff","--stat",baseline]);
        if(!diff.Ok)return WorktreeChanges.Fail("读取变更统计失败："+diff.Error);

        var counts=Git(git,root,["rev-list","--left-right","--count",baseline+"...HEAD"]);
        if(!counts.Ok)return WorktreeChanges.Fail("读取 ahead/behind 失败："+counts.Error);
        var parts=counts.Text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length!=2||!int.TryParse(parts[0],out var behind)||!int.TryParse(parts[1],out var ahead))
            return WorktreeChanges.Fail("无法解析 ahead/behind："+counts.Text);
        return new(true,branch,baseline,hasUpstream,uncommitted,diff.Text,ahead,behind,""){UntrackedFiles=untracked.Text.Split('\0',StringSplitOptions.RemoveEmptyEntries),PinnedBaseline=baselineCommit is not null};
    }

    public static string? ReadHead(string directory)
    {
        var git=Commands.FindExe("git.exe");if(git is null)return null;
        var result=Git(git,directory,["rev-parse","--verify","HEAD"]);
        return result.Ok&&result.Text.Length is 40 or 64&&result.Text.All(Uri.IsHexDigit)?result.Text:null;
    }

    /// <summary>检测 gh 是否存在并完成认证，结果缓存 60 秒；缓存键包含 gh 路径与 PATH，便于 PATH 隔离后立即失效。</summary>
    public static GhStatus GetGhStatus(string? ghPath=null)
    {
        var gh=ghPath??Commands.FindExe("gh.exe");
        var signature=(gh??"")+"|"+(Environment.GetEnvironmentVariable("PATH")??"");
        if(ghCache is not null&&DateTimeOffset.UtcNow-ghCacheAt<GhCacheTtl&&string.Equals(signature,ghCacheSignature,StringComparison.Ordinal))return ghCache;
        GhStatus status;
        if(string.IsNullOrWhiteSpace(gh)||!File.Exists(gh))
            status=new(false,false,"未找到 GitHub CLI（gh），请安装后重试。");
        else
        {
            var version=Run(gh,["--version"]);
            if(!version.Ok)status=new(false,false,"无法运行 GitHub CLI（gh）。");
            else
            {
                var auth=Run(gh,["auth","status"]);
                status=auth.Ok?new(true,true,"GitHub CLI 已认证。"):new(true,false,"GitHub CLI 未认证，请先运行 gh auth login。");
            }
        }
        ghCache=status;ghCacheAt=DateTimeOffset.UtcNow;ghCacheSignature=signature;
        return status;
    }

    private static string? Resolve(string worktreePath,string? gitPath,out string root,out string git)
    {
        root="";git="";
        if(string.IsNullOrWhiteSpace(worktreePath))return "工作目录不能为空。";
        try{root=Path.GetFullPath(worktreePath);}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return "工作目录路径无效。";}
        if(!Directory.Exists(root))return "工作目录不存在。";
        git=gitPath??Commands.FindExe("git.exe")??"";
        if(git.Length==0||!File.Exists(git))return "未找到 Git，无法核对工作目录状态。";
        return null;
    }

    private static (bool Ok,string Text,string Error) Git(string git,string cwd,string[] args,int timeout=ReadTimeoutSeconds)
    {
        var capture=ReviewGit.CaptureAsync(git,cwd,args,CancellationToken.None,timeout,1024*1024).GetAwaiter().GetResult();
        if(capture.TimedOut)return(false,"",$"Git 操作超时（{timeout} 秒）。");
        if(capture.Truncated)return(false,"","Git 输出超出边界。");
        var raw=Encoding.UTF8.GetString(capture.Output);
        var text=args.Contains("-z")?raw:raw.Replace("\r\n","\n").TrimEnd('\n');
        if(capture.Code!=0)return(false,"",ReviewGit.Clean(capture.Error));
        return(true,text,"");
    }

    private static (bool Ok,string Text,string Error) Run(string gh,string[] args,string? cwd=null,int timeout=ReadTimeoutSeconds)
    {
        var capture=ReviewProcess.CaptureAsync(gh,cwd??Environment.CurrentDirectory,args,CancellationToken.None,timeout,256*1024).GetAwaiter().GetResult();
        if(capture.TimedOut)return(false,"","命令超时。");
        var text=Encoding.UTF8.GetString(capture.Output).Trim();
        if(capture.Code!=0)return(false,text,ReviewGit.Clean(capture.Error));
        return(true,text,"");
    }
}
