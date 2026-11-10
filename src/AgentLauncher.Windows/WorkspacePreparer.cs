using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;
public sealed record CommandResult(int Code,string Output,string Error);
public static class ControlledCommand
{
    public static async Task<CommandResult> RunAsync(CommandSpec command,string cwd,CancellationToken ct=default,Action<string>? outputLine=null)
    {
        ct.ThrowIfCancellationRequested();
        var item=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"Git 准备",cwd,PhysicalDirectory.Workspace(cwd),false,command,ResourcePolicy.GitJobMemoryLimit,"git",PlanningBytes:ResourcePolicy.DefaultGitPlanningBytes,ResourceMode:"practical");
        var reserve=await Ipc.ReserveWithCleanupAsync([item],ct);if(!reserve.Ok)throw new IOException(reserve.Message);
        Process? process=null;
        try
        {
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            start.ArgumentList.Add("run");start.ArgumentList.Add(item.Id.ToString());process=Process.Start(start)??throw new IOException("无法运行受控准备程序。");process.StandardInput.Close();
            var output=outputLine==null?ReadBoundedAsync(process.StandardOutput):ReadLinesAsync(process.StandardOutput,outputLine);var error=ReadBoundedAsync(process.StandardError);
            // Cancellation stops subsequent operations. An in-progress Git write is allowed to finish.
            await process.WaitForExitAsync();var result=new CommandResult(process.ExitCode,await output,await error);
            var after=await Ipc.SendAsync(new("status",Guid.NewGuid()));
            if(after.Entries?.Any(e=>e.Plan.Id==item.Id&&e.State=="Pending")==true)
            {
                // The Runner exited while the reservation is still Pending: begin was rejected and the command never ran. Release the global Git slot instead of returning a fake exit code.
                try{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));}catch(IOException){}catch(TimeoutException){}
                throw new IOException("准备命令未运行："+(result.Error.Trim().Length>0?result.Error.Trim():"启动授权未领取。"));
            }
            ct.ThrowIfCancellationRequested();return result;
        }
        catch
        {
            // May have reached the Runner. Revoke only Pending; Creating remains under reconciliation.
            try{await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:item.Id));}catch(IOException){}catch(OperationCanceledException){}
            throw;
        }
        finally{process?.Dispose();}
    }
    private static async Task<string> ReadLinesAsync(StreamReader reader,Action<string> consume)
    {
        var line=new StringBuilder();var buffer=new char[4096];int count;Exception? failure=null;
        while((count=await reader.ReadAsync(buffer))>0)
        {
            for(var i=0;i<count;i++)
            {
                if(failure!=null)continue;
                if(buffer[i]=='\n'){try{consume(line.ToString().TrimEnd('\r'));}catch(Exception e)when(e is not OutOfMemoryException){failure=e;}line.Clear();}
                else if(line.Length>=8192)failure=new IOException("准备命令输出行超出边界。");else line.Append(buffer[i]);
            }
        }
        if(failure!=null)throw new IOException("流式准备结果无法解析。",failure);
        if(line.Length>0)consume(line.ToString());return "";
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text=new StringBuilder();var buffer=new char[4096];int count;var overflow=false;
        while((count=await reader.ReadAsync(buffer))>0){if(text.Length+count<=1024*1024)text.Append(buffer,0,count);else overflow=true;}
        if(overflow)throw new IOException("命令输出超过 1 MiB，已停止采集；实际操作结果需要核对。");return text.ToString();
    }
}
public sealed class PreparationRecord
{
    public int SchemaVersion { get; set; }
    public required string Digest { get; set; }
    public required List<LaunchItem> Original { get; set; }
    public Dictionary<Guid,LaunchItem> Completed { get; set; }=[];
    public Dictionary<string,string> Baselines { get; set; }=[];
}
public sealed class WorkspacePreparer
{
    private readonly string git=Commands.FindExe("git.exe")??"";
    private readonly string? destinationRoot;
    public WorkspacePreparer(string? destinationRoot=null){this.destinationRoot=string.IsNullOrWhiteSpace(destinationRoot)?null:destinationRoot;}
    private static bool StructurallyValid(PreparationRecord record,IReadOnlyList<LaunchItem> expected)
    {
        if(string.IsNullOrWhiteSpace(record.Digest)||record.Original is not {Count:>0}||record.Original.Count!=expected.Count||record.Completed==null||record.Baselines==null)return false;
        for(var i=0;i<record.Original.Count;i++)
        {
            var plan=record.Original[i];var current=expected[i];
            if(!Usable(plan)||plan.Position!=current.Position||plan.Name!=current.Name||plan.Shared!=current.Shared||plan.Kind!=current.Kind||plan.Budget!=current.Budget)return false;
            if(plan.Cwd!=current.Cwd||plan.Workspace!=current.Workspace||plan.BatchId!=record.Original[0].BatchId)return false;
            for(var j=0;j<i;j++)if(record.Original[j].Id==plan.Id)return false;
        }
        foreach(var pair in record.Completed)
        {
            var plan=pair.Value;if(!Usable(plan)||plan.Id!=pair.Key)return false;
            var original=record.Original.Find(p=>p.Id==pair.Key);if(original==null)return false;
            if(plan.Position!=original.Position||plan.BatchId!=original.BatchId||plan.Name!=original.Name||plan.Shared!=original.Shared||plan.Kind!=original.Kind||plan.Budget!=original.Budget)return false;
        }
        foreach(var baseline in record.Baselines)
        {
            if(baseline.Value is not {Length:40 or 64}||!baseline.Value.All(Uri.IsHexDigit))return false;
            if(!record.Original.Exists(p=>p.Workspace.Key==baseline.Key))return false;
        }
        return true;
    }
    private static bool Usable(LaunchItem? plan)
    {
        if(plan==null||plan.Id==Guid.Empty||plan.BatchId==Guid.Empty||plan.Position is <1 or >4||!ValidText(plan.Name)||!AbsolutePath(plan.Cwd))return false;
        if(plan.Workspace==null||!AbsolutePath(plan.Workspace.Path)||!ValidText(plan.Workspace.Key)||plan.Command==null)return false;
        if(!AbsolutePath(plan.Command.Executable)||!plan.Command.Executable.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||plan.Command.Adapter is not ("exe" or "node-shim")||plan.Command.Arguments==null)return false;
        foreach(var argument in plan.Command.Arguments)if(argument==null||argument.Contains('\0'))return false;
        return true;
    }
    private static bool ValidText(string? value)=>!string.IsNullOrWhiteSpace(value)&&!value.Contains('\0');
    private static bool AbsolutePath(string? value)=>ValidText(value)&&Path.IsPathFullyQualified(value!);
    public async Task<List<LaunchItem>> PrepareAsync(Draft draft,Action<string> progress,Func<string,Task<bool>> confirmHead,CancellationToken ct,IReadOnlyList<WorkspaceIdentity>? activeWorkspaces=null)
    {
        draft.Validate();
        Directory.CreateDirectory(Store.Root);
        await using var preparationLock=new FileStream(Store.FilePath("preparation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var active=draft.Slots.Where(s=>s.Enabled).ToArray();if(active.Length==0)throw new IOException("先勾选本次启动项。");
        if(active.Any(s=>s.RequiresDirectory&&string.IsNullOrWhiteSpace(s.Directory)))throw new IOException("通用组合中有需要单独指定的工作目录，请先在对应 Agent 详情中选择文件夹。");
        var batch=Guid.NewGuid();var plans=new List<LaunchItem>();
        foreach(var slot in active)
        {
            ct.ThrowIfCancellationRequested();var cwd=PhysicalDirectory.Resolve(string.IsNullOrWhiteSpace(slot.Directory)?draft.Project:slot.Directory).Path;
            var position=draft.Slots.IndexOf(slot)+1;
            var resource=draft.Resource??new ResourceSettings();
            plans.Add(new(Guid.NewGuid(),batch,position,slot.DisplayName,cwd,PhysicalDirectory.Workspace(cwd),draft.DirectoryMode=="shared",Commands.Resolve(slot.AgentId,slot.Current),resource.JobMemoryLimit,PlanningBytes:resource.PlanningBytes,ResourceMode:resource.Mode));
        }
        var defaultRoot=Store.FilePath("worktrees");var worktreeRoot=defaultRoot;var nondefaultRoot=false;
        var isolatedGroups=plans.GroupBy(p=>p.Workspace.Key).Where(g=>g.Count()>1&&draft.DirectoryMode=="isolated").ToArray();
        if(isolatedGroups.Length>0)
        {
            var physicalDefault=Directory.Exists(defaultRoot)?PhysicalDirectory.Resolve(defaultRoot):null;
            worktreeRoot=destinationRoot!=null?PhysicalDirectory.Resolve(destinationRoot).Path:physicalDefault?.Path??Path.Combine(PhysicalDirectory.Resolve(Store.Root).Path,"worktrees");
            nondefaultRoot=destinationRoot!=null&&!StringComparer.OrdinalIgnoreCase.Equals(physicalDefault?.Path??Path.GetFullPath(defaultRoot),worktreeRoot);
            foreach(var group in isolatedGroups)
            {
                var source=group.First().Workspace.Path.TrimEnd(Path.DirectorySeparatorChar);var target=Path.GetFullPath(worktreeRoot).TrimEnd(Path.DirectorySeparatorChar);
                if(target.Equals(source,StringComparison.OrdinalIgnoreCase)||target.StartsWith(source+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("独立工作目录必须放在源仓库之外，请在设置中选择其他位置。");
            }
        }
        var canonical=draft.Copy();canonical.Revision=0;
        var digest=Convert.ToHexString(SHA256.HashData(!nondefaultRoot?JsonSerializer.SerializeToUtf8Bytes(canonical,Store.Json):JsonSerializer.SerializeToUtf8Bytes(new{Draft=canonical,WorktreeRoot=worktreeRoot.ToUpperInvariant()},Store.Json)));
        var preparationPath=Store.FilePath("preparations/"+digest+".json");
        var record=Store.Read<PreparationRecord>(preparationPath);List<LaunchItem>? effective=null;
        if(record!=null)
        {
            if(record.SchemaVersion is <0 or >1)throw new IOException($"准备记录版本不支持，未改写文件：{preparationPath}");
            if(record.Digest!=digest)throw new IOException("准备记录摘要不符，禁止重放。");
            if(!StructurallyValid(record,plans))throw new IOException($"准备记录损坏，禁止重放：{preparationPath}");
            // Reject batch target collisions and changed prepared-directory identities before the repair/baseline/completed writes below.
            EnsureBatchTargetsDistinct(plans,record.Original[0].BatchId,worktreeRoot,draft.DirectoryMode,activeWorkspaces);
            EnsureExistingTargetsStable(record,worktreeRoot,record.Original[0].BatchId,draft.DirectoryMode);
            var repaired=false;
            // The draft digest still matches, so slot order is authoritative; keep worktree mapping but re-resolve commands so shim/entry updates are honored.
            for(var i=0;i<record.Original.Count;i++)
            {
                var plan=record.Original[i];var slot=draft.Slots[plan.Position-1];var fresh=Commands.Resolve(slot.AgentId,slot.Current);
                if(plan.Command.Executable!=fresh.Executable||!plan.Command.Arguments.SequenceEqual(fresh.Arguments)||plan.Command.Adapter!=fresh.Adapter){record.Original[i]=plan with{Command=fresh};repaired=true;}
            }
            foreach(var id in record.Completed.Keys.ToArray()){var mapped=record.Original.Single(p=>p.Id==id).Command;if(record.Completed[id].Command.Executable!=mapped.Executable||!record.Completed[id].Command.Arguments.SequenceEqual(mapped.Arguments)||record.Completed[id].Command.Adapter!=mapped.Adapter){record.Completed[id]=record.Completed[id] with{Command=mapped};repaired=true;}}
            if(record.SchemaVersion!=1||repaired){record.SchemaVersion=1;Store.Save(preparationPath,record);}
            effective=record.Original.Select(p=>record.Completed.GetValueOrDefault(p.Id)??p).ToList();
        }
        if(record==null){EnsureBatchTargetsDistinct(plans,batch,worktreeRoot,draft.DirectoryMode,activeWorkspaces);record=new(){SchemaVersion=1,Digest=digest,Original=plans};Store.Save(preparationPath,record);}
        plans=effective??record.Original.ToList();batch=record.Original[0].BatchId;
        foreach(var group in record.Original.GroupBy(p=>p.Workspace.Key).Where(g=>g.Count()>1).ToArray())
        {
            if(draft.DirectoryMode=="shared")continue;
            if(draft.DirectoryMode=="prepared")throw new IOException("已有目录必须分别指向不同工作目录，请在各项参数中设置目录。");
            if(git.Length==0)throw new IOException("未找到 Git，不能创建独立工作目录。");
            var root=group.First().Workspace.Path;
            if(!Directory.Exists(Path.Combine(root,".git"))&&!File.Exists(Path.Combine(root,".git")))throw new IOException("同目录多 Agent 需要 Git worktree，或明确选择共享目录。");
            var oid=(await Git(root,["rev-parse","--verify","HEAD"],ct)).Trim();
            if(oid.Length is not (40 or 64)||!oid.All(Uri.IsHexDigit))throw new IOException("仓库没有有效基线提交。");
            if(record.Baselines.TryGetValue(group.Key,out var fixedOid))oid=fixedOid;
            else{record.Baselines[group.Key]=oid;Store.Save(preparationPath,record);}
            if((await Git(root,["ls-tree",oid,".gitmodules"],ct)).Length>0)throw new IOException("首版不自动准备含子模块的仓库，请先自行准备独立目录。");
            var sparse=await ControlledCommand.RunAsync(new(git,["config","--bool","core.sparseCheckout"],"exe"),root,ct);
            if(sparse.Code is not (0 or 1)||sparse.Output.Trim()=="true")throw new IOException("稀疏检出尚未验证，请使用已准备目录。");
            var dirty=(await Git(root,["status","--porcelain","--untracked-files=normal"],ct)).Length>0;
            if(dirty&&!await confirmHead($"原项目有未提交修改。独立目录只包含已提交版本 {oid[..8]}，不复制未提交文件、依赖或 .env。继续使用这个版本？"))throw new OperationCanceledException("已取消目录准备。");
            ulong estimated=0;
            var sizeResult=await ControlledCommand.RunAsync(new(git,["ls-tree","-r","--format=%(objectsize)",oid],"exe"),root,ct,line=>{if(ulong.TryParse(line,out var bytes))estimated=checked(estimated+bytes);else if(line!="-")throw new IOException("Git 文件大小格式无效。");});
            if(sizeResult.Code!=0)throw new IOException("无法估算检出大小："+sizeResult.Error);
            foreach(var plan in group)
            {
                ct.ThrowIfCancellationRequested();var target=Path.Combine(worktreeRoot,batch.ToString("N"),"agent-"+plan.Position);
                var branch=$"launcher/{batch:N}/{plan.Position}";
                var workspacePath=Store.FilePath($"workspaces/{plan.Id:N}.json");
                var known=File.Exists(workspacePath);var targetExists=Directory.Exists(target);var completed=record.Completed.GetValueOrDefault(plan.Id);
                // Never adopt a replacement directory: compare the stored physical identity before any intent/completed/progress/record write.
                EnsureTargetAdoptable(workspacePath,root,target,branch,oid,completed);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);var drive=new DriveInfo(Path.GetPathRoot(target)!);
                var reserve=Math.Max(2*ResourcePolicy.GiB,(ulong)drive.TotalSize/20);
                if((ulong)drive.AvailableFreeSpace<reserve+estimated)throw new IOException("目标盘空间不足以保留余量和检出文件。");
                if(!known&&!targetExists)Store.Save(workspacePath,new{State="Intent",Root=root,Target=target,Branch=branch,Oid=oid});
                progress($"正在准备/核对第 {plan.Position} 项独立目录 · {oid[..8]}。取消只停止后续步骤，已创建目录保留。");
                if(!targetExists)await Git(root,["worktree","add","-b",branch,target,oid],ct);
                var actual=(await Git(target,["rev-parse","HEAD"],ct)).Trim();if(actual!=oid)throw new IOException("已有准备目录基线已变化，请改用“使用已准备目录”。");
                var actualBranch=(await Git(target,["symbolic-ref","--short","HEAD"],ct)).Trim();if(actualBranch!=branch)throw new IOException("已有目录分支不匹配，禁止复用。");
                // Same OID/branch is not proof of identity: a separate clone can satisfy both, so validate the repository and worktree registry.
                if(targetExists)await EnsureLinkedWorktreeAsync(root,target,branch,oid,ct);
                var relative=Path.GetRelativePath(root,plan.Cwd);var mapped=relative=="."?target:Path.Combine(target,relative);
                if(!Directory.Exists(mapped))throw new IOException("基线不包含原子目录；已准备目录保留，请选择仓库根目录重试。");
                if(completed!=null)EnsureCompletedCwdIdentity(completed,mapped);
                var replacement=plan with{Cwd=PhysicalDirectory.Resolve(mapped).Path,Workspace=PhysicalDirectory.Workspace(mapped)};
                plans[plans.FindIndex(p=>p.Id==plan.Id)]=replacement;record.Completed[plan.Id]=replacement;Store.Save(preparationPath,record);
                Store.Save(Store.FilePath($"workspaces/{plan.Id:N}.json"),new{State="Prepared",Root=root,Target=target,Branch=branch,Oid=oid});
            }
        }
        // Prepared directories are reusable; launch authorization IDs are always fresh.
        var launchBatch=Guid.NewGuid();return plans.Select(p=>p with{Id=Guid.NewGuid(),BatchId=launchBatch}).ToList();
    }
    private static void EnsureBatchTargetsDistinct(IReadOnlyList<LaunchItem> plans,Guid batch,string worktreeRoot,string mode,IReadOnlyList<WorkspaceIdentity>? activeWorkspaces)
    {
        if(mode!="isolated")return;
        var isolated=plans.GroupBy(p=>p.Workspace.Key).Where(g=>g.Count()>1).SelectMany(g=>g).Select(p=>p.Id).ToHashSet();
        // Direct slots never write a worktree, so only isolated targets can mutate disk before the monitor's reserve check.
        if(isolated.Count==0)return;
        // Compare the eventual prepared workspace (actual worktree target), never the destination root: a direct sibling under that root may be valid.
        var eventual=new Dictionary<Guid,string>();
        foreach(var plan in plans)
        {
            var path=isolated.Contains(plan.Id)?Path.Combine(worktreeRoot,batch.ToString("N"),"agent-"+plan.Position):plan.Workspace.Path;
            eventual[plan.Id]=Directory.Exists(path)?PhysicalDirectory.Resolve(path).Path:Path.GetFullPath(path);
        }
        foreach(var plan in plans)
        {
            if(!isolated.Contains(plan.Id))continue;
            foreach(var other in plans)
            {
                if(other.Id==plan.Id)continue;
                if(!SameOrWithin(eventual[plan.Id],eventual[other.Id])&&!SameOrWithin(eventual[other.Id],eventual[plan.Id]))continue;
                if(plan.Shared&&other.Shared)continue;
                throw new IOException("独立工作目录目标与同批其他工作目录重叠，已停止；请调整目录模式或位置。");
            }
            // A target written now must not land inside (or contain) a workspace that is already active. The monitor refuses
            // that batch at reserve time, which is after the worktree, branch and preparation record exist; check it here first.
            foreach(var active in activeWorkspaces??[])
            {
                if(!SameOrWithin(eventual[plan.Id],active.Path)&&!SameOrWithin(active.Path,eventual[plan.Id]))continue;
                throw new IOException("独立工作目录目标与正在运行的会话工作目录重叠，已停止；请先结束该会话或更换存放位置。");
            }
        }
    }
    private static void EnsureExistingTargetsStable(PreparationRecord record,string worktreeRoot,Guid batch,string mode)
    {
        if(mode!="isolated")return;
        foreach(var group in record.Original.GroupBy(p=>p.Workspace.Key).Where(g=>g.Count()>1))
        {
            var root=group.First().Workspace.Path;var baseline=record.Baselines.GetValueOrDefault(group.Key);
            foreach(var plan in group)
            {
                EnsureTargetAdoptable(Store.FilePath($"workspaces/{plan.Id:N}.json"),root,Path.Combine(worktreeRoot,batch.ToString("N"),"agent-"+plan.Position),$"launcher/{batch:N}/{plan.Position}",baseline,record.Completed.GetValueOrDefault(plan.Id));
            }
        }
    }
    private static void EnsureTargetAdoptable(string workspacePath,string root,string target,string branch,string? oid,LaunchItem? completed)
    {
        var known=File.Exists(workspacePath);var targetExists=Directory.Exists(target);
        if(targetExists&&known)ValidateIntentRecord(workspacePath,root,target,branch,oid);
        if(!targetExists&&known)throw new IOException("上次准备已中断且目录未找到。请核对工作目录记录；不会自动重复创建或删除分支。");
        if(!targetExists&&completed!=null)throw new IOException("已准备目录缺失，禁止自动重放；请核对工作目录记录。");
        if(targetExists&&completed!=null)EnsureCompletedTargetIdentity(completed,target);
        else if(targetExists&&!known)throw new IOException("目标目录已存在但没有准备意图记录，禁止复用或删除。");
    }
    private static void ValidateIntentRecord(string path,string root,string target,string branch,string? oid)
    {
        Dictionary<string,JsonElement>? state;
        try{state=Store.Read<Dictionary<string,JsonElement>>(path);}catch(Exception e)when(e is JsonException or InvalidDataException){throw new IOException($"工作目录意图记录无法解析，禁止重放：{path}",e);}
        if(state==null
          ||!state.TryGetValue("State",out var stateValue)||stateValue.ValueKind!=JsonValueKind.String||stateValue.GetString() is not ("Intent" or "Prepared")
          ||!state.TryGetValue("Root",out var rootValue)||rootValue.ValueKind!=JsonValueKind.String
          ||!state.TryGetValue("Target",out var targetValue)||targetValue.ValueKind!=JsonValueKind.String
          ||!state.TryGetValue("Branch",out var branchValue)||branchValue.ValueKind!=JsonValueKind.String
          ||!state.TryGetValue("Oid",out var oidValue)||oidValue.ValueKind!=JsonValueKind.String
          ||branchValue.GetString()!=branch||(oid!=null&&oidValue.GetString()!=oid)
          ||!SamePhysicalDirectory(rootValue.GetString()!,root)||!SamePhysicalDirectory(targetValue.GetString()!,target))
            throw new IOException($"工作目录意图记录与当前目标不一致，禁止重放：{path}");
    }
    private static void EnsureCompletedTargetIdentity(LaunchItem completed,string target)
    {
        var live=PhysicalDirectory.Resolve(target);
        if(!StringComparer.Ordinal.Equals(completed.Workspace.Key,live.Key)||!StringComparer.OrdinalIgnoreCase.Equals(completed.Workspace.Path,live.Path))
            throw new IOException("已准备目录物理身份与准备记录不一致，禁止覆盖记录。请恢复原目录，或改用“使用已准备目录”。");
    }
    private static void EnsureCompletedCwdIdentity(LaunchItem completed,string mapped)
    {
        var live=PhysicalDirectory.Resolve(mapped).Path;
        if(!StringComparer.OrdinalIgnoreCase.Equals(completed.Cwd,live))throw new IOException("准备记录中的工作目录与当前准备目录不一致，禁止覆盖记录。");
    }
    private async Task EnsureLinkedWorktreeAsync(string root,string target,string branch,string oid,CancellationToken ct)
    {
        var rootCommon=await GitCommonDirectoryAsync(root,ct);var targetCommon=await GitCommonDirectoryAsync(target,ct);
        if(rootCommon!=targetCommon)throw new IOException("已准备目录不是该仓库登记的 Git 工作树，禁止复用或覆盖准备记录。");
        if(!WorktreeListed(await Git(root,["worktree","list","--porcelain"],ct),target,branch,oid))throw new IOException("已准备目录未登记在该仓库的 Git 工作树列表中，禁止复用或覆盖准备记录。");
    }
    private async Task<string> GitCommonDirectoryAsync(string cwd,CancellationToken ct)
    {
        var raw=(await Git(cwd,["rev-parse","--git-common-dir"],ct)).Trim();
        if(raw.Length==0)throw new IOException("无法解析 Git 公共目录。");
        string full;try{full=Path.GetFullPath(raw.Replace('/',Path.DirectorySeparatorChar),Path.GetFullPath(cwd));}catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){throw new IOException("Git 公共目录路径无效。",e);}
        if(!Directory.Exists(full))throw new IOException($"Git 公共目录不可用：{full}");
        return PhysicalDirectory.Resolve(full).Key;
    }
    private static bool WorktreeListed(string output,string target,string branch,string oid)
    {
        string? path=null,head=null,refName=null;
        foreach(var raw in output.Split('\n'))
        {
            var line=raw.TrimEnd('\r');
            if(line.Length==0)
            {
                if(path!=null&&head==oid&&refName=="refs/heads/"+branch&&SamePath(path,target))return true;
                path=null;head=null;refName=null;continue;
            }
            if(line.StartsWith("worktree ",StringComparison.Ordinal))path=line[9..];
            else if(line.StartsWith("HEAD ",StringComparison.Ordinal))head=line[5..];
            else if(line.StartsWith("branch ",StringComparison.Ordinal))refName=line[7..];
        }
        return path!=null&&head==oid&&refName=="refs/heads/"+branch&&SamePath(path,target);
    }
    private static bool SamePath(string left,string right)
    {
        try
        {
            var a=Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Replace('/',Path.DirectorySeparatorChar)));var b=Path.TrimEndingDirectorySeparator(Path.GetFullPath(right.Replace('/',Path.DirectorySeparatorChar)));
            if(StringComparer.OrdinalIgnoreCase.Equals(a,b))return true;
            if(Directory.Exists(a)&&Directory.Exists(b))return PhysicalDirectory.Resolve(a).Key==PhysicalDirectory.Resolve(b).Key;
        }
        catch(Exception e)when(e is not OutOfMemoryException){}
        return false;
    }
    private static bool SamePhysicalDirectory(string left,string right)
    {
        try{return Directory.Exists(left)&&Directory.Exists(right)&&PhysicalDirectory.Resolve(left).Key==PhysicalDirectory.Resolve(right).Key;}
        catch(Exception e)when(e is not OutOfMemoryException){return false;}
    }
    private static bool SameOrWithin(string path,string root)
    {
        var candidate=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix=parent.EndsWith(Path.DirectorySeparatorChar)?parent:parent+Path.DirectorySeparatorChar;
        return StringComparer.OrdinalIgnoreCase.Equals(candidate,parent)||candidate.StartsWith(prefix,StringComparison.OrdinalIgnoreCase);
    }
    private async Task<string> Git(string cwd,string[] args,CancellationToken ct)
    {
        var result=await ControlledCommand.RunAsync(new(git,args,"exe"),cwd,ct);
        if(result.Code!=0)throw new IOException($"Git 准备失败（{result.Code}）：{result.Error.Trim()}");return result.Output;
    }
}
