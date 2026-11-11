using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>Read-only change review plus explicit, user-triggered commit/push/PR and monitored verification.</summary>
public sealed class ReviewService
{
    private const int MaxFiles=2000;
    private const long MaxFingerprintBytes=1L*1024*1024*1024;
    private const int MaxDiffBytes=512*1024;
    private const long MaxDiffLines=4000;
    private const long MaxUntrackedDiffBytes=2L*1024*1024;
    private const int MaxLogBytes=64*1024;
    private const int MaxSelectedPaths=500;
    private readonly string? gitPath;
    private readonly string? ghPath;
    public ReviewService(string? gitPath=null,string? ghPath=null)
    {
        this.gitPath=gitPath;
        this.ghPath=ghPath;
    }
    public static string CommandFingerprint(VerificationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var payload=string.Join("\0",[profile.Command.Executable,..profile.Command.Arguments,profile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
    public async Task<ReviewSnapshot> CaptureAsync(string cwd,string? baseSha=null,CancellationToken ct=default)
        =>await Task.Run(()=>CaptureCore(cwd,baseSha,ct),ct);
    public async Task<ReviewDiff> DiffAsync(ReviewSnapshot snapshot,string path,CancellationToken ct=default)
    {
        ValidateSnapshot(snapshot);
        return await Task.Run(()=>DiffCore(snapshot,path,ct),ct);
    }
    public async Task<string> CommitAsync(ReviewSnapshot snapshot,IReadOnlyList<string> selectedPaths,string message,CancellationToken ct=default)
    {
        ValidateSnapshot(snapshot);
        if(selectedPaths is null||selectedPaths.Count is <1 or >MaxSelectedPaths)throw new InvalidDataException($"请明确选择 1–{MaxSelectedPaths} 个要提交的文件。");
        if(string.IsNullOrWhiteSpace(message)||message.Contains('\0')||message.Length>8192)throw new InvalidDataException("提交说明不能为空、不能含 NUL，且不超过 8192 字符。");
        var paths=selectedPaths.Distinct(StringComparer.Ordinal).ToArray();
        if(paths.Length!=selectedPaths.Count)throw new InvalidDataException("提交文件列表包含重复路径。");
        foreach(var path in paths)
        {
            SafeGitPath(path);
            if(!snapshot.Files.Any(file=>string.Equals(file.Path,path,StringComparison.Ordinal)))
                throw new InvalidDataException($"文件不在当前审阅快照中：{path}。请重新加载变更列表。");
        }
        return await Task.Run(()=>CommitCore(snapshot,paths,message,ct),ct);
    }
    public async Task<VerificationResult> VerifyAsync(Guid taskRunId,ReviewSnapshot snapshot,VerificationProfile profile,CancellationToken ct=default)
    {
        if(taskRunId==Guid.Empty)throw new InvalidDataException("检查结果缺少任务运行身份。");
        ReviewPolicy.ValidateProfile(profile);
        ValidateSnapshot(snapshot);
        var executable=ResolveCheckExecutable(profile.Command.Executable);
        var fresh=await CaptureAsync(snapshot.WorkspacePath,snapshot.BaseSha,ct);
        if(!string.Equals(fresh.HeadSha,snapshot.HeadSha,StringComparison.Ordinal)
            ||!string.Equals(fresh.WorktreeFingerprint,snapshot.WorktreeFingerprint,StringComparison.Ordinal)
            ||!string.Equals(fresh.IndexFingerprint,snapshot.IndexFingerprint,StringComparison.Ordinal))
            throw new IOException("工作区在审阅后已变化，检查未运行；请重新加载变更。");
        var command=profile.Command with{Executable=executable};
        var item=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,profile.Name,snapshot.WorkspacePath,PhysicalDirectory.Workspace(snapshot.WorkspacePath),false,command,ResourcePolicy.GitJobMemoryLimit,"git",PlanningBytes:ResourcePolicy.DefaultGitPlanningBytes,ResourceMode:"practical");
        Response reserve;
        try{reserve=await Ipc.ReserveWithCleanupAsync([item],ct,planDigest:ResourcePolicy.PlanDigest([item]));}
        catch(OperationCanceledException)
        {
            return Result(taskRunId,snapshot,profile,"Cancelled",null,0,"","检查已取消。");
        }
        if(!reserve.Ok)throw new IOException("检查未运行："+reserve.Message);
        Process? process=null;var watch=Stopwatch.StartNew();var timedOut=false;var wasCancelled=false;string output="";string error="";int? exitCode=null;
        try
        {
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
            start.ArgumentList.Add("run");start.ArgumentList.Add(item.Id.ToString());
            process=Process.Start(start)??throw new IOException("无法启动受管检查。");
            process.StandardInput.Close();
            var outputTask=ReadTailAsync(process.StandardOutput.BaseStream,MaxLogBytes);
            var errorTask=ReadTailAsync(process.StandardError.BaseStream,MaxLogBytes);
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
                try{await process.WaitForExitAsync(deadline.Token);}
                catch(OperationCanceledException)when(!ct.IsCancellationRequested){timedOut=true;TerminateAttempt(item.Id);}
                catch(OperationCanceledException){wasCancelled=true;TerminateAttempt(item.Id);}
            }
            if(timedOut||wasCancelled)
            {
                using(var grace=new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                {
                    try{await process.WaitForExitAsync(grace.Token);}
                    catch(OperationCanceledException){try{if(!process.HasExited)process.Kill(true);}catch(Exception e)when(e is not OutOfMemoryException){}}
                }
                TerminateAttempt(item.Id);
                await TryRevokeAsync(item.Id);
            }
            output=await outputTask;error=await errorTask;
            if(timedOut||wasCancelled)watch.Stop();
            else
            {
                exitCode=process.ExitCode;
                var receipt=Store.Read<Receipt>(Store.ReceiptPath(item.Id));
                if(receipt==null||receipt.Phase is "FailedBeforeStart" or "Pending" or "Reserved" or "Creating")
                {
                    await TryRevokeAsync(item.Id);
                    throw new IOException("检查未能启动："+FirstLine(error.Length>0?error:output));
                }
            }
        }
        catch
        {
            await TryRevokeAsync(item.Id);
            throw;
        }
        finally{process?.Dispose();}
        watch.Stop();
        var state=timedOut?"TimedOut":wasCancelled?"Cancelled":exitCode==0?"Passed":"Failed";
        if(state=="Passed")
        {
            try
            {
                var completed=await CaptureAsync(snapshot.WorkspacePath,snapshot.BaseSha,CancellationToken.None);
                if(completed.HeadSha!=snapshot.HeadSha||completed.WorktreeFingerprint!=snapshot.WorktreeFingerprint)state="Stale";
            }
            catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                state="Stale";error=FirstLine(error+"\n完成后无法核对工作区："+e.Message);
            }
        }
        return Result(taskRunId,snapshot,profile,state,exitCode,watch.ElapsedMilliseconds,output,error);
    }
    public async Task<CiReport> GetCiAsync(ReviewSnapshot snapshot,CancellationToken ct=default)
    {
        ValidateSnapshot(snapshot);
        return await Task.Run(()=>CiCore(snapshot,ct),ct);
    }
    public async Task<string> PushAsync(ReviewSnapshot snapshot,CancellationToken ct=default)
    {
        ValidateSnapshot(snapshot);
        return await Task.Run(()=>PushCore(snapshot,ct),ct);
    }
    public async Task<string> CreatePullRequestAsync(ReviewSnapshot snapshot,string title,string body,CancellationToken ct=default)
    {
        ValidateSnapshot(snapshot);
        if(string.IsNullOrWhiteSpace(title)||title.Contains('\0')||title.Length>256)throw new InvalidDataException("PR 标题不能为空、不能含 NUL，且不超过 256 字符。");
        if(body is null||body.Contains('\0')||body.Length>64*1024)throw new InvalidDataException("PR 说明不能含 NUL，且不超过 64 KiB。");
        return await Task.Run(()=>PullRequestCore(snapshot,title,body,ct),ct);
    }
    public async Task<IReadOnlyList<ManagedPreviewPort>> PreviewPortsAsync(Guid attemptId,CancellationToken ct=default)
    {
        if(attemptId==Guid.Empty)throw new InvalidDataException("缺少尝试身份。");
        return await Task.Run(()=>PreviewPortsCore(attemptId),ct);
    }

    private string Git=>gitPath??Commands.FindExe("git.exe")??throw new FileNotFoundException("未找到 Git，请先安装 Git for Windows。");
    private string Gh=>ghPath??Commands.FindExe("gh.exe")??throw new FileNotFoundException("未找到 GitHub CLI（gh），请安装后重试。");
    private static ReviewSnapshot ValidateSnapshot(ReviewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if(string.IsNullOrWhiteSpace(snapshot.WorkspacePath)||!Path.IsPathFullyQualified(snapshot.WorkspacePath))throw new InvalidDataException("审阅工作区路径无效。");
        if(!FullSha(snapshot.HeadSha)||!FullSha(snapshot.BaseSha))throw new InvalidDataException("审阅快照的提交身份无效。");
        if(string.IsNullOrEmpty(snapshot.WorkspaceKey)||string.IsNullOrEmpty(snapshot.IndexFingerprint)||string.IsNullOrEmpty(snapshot.WorktreeFingerprint))throw new InvalidDataException("审阅快照缺少内容指纹。");
        if(snapshot.Files is null)throw new InvalidDataException("审阅快照缺少变更文件。");
        return snapshot;
    }
    private static bool FullSha(string? value)=>value is{Length:40 or 64}&&value.All(Uri.IsHexDigit);
    private string ResolveRepository(string cwd,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(cwd)||!Path.IsPathFullyQualified(cwd))throw new InvalidDataException("请选择本地 Git 工作目录。");
        var directory=Path.GetFullPath(cwd);
        if(!Directory.Exists(directory))throw new IOException("所选工作目录不存在。");
        var inside=GitRaw(directory,["rev-parse","--is-inside-work-tree"],ct,60,64*1024);
        if(inside.TimedOut)throw new TimeoutException("读取 Git 仓库超时。");
        if(inside.Code!=0)throw new IOException("所选目录不是 Git 工作区："+ReviewGit.Clean(inside.Error));
        if(Decode(inside.Output).Trim()!="true")throw new InvalidDataException("所选目录不是 Git 工作区。");
        var top=GitChecked(directory,["rev-parse","--show-toplevel"],"读取 Git 工作区",ct,64*1024);
        var root=Path.GetFullPath(Decode(top.Output).Trim());
        if(!Directory.Exists(root))throw new IOException("Git 工作区目录不存在。");
        var bare=GitChecked(root,["rev-parse","--is-bare-repository"],"读取仓库类型",ct,64*1024);
        if(Decode(bare.Output).Trim()=="true")throw new InvalidDataException("不支持 bare 仓库。");
        return root;
    }
    private ReviewSnapshot CaptureCore(string cwd,string? baseSha,CancellationToken ct)
    {
        var root=ResolveRepository(cwd,ct);ct.ThrowIfCancellationRequested();
        var head=RevParse(root,"HEAD^{commit}",ct);
        var resolvedBase=head;
        if(baseSha!=null)
        {
            if(!FullSha(baseSha))throw new InvalidDataException("基准必须是完整的提交 SHA（40 或 64 位十六进制）。");
            resolvedBase=RevParse(root,baseSha+"^{commit}",ct);
            if(!string.Equals(resolvedBase,baseSha,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("基准提交不存在或不是完整 SHA。");
            resolvedBase=resolvedBase.ToLowerInvariant();
        }
        var unmerged=GitChecked(root,["ls-files","--unmerged","-z","--"],"读取索引冲突",ct,1024*1024);
        if(unmerged.Output.Length>0)throw new InvalidDataException("Git 索引存在未解决冲突，请先解决后再审查。");
        var indexFingerprint=IndexFingerprint(root,head,ct);
        var raw=GitChecked(root,["diff","--raw","-z","--no-renames","--no-ext-diff","--no-textconv",resolvedBase,"--"],"读取变更列表",ct,16*1024*1024);
        var seeds=ParseRawDiff(raw.Output);
        var untracked=GitChecked(root,["ls-files","--others","--exclude-standard","-z","--"],"读取未跟踪文件",ct,16*1024*1024);
        var seen=new HashSet<string>(seeds.Select(seed=>seed.Path),StringComparer.Ordinal);
        foreach(var rawPath in NulSegments(untracked.Output)){var path=SafeGitPath(rawPath);if(seen.Add(path))seeds.Add(new(path,"Untracked",true));}
        if(seeds.Count>MaxFiles)throw new IOException($"变更文件超过 {MaxFiles} 个，超出审查边界。");
        seeds.Sort((left,right)=>string.CompareOrdinal(left.Path,right.Path));
        var files=new List<ReviewFile>(seeds.Count);long hashedBytes=0;
        foreach(var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            var full=FullPath(root,seed.Path);
            if(seed.Status=="Deleted")
            {
                if(File.Exists(full)||Directory.Exists(full))throw new IOException($"Git 报告删除但文件仍存在：{seed.Path}。请重新加载。");
                files.Add(new(seed.Path,seed.Status,seed.Untracked,false,0,"missing"));
                continue;
            }
            var info=new FileInfo(full);
            if(!info.Exists)
            {
                if(Directory.Exists(full))throw new InvalidDataException($"目录变更暂不支持审查：{seed.Path}");
                throw new IOException($"无法读取变更文件：{seed.Path}");
            }
            if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException($"符号链接/重解析点暂不支持审查：{seed.Path}");
            var(hash,size,binary)=ReviewGit.HashFile(full,MaxFingerprintBytes-hashedBytes);
            hashedBytes+=size;
            files.Add(new(seed.Path,seed.Status,seed.Untracked,binary,size,hash));
        }
        if(hashedBytes>MaxFingerprintBytes)throw new IOException("工作区变更内容超过审查边界（1 GiB）。");
        WorkspaceIdentity identity;
        try{identity=PhysicalDirectory.Resolve(root);}
        catch(Exception e)when(e is not OutOfMemoryException){throw new IOException("无法解析 Git 工作区物理身份。",e);}
        var canonical=new StringBuilder();
        canonical.Append("workspace ").Append(identity.Key).Append('\n').Append("head ").Append(head).Append('\n').Append("base ").Append(resolvedBase).Append('\n').Append("index ").Append(indexFingerprint).Append('\n');
        foreach(var file in files)
            canonical.Append(file.Path).Append('\0').Append(file.Status).Append('\0').Append(file.Untracked?'1':'0').Append('\0')
                .Append(file.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append('\0').Append(file.Binary?'1':'0').Append('\0').Append(file.ContentHash).Append('\n');
        var fingerprint=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        return new(root,identity.Key,resolvedBase,head,indexFingerprint,fingerprint,[..files],DateTimeOffset.UtcNow);
    }
    private ReviewDiff DiffCore(ReviewSnapshot snapshot,string path,CancellationToken ct)
    {
        var file=snapshot.Files.SingleOrDefault(candidate=>string.Equals(candidate.Path,path,StringComparison.Ordinal))
            ??throw new InvalidDataException("该文件不在当前审阅快照中，请重新加载变更。");
        var root=EnsureWorkspace(snapshot,ct);
        var head=RevParse(root,"HEAD^{commit}",ct);
        if(!string.Equals(head,snapshot.HeadSha,StringComparison.Ordinal))throw new IOException("HEAD 在审阅后已变化，请重新加载变更。");
        EnsureFileUnchanged(file,root,ct);
        if(file.Binary)return BinarySummary(file);
        if(file.Untracked)return UntrackedDiff(root,file,ct);
        return TrackedDiff(root,snapshot.BaseSha,file,ct);
    }
    private ReviewDiff TrackedDiff(string root,string baseSha,ReviewFile file,CancellationToken ct)
    {
        var numstat=GitRaw(root,["diff","--numstat","--no-ext-diff","--no-textconv",baseSha,"--",file.Path],ct,120,1024*1024);
        if(numstat.Truncated)return LargeSummary(file,0,0);
        if(numstat.Code!=0)throw new IOException("无法读取变更行数："+ReviewGit.Clean(numstat.Error));
        var(adds,deletes,binary)=ParseNumstat(numstat.Output);
        if(binary)return BinarySummary(file);
        if(adds+deletes>MaxDiffLines)return LargeSummary(file,adds,deletes);
        var diff=GitRaw(root,["diff","--no-ext-diff","--no-textconv","--no-color",baseSha,"--",file.Path],ct,120,MaxDiffBytes);
        if(!diff.Truncated&&diff.Code!=0)throw new IOException("无法读取 diff："+ReviewGit.Clean(diff.Error));
        var text=Encoding.UTF8.GetString(diff.Output);
        if(text.Contains('\0'))return BinarySummary(file);
        if(text.Trim().Length==0)return new("",false,"没有文本差异。");
        if(diff.Truncated)return new(text+"\n…（diff 已截断）",true,$"{adds} 行新增，{deletes} 行删除；输出已截断。");
        return new(text,false,$"{adds} 行新增，{deletes} 行删除。");
    }
    private ReviewDiff UntrackedDiff(string root,ReviewFile file,CancellationToken ct)
    {
        if(file.SizeBytes>MaxUntrackedDiffBytes)return LargeSummary(file,0,file.SizeBytes);
        var numstat=GitRaw(root,["diff","--no-index","--numstat","--no-ext-diff","--no-textconv","--no-color","--","/dev/null",file.Path],ct,120,1024*1024);
        if(numstat.Truncated)return LargeSummary(file,0,0);
        if(numstat.Code is not(0 or 1))throw new IOException("无法读取新文件行数："+ReviewGit.Clean(numstat.Error));
        var(adds,deletes,binary)=ParseNumstat(numstat.Output);
        if(binary)return BinarySummary(file);
        if(adds+deletes>MaxDiffLines)return LargeSummary(file,adds,deletes);
        var diff=GitRaw(root,["diff","--no-index","--no-ext-diff","--no-textconv","--no-color","--","/dev/null",file.Path],ct,120,MaxDiffBytes);
        if(!diff.Truncated&&diff.Code is not(0 or 1))throw new IOException("无法读取新文件 diff："+ReviewGit.Clean(diff.Error));
        var text=Encoding.UTF8.GetString(diff.Output);
        if(text.Contains('\0'))return BinarySummary(file);
        if(text.Trim().Length==0)return new("",false,"没有文本差异。");
        if(diff.Truncated)return new(text+"\n…（diff 已截断）",true,$"{adds} 行新增；输出已截断。");
        return new(text,false,$"{adds} 行新增。");
    }
    private ReviewDiff BinarySummary(ReviewFile file)=>new("",true,$"二进制文件（{file.SizeBytes} 字节），未显示内容。");
    private static ReviewDiff LargeSummary(ReviewFile file,long adds,long deletes,long size=0) =>
        new("",true,size>0?$"文件较大（{size} 字节），未显示内容。":$"变更较大（+{adds} / -{deletes} 行），未显示全文。");
    private string CommitCore(ReviewSnapshot snapshot,string[] paths,string message,CancellationToken ct)
    {
        var root=EnsureWorkspace(snapshot,ct);
        var head=RevParse(root,"HEAD^{commit}",ct);
        if(!string.Equals(head,snapshot.HeadSha,StringComparison.Ordinal))throw new IOException("HEAD 在审阅后已变化，提交已取消。请重新加载变更。");
        var currentIndex=IndexFingerprint(root,head,ct);
        if(!string.Equals(currentIndex,snapshot.IndexFingerprint,StringComparison.Ordinal))throw new IOException("Git 索引在审阅后已变化，提交已取消。请重新加载变更。");
        EnsureNoConflicts(root,ct);
        var identity=GitRaw(root,["var","GIT_AUTHOR_IDENT"],ct,60,64*1024);
        if(identity.Code!=0)throw new IOException("Git 未配置提交身份（user.name / user.email），无法提交。");
        var branchReference=BranchRef(root,ct);
        var indexPath=Path.GetFullPath(Decode(GitChecked(root,["rev-parse","--path-format=absolute","--git-path","index"],"定位索引",ct,64*1024).Output).Trim());
        var lockPath=indexPath+".lock";
        using var indexLock=new FileStream(lockPath,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        var indexPublished=false;
        var contentLocks=new List<FileStream>();
        var scratch=Path.Combine(Path.GetTempPath(),"AgentLauncher-review-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            // Hold Git's real index lock from revalidation through ref/index publication. A concurrent git add/checkout must wait or fail.
            if(!string.Equals(IndexFingerprint(root,head,ct),snapshot.IndexFingerprint,StringComparison.Ordinal))throw new IOException("Git 索引在取得写锁前已变化，提交已取消。");
            var nextIndex=Path.Combine(scratch,"next-index");
            var nextEnvironment=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"GIT_INDEX_FILE",nextIndex}};
            if(File.Exists(indexPath))File.Copy(indexPath,nextIndex);
            else GitChecked(root,["read-tree",head],"读取原索引",ct,64*1024,nextEnvironment);
            var tempIndex=Path.Combine(scratch,"index");
            var indexEnvironment=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"GIT_INDEX_FILE",tempIndex}};
            GitChecked(root,["read-tree",snapshot.HeadSha],"准备临时索引",ct,64*1024,indexEnvironment);
            var updates=new List<(ReviewFile File,string? Blob,string Mode)>(paths.Length);
            foreach(var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                var file=snapshot.Files.Single(candidate=>string.Equals(candidate.Path,path,StringComparison.Ordinal));
                var full=FullPath(root,path);
                if(file.Status=="Deleted"||file.ContentHash=="missing")
                {
                    if(File.Exists(full)||Directory.Exists(full))throw new IOException($"文件在审阅后已恢复：{path}。提交已取消。");
                    if(file.ContentHash!="missing")throw new IOException($"删除状态与审阅内容不一致：{path}。提交已取消。");
                    GitChecked(root,["update-index","--force-remove","--",path],"准备删除",ct,64*1024,indexEnvironment);
                    updates.Add((file,null,"100644"));
                    continue;
                }
                var info=new FileInfo(full);
                if(!info.Exists)throw new IOException($"文件在审阅后已删除：{path}。提交已取消。");
                if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException($"符号链接/重解析点暂不支持提交：{path}");
                contentLocks.Add(new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.Read));
                var mode=FileModeFor(root,path,ct);
                var(blob,hash)=ReviewGit.HashObjectAsync(Git,root,full,ct).GetAwaiter().GetResult();
                if(!string.Equals(hash,file.ContentHash,StringComparison.Ordinal))throw new IOException($"文件在审阅后已变化：{path}。提交已取消；未提交任何内容。");
                GitChecked(root,["update-index","--add","--cacheinfo",$"{mode},{blob},{path}"],"准备提交内容",ct,64*1024,indexEnvironment);
                updates.Add((file,blob,mode));
            }
            var tree=Decode(GitChecked(root,["write-tree"],"生成提交树",ct,64*1024,indexEnvironment).Output).Trim();
            var headTree=Decode(GitChecked(root,["rev-parse",head+"^{tree}"],"读取 HEAD 树",ct,64*1024).Output).Trim();
            if(string.Equals(tree,headTree,StringComparison.Ordinal))throw new IOException("所选文件没有可提交的变化。");
            if(!string.Equals(RevParse(root,"HEAD^{commit}",ct),head,StringComparison.Ordinal))throw new IOException("HEAD 在提交准备期间已变化，提交已取消。");
            if(!string.Equals(IndexFingerprint(root,head,ct),snapshot.IndexFingerprint,StringComparison.Ordinal))throw new IOException("Git 索引在提交准备期间已变化，提交已取消。");
            var messagePath=Path.Combine(scratch,"message.txt");
            File.WriteAllText(messagePath,message,new UTF8Encoding(false));
            GitChecked(root,["hook","run","--ignore-missing","pre-commit"],"项目 pre-commit 检查拒绝提交",ct,64*1024,indexEnvironment);
            GitChecked(root,["hook","run","--ignore-missing","prepare-commit-msg","--",messagePath,"message"],"项目 prepare-commit-msg 拒绝提交",ct,64*1024,indexEnvironment);
            GitChecked(root,["hook","run","--ignore-missing","commit-msg","--",messagePath],"项目 commit-msg 检查拒绝提交",ct,64*1024,indexEnvironment);
            if(Decode(GitChecked(root,["write-tree"],"核对 hook 后的提交树",ct,64*1024,indexEnvironment).Output).Trim()!=tree
                ||File.ReadAllText(messagePath)!=message)throw new IOException("项目 hook 修改了待提交内容或说明，请重新审阅后提交；未移动 HEAD。");
            var signing=GitRaw(root,["config","--bool","--get","commit.gpgsign"],ct,60,64*1024);
            if(signing.Code is not (0 or 1))throw new IOException("无法核对项目提交签名设置。");
            var commitArguments=new List<string>{"commit-tree",tree,"-p",head,"-F",messagePath};
            if(Decode(signing.Output).Trim()=="true")commitArguments.Add("-S");
            var commit=Decode(GitChecked(root,[..commitArguments],"创建提交",ct,64*1024).Output).Trim();
            if(!FullSha(commit))throw new IOException("Git 返回的提交身份无效，提交已取消。");
            foreach(var(file,blob,mode)in updates)
            {
                if(blob==null)GitChecked(root,["update-index","--force-remove","--",file.Path],"准备索引发布",ct,64*1024,nextEnvironment);
                else GitChecked(root,["update-index","--add","--cacheinfo",$"{mode},{blob},{file.Path}"],"准备索引发布",ct,64*1024,nextEnvironment);
                EnsureFileUnchanged(file,root,ct);
            }
            using(var preparedIndex=File.OpenRead(nextIndex))preparedIndex.CopyTo(indexLock);
            indexLock.Flush(true);
            if(BranchRef(root,ct)!=branchReference)throw new IOException("当前分支在提交准备期间已变化，提交已取消。");
            GitChecked(root,["update-ref",branchReference,commit,head],"更新分支",ct,64*1024);
            // From here cancellation cannot strand a published ref with an unpublished index.
            try{indexLock.Dispose();File.Move(lockPath,indexPath,true);indexPublished=true;}
            catch(Exception e)when(e is not OutOfMemoryException)
            {
                throw new IOException($"提交 {commit} 已创建，但索引发布失败：{e.Message}；请核对 HEAD 与索引，不要重复提交。",e);
            }
            try{GitChecked(root,["hook","run","--ignore-missing","post-commit"],"项目 post-commit 执行失败",CancellationToken.None,64*1024);}
            catch(IOException error){throw new IOException($"提交 {commit} 已创建，但 post-commit 失败：{error.Message}；请勿重复提交。",error);}
            return commit;
        }
        finally
        {
            indexLock.Dispose();
            if(!indexPublished)try{File.Delete(lockPath);}catch(IOException){}
            foreach(var held in contentLocks)held.Dispose();
            try{Directory.Delete(scratch,true);}catch(Exception e)when(e is not OutOfMemoryException){}
        }
    }
    private string PushCore(ReviewSnapshot snapshot,CancellationToken ct)
    {
        var root=EnsureWorkspace(snapshot,ct);
        var head=RevParse(root,"HEAD^{commit}",ct);
        if(!string.Equals(head,snapshot.HeadSha,StringComparison.Ordinal))throw new IOException("HEAD 在审阅后已变化，请重新加载后再推送。");
        var branch=ShortBranch(BranchRef(root,ct));
        var remote=Remote(root,ct)??throw new IOException("未配置 Git 远程仓库，无法推送。");
        var result=GitRaw(root,["push","--porcelain",remote.Name,$"{snapshot.HeadSha}:refs/heads/{branch}"],ct,300,1024*1024);
        if(result.TimedOut)throw new TimeoutException("推送超时，未确认远程状态。");
        if(result.Code!=0)throw new IOException(MapAuth(ReviewGit.Clean(result.Error.Length>0?result.Error:Decode(result.Output)),"推送"));
        return $"已推送 {snapshot.HeadSha} 到 {remote.Name}/{branch}。";
    }
    private string PullRequestCore(ReviewSnapshot snapshot,string title,string body,CancellationToken ct)
    {
        var root=EnsureWorkspace(snapshot,ct);
        var head=RevParse(root,"HEAD^{commit}",ct);
        if(!string.Equals(head,snapshot.HeadSha,StringComparison.Ordinal))throw new IOException("HEAD 在审阅后已变化，请重新加载后再创建 PR。");
        var branch=ShortBranch(BranchRef(root,ct));
        var remote=Remote(root,ct)??throw new IOException("未配置 Git 远程仓库，无法创建 PR。");
        if(!TryGitHubSlug(remote.Url,out var slug))throw new InvalidDataException("目前只支持 GitHub 远程仓库创建 PR。");
        var gh=Gh;
        var environment=GhEnvironment();
        var remoteHeads=GitRaw(root,["ls-remote","--heads",remote.Name,"refs/heads/"+branch],ct,120,1024*1024);
        if(remoteHeads.TimedOut)throw new TimeoutException("查询远程分支超时。");
        if(remoteHeads.Code!=0)throw new IOException(MapAuth(ReviewGit.Clean(remoteHeads.Error),"查询远程分支"));
        var remoteSha=Decode(remoteHeads.Output).Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).FirstOrDefault()?.Split('\t')[0];
        if(remoteSha is null)throw new IOException("远程分支尚不存在，请先推送当前提交。");
        if(!string.Equals(remoteSha,snapshot.HeadSha,StringComparison.OrdinalIgnoreCase))throw new IOException("远程分支未指向当前审阅提交，请先推送。");
        var repository=GhApi(gh,root,["api",$"repos/{slug}"],ct,environment,"读取仓库信息");
        var defaultBranch=Decode(repository.Output).Trim().Length>0
            ? JsonString(repository.Output,"default_branch")??throw new IOException("GitHub 未返回默认分支。")
            : throw new IOException("GitHub 未返回仓库信息。");
        var existing=GhApi(gh,root,["pr","list","--repo",slug,"--head",branch,"--state","open","--json","url","--limit","1"],ct,environment,"查询已有 PR");
        using(var document=JsonDocument.Parse(existing.Output))
        {
            if(document.RootElement.ValueKind==JsonValueKind.Array&&document.RootElement.GetArrayLength()>0)
            {
                var existingUrl=JsonString(document.RootElement[0],"url")??"";
                throw new IOException("该分支已有打开的 PR："+existingUrl);
            }
        }
        var created=GhApi(gh,root,["pr","create","--repo",slug,"--head",branch,"--base",defaultBranch,"--title",title,"--body",body],ct,environment,"创建 PR");
        var output=Decode(created.Output);
        var url=output.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).LastOrDefault(line=>line.StartsWith("https://",StringComparison.OrdinalIgnoreCase));
        if(url is null)throw new IOException("PR 已创建，但 GitHub 未返回链接。");
        return url;
    }
    private CiReport CiCore(ReviewSnapshot snapshot,CancellationToken ct)
    {
        var root=Path.GetFullPath(snapshot.WorkspacePath);
        var remote=Directory.Exists(root)?Remote(root,ct):null;
        if(remote is null)return new("",snapshot.HeadSha,"Unknown","未配置 Git 远程仓库，无法查询 GitHub CI。",[]);
        var info=remote.Value;
        if(!TryGitHubSlug(info.Url,out var slug))return new(info.Name,snapshot.HeadSha,"Unknown","当前只支持 GitHub 远程仓库的 CI 查询。",[]);
        string gh;
        try{gh=Gh;}catch(FileNotFoundException e){return new(slug,snapshot.HeadSha,"Unavailable",e.Message,[]);}
        var environment=GhEnvironment();
        var runsResponse=GitRawPath(gh,root,["api",$"repos/{slug}/actions/runs?head_sha={snapshot.HeadSha}&per_page=100"],ct,60,4*1024*1024,environment);
        if(runsResponse.TimedOut)return new(slug,snapshot.HeadSha,"Unavailable","GitHub API 请求超时。",[]);
        if(runsResponse.Code!=0)return GhFailure(slug,snapshot.HeadSha,DescribeGhFailure(runsResponse));
        var runs=ParseRuns(runsResponse.Output,snapshot.HeadSha,out var parsed);
        if(!parsed)return new(slug,snapshot.HeadSha,"Unknown","GitHub 返回的 CI 数据无法识别。",[]);
        if(JsonLong(runsResponse.Output,"total_count") is not { } runCount||runCount>100)return new(slug,snapshot.HeadSha,"Unknown","workflow 列表超出读取边界，不能判为通过。",[]);
        var checksResponse=GitRawPath(gh,root,["api",$"repos/{slug}/commits/{snapshot.HeadSha}/check-runs?per_page=100"],ct,60,4*1024*1024,environment);
        if(checksResponse.TimedOut)return new(slug,snapshot.HeadSha,"Unavailable","GitHub check runs 请求超时。",[]);
        if(checksResponse.Code!=0)return GhFailure(slug,snapshot.HeadSha,DescribeGhFailure(checksResponse));
        var statusesResponse=GitRawPath(gh,root,["api",$"repos/{slug}/commits/{snapshot.HeadSha}/status?per_page=100"],ct,60,4*1024*1024,environment);
        if(statusesResponse.TimedOut)return new(slug,snapshot.HeadSha,"Unavailable","GitHub commit statuses 请求超时。",[]);
        if(statusesResponse.Code!=0)return GhFailure(slug,snapshot.HeadSha,DescribeGhFailure(statusesResponse));
        try{runs=[..runs,..ReviewCi.Checks(checksResponse.Output,snapshot.HeadSha),..ReviewCi.Statuses(statusesResponse.Output,snapshot.HeadSha)];}
        catch(Exception error)when(error is JsonException or InvalidDataException){return new(slug,snapshot.HeadSha,"Unknown",error.Message,[]);}
        if(runs.Length>0)
        {
            var state=ReviewPolicy.CiState(snapshot.HeadSha,runs);
            var message=state switch
            {
                "Passed"=>"目标提交的 CI 已通过。",
                "Failed"=>"目标提交的 CI 失败。",
                "Running"=>"目标提交的 CI 正在运行。",
                _=>"目标提交的 CI 状态未知。"
            };
            return new(slug,snapshot.HeadSha,state,$"{message}（{runs.Length} 项 workflow/check/status 证据）",runs);
        }
        var workflows=GitRawPath(gh,root,["api",$"repos/{slug}/actions/workflows"],ct,60,4*1024*1024,environment);
        if(workflows.TimedOut)return new(slug,snapshot.HeadSha,"Unavailable","GitHub API 请求超时。",[]);
        if(workflows.Code!=0)return GhFailure(slug,snapshot.HeadSha,DescribeGhFailure(workflows));
        var total=JsonLong(workflows.Output,"total_count");
        if(total is null)return new(slug,snapshot.HeadSha,"Unknown","GitHub 返回的 workflow 数据无法识别。",[]);
        if(total==0)return new(slug,snapshot.HeadSha,"NoWorkflow","该仓库没有配置 GitHub Actions workflow。",[]);
        return new(slug,snapshot.HeadSha,"Unknown","该提交尚无 workflow 运行；GitHub 可能仍在排队或未触发。",[]);
    }
    private IReadOnlyList<ManagedPreviewPort> PreviewPortsCore(Guid attemptId)
    {
        int[] members;
        try
        {
            using var job=new Job(Store.JobName(attemptId),openOnly:true);
            members=job.Members();
        }
        catch(Win32Exception e)when(e.NativeErrorCode==2){throw new IOException("找不到该尝试的任务组；任务可能已结束。",e);}
        if(members.Length==0)return [];
        var memberSet=new HashSet<int>(members);
        var listeners=new Dictionary<int,(int Pid,bool Ipv6)>();
        CollectListeners(false,memberSet,listeners);
        CollectListeners(true,memberSet,listeners);
        return listeners.OrderBy(pair=>pair.Key).Select(pair=>new ManagedPreviewPort(pair.Key,pair.Value.Pid,pair.Value.Ipv6?$"http://[::1]:{pair.Key}/":$"http://127.0.0.1:{pair.Key}/")).ToArray();
    }
    private string EnsureWorkspace(ReviewSnapshot snapshot,CancellationToken ct)
    {
        var root=Path.GetFullPath(snapshot.WorkspacePath);
        if(!Directory.Exists(root))throw new IOException("审阅工作区已不存在。");
        WorkspaceIdentity identity;
        try{identity=PhysicalDirectory.Resolve(root);}
        catch(Exception e)when(e is not OutOfMemoryException){throw new IOException("无法解析审阅工作区物理身份。",e);}
        if(!string.Equals(identity.Key,snapshot.WorkspaceKey,StringComparison.Ordinal))throw new IOException("审阅工作区物理身份已变化，请重新加载。");
        var top=Path.GetFullPath(Decode(GitChecked(root,["rev-parse","--show-toplevel"],"读取 Git 工作区",ct,64*1024).Output).Trim());
        if(!string.Equals(top,root,StringComparison.OrdinalIgnoreCase))throw new IOException("审阅工作区已更换 Git 仓库，请重新加载。");
        return root;
    }
    private void EnsureNoConflicts(string root,CancellationToken ct)
    {
        var unmerged=GitChecked(root,["ls-files","--unmerged","-z","--"],"读取索引冲突",ct,1024*1024);
        if(unmerged.Output.Length>0)throw new InvalidDataException("Git 索引存在未解决冲突，请先解决后再提交。");
    }
    private static void EnsureFileUnchanged(ReviewFile file,string root,CancellationToken ct)
    {
        var full=FullPath(root,file.Path);
        if(file.Status=="Deleted"||file.ContentHash=="missing")
        {
            if(File.Exists(full)||Directory.Exists(full))throw new IOException($"文件在审阅后已恢复：{file.Path}。请重新加载变更。");
            return;
        }
        var info=new FileInfo(full);
        if(!info.Exists)throw new IOException($"文件在审阅后已删除：{file.Path}。请重新加载变更。");
        if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException($"符号链接/重解析点暂不支持：{file.Path}");
        var(hash,_,_)=ReviewGit.HashFile(full,MaxFingerprintBytes);
        if(!string.Equals(hash,file.ContentHash,StringComparison.Ordinal))throw new IOException($"文件在审阅后已变化：{file.Path}。请重新加载变更。");
    }
    private string IndexFingerprint(string root,string head,CancellationToken ct)
    {
        var raw=GitRaw(root,["diff-index","--cached","--raw","-z","--no-ext-diff",head,"--"],ct,120,32*1024*1024);
        if(raw.Truncated)throw new IOException("Git 索引超出审查边界。");
        if(raw.Code!=0)throw new IOException("无法读取 Git 索引："+ReviewGit.Clean(raw.Error));
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(head+"\n"));
        hash.AppendData(raw.Output);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private string RevParse(string root,string expression,CancellationToken ct)
    {
        var value=Decode(GitChecked(root,["rev-parse","--verify",expression],"解析提交",ct,64*1024).Output).Trim();
        if(!FullSha(value))throw new IOException("Git 返回的提交身份无效。");
        return value.ToLowerInvariant();
    }
    private string BranchRef(string root,CancellationToken ct)
    {
        var result=GitRaw(root,["symbolic-ref","-q","HEAD"],ct,60,64*1024);
        if(result.TimedOut)throw new TimeoutException("读取当前分支超时。");
        if(result.Code==1)throw new IOException("当前处于分离 HEAD，请先切换到分支。");
        if(result.Code!=0)throw new IOException("无法读取当前分支："+ReviewGit.Clean(result.Error));
        var reference=Decode(result.Output).Trim();
        if(!reference.StartsWith("refs/heads/",StringComparison.Ordinal))throw new IOException("当前分支身份无效。");
        return reference;
    }
    private static string ShortBranch(string reference)=>reference["refs/heads/".Length..];
    private (string Name,string Url)? Remote(string root,CancellationToken ct)
    {
        var listing=GitRaw(root,["remote"],ct,60,64*1024);
        if(listing.TimedOut)throw new TimeoutException("读取远程仓库超时。");
        if(listing.Code!=0)return null;
        var names=Decode(listing.Output).Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        var name=names.Contains("origin",StringComparer.Ordinal)?"origin":names.Length==1?names[0]:null;
        if(name is null)return null;
        var urlResult=GitRaw(root,["remote","get-url",name],ct,60,64*1024);
        if(urlResult.Code!=0)return null;
        var url=Decode(urlResult.Output).Trim();
        return url.Length==0?null:(name,url);
    }
    private static bool TryGitHubSlug(string url,out string slug)
    {
        slug="";var text=url.Trim();
        if(text.StartsWith("git@github.com:",StringComparison.OrdinalIgnoreCase))text=text["git@github.com:".Length..];
        else if(text.StartsWith("ssh://git@github.com/",StringComparison.OrdinalIgnoreCase))text=text["ssh://git@github.com/".Length..];
        else if(text.StartsWith("https://github.com/",StringComparison.OrdinalIgnoreCase))text=text["https://github.com/".Length..];
        else if(text.StartsWith("http://github.com/",StringComparison.OrdinalIgnoreCase))text=text["http://github.com/".Length..];
        else return false;
        if(text.EndsWith(".git",StringComparison.OrdinalIgnoreCase))text=text[..^4];
        text=text.Trim('/');
        var parts=text.Split('/');
        if(parts.Length!=2||parts.Any(part=>part.Length==0))return false;
        if(!parts.All(part=>part.All(character=>char.IsLetterOrDigit(character)||character is '-' or '_' or '.')))return false;
        slug=parts[0]+"/"+parts[1];return true;
    }
    private ProcessCapture GhApi(string gh,string root,IReadOnlyList<string> arguments,CancellationToken ct,IReadOnlyDictionary<string,string> environment,string operation)
    {
        var result=GitRawPath(gh,root,arguments,ct,120,4*1024*1024,environment);
        if(result.TimedOut)throw new TimeoutException(operation+"超时。");
        if(result.Truncated)throw new IOException(operation+"：GitHub 输出超出边界。");
        if(result.Code!=0)throw new IOException(MapAuth(ReviewGit.Clean(result.Error.Length>0?result.Error:Decode(result.Output)),operation));
        return result;
    }
    private static Dictionary<string,string> GhEnvironment()=>new(StringComparer.OrdinalIgnoreCase){{"GH_PROMPT_DISABLED","1"},{"GH_NO_UPDATE_NOTIFIER","1"},{"NO_COLOR","1"}};
    private CiReport GhFailure(string repository,string sha,string detail)
    {
        var text=detail.ToLowerInvariant();
        if(text.Contains("401")||text.Contains("403")||text.Contains("bad credentials")||text.Contains("authentication")||text.Contains("not logged")||text.Contains("gh auth login"))
            return new(repository,sha,"Unauthorized","GitHub 认证失败或无权限；请先运行 gh auth login。",[]);
        if(text.Contains("could not resolve")||text.Contains("dial tcp")||text.Contains("connection")||text.Contains("timeout")||text.Contains("timed out")||text.Contains("tls")||text.Contains("proxy"))
            return new(repository,sha,"Unavailable","无法连接 GitHub；请检查网络和代理。",[]);
        if(text.Contains("404")||text.Contains("not found"))
            return new(repository,sha,"Unknown","仓库不存在或 GitHub Actions 未启用。",[]);
        return new(repository,sha,"Unknown","GitHub 返回错误："+detail,[]);
    }
    private static string DescribeGhFailure(ProcessCapture result)
    {
        var text=ReviewGit.Clean(result.Error);
        if(text.Length==0)text=ReviewGit.Clean(Decode(result.Output));
        return text.Length==0?"退出码 "+result.Code.ToString(CultureInfo.InvariantCulture):text;
    }
    private static string MapAuth(string detail,string operation)
    {
        var text=detail.ToLowerInvariant();
        if(text.Contains("authentication")||text.Contains("permission")||text.Contains("denied")||text.Contains("403")||text.Contains("could not read username")||text.Contains("terminal prompts disabled")||text.Contains("credential"))
            return operation+"被拒绝：无权限或需要登录。";
        if(text.Contains("non-fast-forward")||text.Contains("rejected")||text.Contains("fetch first"))
            return operation+"被拒绝：远程分支已前进，请先同步。";
        return operation+"失败："+detail;
    }
    private static (long Adds,long Deletes,bool Binary) ParseNumstat(byte[] output)
    {
        long adds=0,deletes=0;var binary=false;
        foreach(var record in NulSegments(output))
        {
            var parts=record.Split('\t');
            if(parts.Length<2)throw new IOException("Git 行数统计格式无法识别。");
            if(parts[0]=="-"){binary=true;continue;}
            if(!long.TryParse(parts[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out var addValue)
                ||!long.TryParse(parts[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out var deleteValue))
                throw new IOException("Git 行数统计格式无法识别。");
            adds+=addValue;deletes+=deleteValue;
        }
        return (adds,deletes,binary);
    }
    private static List<Seed> ParseRawDiff(byte[] output)
    {
        var segments=NulSegments(output);
        if(segments.Length%2!=0)throw new IOException("Git 变更列表格式不完整。");
        var seeds=new List<Seed>();var seen=new HashSet<string>(StringComparer.Ordinal);
        for(var index=0;index<segments.Length;index+=2)
        {
            var header=segments[index];var rawPath=segments[index+1];
            if(header.Length<6||header[0]!=':')throw new IOException("Git 变更列表格式无法识别。");
            var fields=header[1..].Split(' ');
            if(fields.Length<5||fields[4].Length==0)throw new IOException("Git 变更列表字段不完整。");
            var oldMode=fields[0];var newMode=fields[1];var status=fields[4][0];
            if(status=='U')throw new InvalidDataException("Git 中存在未解决冲突，无法审查。请先解决冲突。");
            if(status is 'R' or 'C')throw new InvalidDataException("暂不支持改名/复制状态的审查。");
            if(oldMode is "120000" or "160000"||newMode is "120000" or "160000")throw new InvalidDataException("符号链接或子模块变更暂不支持审查。");
            var mapped=status switch
            {
                'A'=>"Added",
                'D'=>"Deleted",
                'M'=>"Modified",
                'T'=>throw new InvalidDataException("文件类型变化暂不支持审查。"),
                _=>throw new InvalidDataException("Git 变更状态无法识别。")
            };
            var path=SafeGitPath(rawPath);
            if(!seen.Add(path))throw new IOException("Git 变更列表包含重复路径。");
            seeds.Add(new(path,mapped,false));
        }
        return seeds;
    }
    private static string[] NulSegments(byte[] output)
    {
        var values=new List<string>();var start=0;var strict=new UTF8Encoding(false,true);
        for(var index=0;index<=output.Length;index++)
        {
            if(index<output.Length&&output[index]!=0)continue;
            if(index>start)
            {
                try{values.Add(strict.GetString(output,start,index-start));}
                catch(DecoderFallbackException){throw new InvalidDataException("Git 返回了非 UTF-8 路径，暂不支持。");}
            }
            start=index+1;
        }
        return[..values];
    }
    private static string SafeGitPath(string path)
    {
        if(path.Length is 0 or >4096||path.Contains('\0'))throw new InvalidDataException("Git 返回了无效路径。");
        var normalized=path.Replace('\\','/');
        if(normalized[0]=='/'||normalized.Contains(':'))throw new InvalidDataException($"不支持绝对或设备路径：{path}");
        foreach(var part in normalized.Split('/'))
            if(part.Length==0||part is "." or ".."||string.Equals(part,".git",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException($"不支持路径：{path}");
        return normalized;
    }
    private static string FullPath(string root,string path)
    {
        var full=Path.GetFullPath(Path.Combine(root,path.Replace('/',Path.DirectorySeparatorChar)));
        var prefix=root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException($"路径越出工作区：{path}");
        return full;
    }
    private string FileModeFor(string root,string path,CancellationToken ct)
    {
        var result=GitRaw(root,["ls-files","-s","-z","--",path],ct,60,1024*1024);
        if(result.TimedOut)throw new TimeoutException("读取文件模式超时。");
        if(result.Code!=0)throw new IOException("无法读取文件模式："+ReviewGit.Clean(result.Error));
        var segments=NulSegments(result.Output);
        if(segments.Length==0)return "100644";
        var entry=segments[0];var tab=entry.IndexOf('\t');
        if(tab<0)throw new IOException("Git 索引条目格式无法识别。");
        var mode=entry[..tab].Split(' ')[0];
        if(mode is "120000" or "160000")throw new InvalidDataException("符号链接或子模块暂不支持提交。");
        if(mode is not("100644" or "100755"))throw new InvalidDataException("不支持的文件模式："+mode);
        return mode;
    }
    private static string ResolveCheckExecutable(string name)
    {
        var path=Commands.FindExe(name)??throw new InvalidDataException("未找到检查程序；请选择已安装的 exe。");
        if(!Path.GetExtension(path).Equals(".exe",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("检查程序必须是 exe；不支持 shell 脚本或 .cmd。");
        return Path.GetFullPath(path);
    }
    private VerificationResult Result(Guid taskRunId,ReviewSnapshot snapshot,VerificationProfile profile,string state,int? exitCode,long duration,string output,string error) =>
        new(Guid.NewGuid(),taskRunId,snapshot.HeadSha,snapshot.WorktreeFingerprint,profile.Id,CommandFingerprint(profile),state,exitCode,duration,output,error,DateTimeOffset.UtcNow,snapshot.WorkspaceKey);
    private static async Task<string> ReadTailAsync(Stream stream,int max)
    {
        var chunks=new Queue<byte[]>();var total=0;var buffer=new byte[16*1024];var truncated=false;
        try
        {
            int count;
            while((count=await stream.ReadAsync(buffer))>0)
            {
                var chunk=new byte[count];Array.Copy(buffer,chunk,count);chunks.Enqueue(chunk);total+=count;
                while(chunks.Count>1&&total-chunks.Peek().Length>=max)total-=chunks.Dequeue().Length;
            }
        }
        catch(Exception e)when(e is IOException or ObjectDisposedException){}
        if(total>max)truncated=true;
        var skip=Math.Max(0,total-max);using var memory=new MemoryStream();
        foreach(var chunk in chunks)
        {
            if(skip>=chunk.Length){skip-=chunk.Length;continue;}
            memory.Write(chunk,skip,chunk.Length-skip);skip=0;
        }
        var text=Encoding.UTF8.GetString(memory.ToArray());
        return truncated?text+"\n…（输出已截断）":text;
    }
    private static void TerminateAttempt(Guid attemptId)
    {
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(DateTime.UtcNow<deadline)
        {
            try
            {
                using var job=new Job(Store.JobName(attemptId),openOnly:true);
                Native.Check(Native.TerminateJobObject(job.Handle,1));
                return;
            }
            catch(Exception e)when(e is not OutOfMemoryException){Thread.Sleep(100);}
        }
    }
    private static async Task TryRevokeAsync(Guid attemptId)
    {
        try{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:attemptId));}
        catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException){}
    }
    private static string FirstLine(string text)
    {
        var line=text.Replace('\r',' ').Replace('\n',' ').Trim();
        return line.Length<=800?line:line[..800]+"…";
    }
    private ProcessCapture GitRaw(string cwd,string[] arguments,CancellationToken ct,int timeoutSeconds=120,int maxOutput=16*1024*1024)
    {
        var result=ReviewGit.CaptureAsync(Git,cwd,arguments,ct,timeoutSeconds,maxOutput).GetAwaiter().GetResult();
        if(result.TimedOut)throw new TimeoutException("Git 操作超时。");
        return result;
    }
    private static ProcessCapture GitRawPath(string executable,string cwd,IReadOnlyList<string> arguments,CancellationToken ct,int timeoutSeconds,int maxOutput,IReadOnlyDictionary<string,string>? environment)
        =>ReviewProcess.CaptureAsync(executable,cwd,arguments,ct,timeoutSeconds,maxOutput,environment).GetAwaiter().GetResult();
    private ProcessCapture GitChecked(string cwd,string[] arguments,string failure,CancellationToken ct,int maxOutput=16*1024*1024,IReadOnlyDictionary<string,string>? environment=null)
    {
        var result=ReviewGit.CaptureAsync(Git,cwd,arguments,ct,120,maxOutput,environment).GetAwaiter().GetResult();
        if(result.TimedOut)throw new TimeoutException(failure+"：Git 操作超时。");
        if(result.Truncated)throw new IOException(failure+"：输出超出边界。");
        if(result.Code!=0)throw new IOException(failure+"："+ReviewGit.Clean(result.Error));
        return result;
    }
    private static string Decode(byte[] bytes)=>Encoding.UTF8.GetString(bytes);
    private static string? JsonString(byte[] json,string name)
    {
        using var document=JsonDocument.Parse(json);
        return JsonString(document.RootElement,name);
    }
    private static string? JsonString(JsonElement element,string name)=>element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static long? JsonLong(byte[] json,string name)
    {
        using var document=JsonDocument.Parse(json);
        return document.RootElement.ValueKind==JsonValueKind.Object&&document.RootElement.TryGetProperty(name,out var value)&&value.TryGetInt64(out var number)?number:null;
    }
    private static CiRun[] ParseRuns(byte[] json,string sha,out bool parsed)
    {
        parsed=false;
        using var document=JsonDocument.Parse(json);
        if(document.RootElement.ValueKind!=JsonValueKind.Object||!document.RootElement.TryGetProperty("workflow_runs",out var runs)||runs.ValueKind!=JsonValueKind.Array)return[];
        parsed=true;var values=new List<(CiRun Run,string WorkflowKey,DateTimeOffset At)>();
        foreach(var run in runs.EnumerateArray())
        {
            if(run.ValueKind!=JsonValueKind.Object)continue;
            var runSha=JsonString(run,"head_sha");
            if(!string.Equals(runSha,sha,StringComparison.OrdinalIgnoreCase))continue;
            var id=run.TryGetProperty("id",out var identifier)&&identifier.TryGetInt64(out var number)?number:0;
            var name=JsonString(run,"name")??JsonString(run,"display_title")??"workflow";
            var status=JsonString(run,"status")??"unknown";
            var conclusion=JsonString(run,"conclusion");
            var url=JsonString(run,"html_url")??"";
            var workflowKey=run.TryGetProperty("workflow_id",out var workflow)&&workflow.TryGetInt64(out var workflowId)?workflowId.ToString(CultureInfo.InvariantCulture):name;
            var at=JsonTime(run,"run_started_at")??JsonTime(run,"created_at")??DateTimeOffset.MinValue;
            values.Add((new(id,name,runSha!,status,conclusion,url),workflowKey,at));
        }
        // GitHub returns newest first, but sort explicitly and keep only the latest run per workflow so an older failure
        // for the same target SHA can never mask a newer success (or the reverse).
        return[..values.OrderByDescending(value=>value.At).ThenByDescending(value=>value.Run.Id).GroupBy(value=>value.WorkflowKey,StringComparer.Ordinal).Select(group=>group.First().Run)];
    }
    private static DateTimeOffset? JsonTime(JsonElement element,string name) =>
        JsonString(element,name) is{} text&&DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var value)?value:null;
    private static void CollectListeners(bool ipv6,HashSet<int> members,Dictionary<int,(int Pid,bool Ipv6)> listeners)
    {
        const int tableClass=3;
        var family=ipv6?23:2;var size=0;
        var error=GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,tableClass,0);
        if(error!=0&&error!=122)throw new IOException("无法读取本机端口列表（Win32 "+error.ToString(CultureInfo.InvariantCulture)+"）。");
        if(size<=0)return;
        var buffer=Marshal.AllocHGlobal(size);
        try
        {
            error=GetExtendedTcpTable(buffer,ref size,false,family,tableClass,0);
            if(error==122)return;
            if(error!=0)throw new IOException("无法读取本机端口列表（Win32 "+error.ToString(CultureInfo.InvariantCulture)+"）。");
            var count=Marshal.ReadInt32(buffer);
            var rowSize=ipv6?56:24;
            if(count<0||(long)count*rowSize+4>size)throw new IOException("端口列表超出读取边界。");
            for(var index=0;index<count;index++)
            {
                var offset=4+index*rowSize;
                var state=Marshal.ReadInt32(buffer,offset+(ipv6?48:0));
                if(state!=2)continue;
                var portRaw=Marshal.ReadInt32(buffer,offset+(ipv6?20:8));
                var port=((portRaw&0xFF)<<8)|((portRaw>>8)&0xFF);
                if(port<=0)continue;
                var pid=Marshal.ReadInt32(buffer,offset+(ipv6?52:20));
                if(!members.Contains(pid))continue;
                var loopback=false;
                if(ipv6)
                {
                    loopback=true;
                    for(var addressIndex=0;addressIndex<15;addressIndex++)
                        if(Marshal.ReadByte(buffer,offset+addressIndex)!=0){loopback=false;break;}
                    loopback&=Marshal.ReadByte(buffer,offset+15)==1;
                }
                else loopback=(Marshal.ReadInt32(buffer,offset+4)&0xFF)==127;
                if(!loopback)continue;
                if(listeners.TryGetValue(port,out var existing)&&(!existing.Ipv6||ipv6))continue;
                listeners[port]=(pid,ipv6);
            }
        }
        finally{Marshal.FreeHGlobal(buffer);}
    }
    [DllImport("iphlpapi.dll",SetLastError=true)] private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int tableClass,uint reserved);
    private sealed record Seed(string Path,string Status,bool Untracked);
}
