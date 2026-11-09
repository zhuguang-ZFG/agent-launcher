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
        var item=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"Git 准备",cwd,PhysicalDirectory.Workspace(cwd),false,command,ResourcePolicy.GitBudget,"git");
        var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),[item]),ct);if(!reserve.Ok)throw new IOException(reserve.Message);
        Process? process=null;
        try
        {
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            start.ArgumentList.Add("run");start.ArgumentList.Add(item.Id.ToString());process=Process.Start(start)??throw new IOException("无法运行受控准备程序。");process.StandardInput.Close();
            var output=outputLine==null?ReadBoundedAsync(process.StandardOutput):ReadLinesAsync(process.StandardOutput,outputLine);var error=ReadBoundedAsync(process.StandardError);
            // Cancellation stops subsequent operations. An in-progress Git write is allowed to finish.
            await process.WaitForExitAsync();var result=new CommandResult(process.ExitCode,await output,await error);
            await Ipc.SendAsync(new("status",Guid.NewGuid()));ct.ThrowIfCancellationRequested();return result;
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
    public async Task<List<LaunchItem>> PrepareAsync(Draft draft,Action<string> progress,Func<string,Task<bool>> confirmHead,CancellationToken ct)
    {
        draft.Validate();
        Directory.CreateDirectory(Store.Root);
        await using var preparationLock=new FileStream(Store.FilePath("preparation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var active=draft.Slots.Where(s=>s.Enabled).ToArray();if(active.Length==0)throw new IOException("先勾选本次启动项。");
        var batch=Guid.NewGuid();var plans=new List<LaunchItem>();
        foreach(var slot in active)
        {
            ct.ThrowIfCancellationRequested();var cwd=PhysicalDirectory.Resolve(string.IsNullOrWhiteSpace(slot.Directory)?draft.Project:slot.Directory).Path;
            var position=draft.Slots.IndexOf(slot)+1;
            plans.Add(new(Guid.NewGuid(),batch,position,Agents.Get(slot.AgentId).Name,cwd,PhysicalDirectory.Workspace(cwd),draft.DirectoryMode=="shared",Commands.Resolve(slot.AgentId,slot.Current),ResourcePolicy.AgentBudget));
        }
        var canonical=draft.Copy();canonical.Revision=0;
        var digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical,Store.Json)));
        var preparationPath=Store.FilePath("preparations/"+digest+".json");
        var record=Store.Read<PreparationRecord>(preparationPath);List<LaunchItem>? effective=null;
        if(record!=null)
        {
            if(record.SchemaVersion is <0 or >1)throw new IOException($"准备记录版本不支持，未改写文件：{preparationPath}");
            if(record.Digest!=digest)throw new IOException("准备记录摘要不符，禁止重放。");
            if(!StructurallyValid(record,plans))throw new IOException($"准备记录损坏，禁止重放：{preparationPath}");
            var repaired=false;
            if(record.Original.Any(p=>!File.Exists(p.Command.Executable))||record.Completed.Values.Any(p=>!File.Exists(p.Command.Executable)))
            {
                // The draft digest still matches, so slot order is authoritative; keep worktree mapping and re-resolve only commands.
                for(var i=0;i<record.Original.Count;i++){var plan=record.Original[i];var slot=draft.Slots[plan.Position-1];record.Original[i]=plan with{Command=Commands.Resolve(slot.AgentId,slot.Current)};}
                foreach(var id in record.Completed.Keys.ToArray())record.Completed[id]=record.Completed[id] with{Command=record.Original.Single(p=>p.Id==id).Command};
                repaired=true;
            }
            if(record.SchemaVersion!=1||repaired){record.SchemaVersion=1;Store.Save(preparationPath,record);}
            effective=record.Original.Select(p=>record.Completed.GetValueOrDefault(p.Id)??p).ToList();
        }
        if(record==null){record=new(){SchemaVersion=1,Digest=digest,Original=plans};Store.Save(preparationPath,record);}
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
                ct.ThrowIfCancellationRequested();var target=Path.Combine(Store.Root,"worktrees",batch.ToString("N"),"agent-"+plan.Position);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);var drive=new DriveInfo(Path.GetPathRoot(target)!);
                var reserve=Math.Max(2*ResourcePolicy.GiB,(ulong)drive.TotalSize/20);
                if((ulong)drive.AvailableFreeSpace<reserve+estimated)throw new IOException("目标盘空间不足以保留余量和检出文件。");
                var branch=$"launcher/{batch:N}/{plan.Position}";
                var workspacePath=Store.FilePath($"workspaces/{plan.Id:N}.json");
                var known=File.Exists(workspacePath);
                if(!known)Store.Save(workspacePath,new{State="Intent",Root=root,Target=target,Branch=branch,Oid=oid});
                progress($"正在准备/核对第 {plan.Position} 项独立目录 · {oid[..8]}。取消只停止后续步骤，已创建目录保留。");
                if(!Directory.Exists(target))
                {
                    if(known)throw new IOException("上次准备已中断且目录未找到。请核对工作目录记录；不会自动重复创建或删除分支。");
                    await Git(root,["worktree","add","-b",branch,target,oid],ct);
                }
                var actual=(await Git(target,["rev-parse","HEAD"],ct)).Trim();if(actual!=oid)throw new IOException("已有准备目录基线已变化，请改用“使用已准备目录”。");
                var actualBranch=(await Git(target,["symbolic-ref","--short","HEAD"],ct)).Trim();if(actualBranch!=branch)throw new IOException("已有目录分支不匹配，禁止复用。");
                var relative=Path.GetRelativePath(root,plan.Cwd);var mapped=relative=="."?target:Path.Combine(target,relative);
                if(!Directory.Exists(mapped))throw new IOException("基线不包含原子目录；已准备目录保留，请选择仓库根目录重试。");
                var replacement=plan with{Cwd=PhysicalDirectory.Resolve(mapped).Path,Workspace=PhysicalDirectory.Workspace(mapped)};
                plans[plans.FindIndex(p=>p.Id==plan.Id)]=replacement;record.Completed[plan.Id]=replacement;Store.Save(preparationPath,record);
                Store.Save(Store.FilePath($"workspaces/{plan.Id:N}.json"),new{State="Prepared",Root=root,Target=target,Branch=branch,Oid=oid});
            }
        }
        // Prepared directories are reusable; launch authorization IDs are always fresh.
        var launchBatch=Guid.NewGuid();return plans.Select(p=>p with{Id=Guid.NewGuid(),BatchId=launchBatch}).ToList();
    }
    private async Task<string> Git(string cwd,string[] args,CancellationToken ct)
    {
        var result=await ControlledCommand.RunAsync(new(git,args,"exe"),cwd,ct);
        if(result.Code!=0)throw new IOException($"Git 准备失败（{result.Code}）：{result.Error.Trim()}");return result.Output;
    }
}
