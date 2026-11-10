using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

// Workspace (CR02/CR12) regressions. Call RunAsync(Test,cwd) from Integration.RunAsync after the
// existing F09 preparation scenarios: earlier integration cases use .Single() over the preparation
// directory and would see this helper's records. Each test removes its own preparation/workspace
// records on exit; worktrees remain under the isolated test root by product policy.
internal static class WorkspaceReviewChecks
{
    public static async Task RunAsync(Func<string,Func<Task>,Task> test,string cwd)
    {
        if(!Store.Root.Contains("AgentLauncher-tests-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("工作区回归需要在隔离测试根下运行。");
        var git=Commands.FindExe("git.exe");
        if(git is not {Length:>0}){await test("CR02/CR12 workspace review needs Git",()=>throw new InvalidOperationException("未找到 Git，无法运行工作区回归。"));return;}
        var gitPath=git;
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        async Task<CommandResult> GitRaw(string directory,params string[] arguments)=>await ControlledCommand.RunAsync(new(gitPath,arguments,"exe"),directory);
        async Task<string> GitChecked(string directory,params string[] arguments){var result=await GitRaw(directory,arguments);if(result.Code!=0)throw new InvalidOperationException($"git {arguments[0]} 失败：{result.Error.Trim()}");return result.Output;}
        async Task NewRepository(string path){Directory.CreateDirectory(path);await GitChecked(path,"init");await File.WriteAllTextAsync(Path.Combine(path,"hello.txt"),"baseline");await GitChecked(path,"add","hello.txt");await GitChecked(path,"-c","user.name=Launcher Test","-c","user.email=test@example.invalid","commit","-m","temporary fixture");}
        Draft Isolated(string project,IReadOnlyList<int> positions,string? directDirectory=null,int directIndex=-1)
        {
            var draft=new Draft{Project=project,DirectoryMode="isolated"};draft.Slots.ForEach(s=>s.Enabled=false);
            foreach(var index in positions)
            {
                var slot=draft.Slots[index];slot.Enabled=true;if(index==directIndex)slot.Directory=directDirectory??"";
                slot.Profiles[slot.AgentId]=new(){Executable=Environment.ProcessPath!,Arguments=["--hold","1"]};
            }
            return draft;
        }
        string OwnedRoot(){var root=Path.GetFullPath(Path.Combine(cwd,"AgentLauncher-tests-workspace-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);return root;}
        HashSet<string> PreparationFiles()=>Directory.Exists(Store.FilePath("preparations"))?Directory.GetFiles(Store.FilePath("preparations"),"*.json").ToHashSet():[];
        HashSet<string> WorkspaceFiles()=>Directory.Exists(Store.FilePath("workspaces"))?Directory.GetFiles(Store.FilePath("workspaces"),"*.json").ToHashSet():[];
        byte[] Bytes(string path)=>File.Exists(path)?File.ReadAllBytes(path):[];
        string PrepPath(HashSet<string> before)=>Directory.GetFiles(Store.FilePath("preparations"),"*.json").Single(path=>!before.Contains(path));
        async Task TestWithCleanup(string name,Func<Task> action)
        {
            var beforePreparation=PreparationFiles();var beforeWorkspaces=WorkspaceFiles();
            try{await test(name,action);}
            finally
            {
                foreach(var path in PreparationFiles().Except(beforePreparation).ToArray()){try{File.Delete(path);File.Delete(path+".bak");}catch(Exception e)when(e is not OutOfMemoryException){}}
                foreach(var path in WorkspaceFiles().Except(beforeWorkspaces).ToArray()){try{File.Delete(path);File.Delete(path+".bak");}catch(Exception e)when(e is not OutOfMemoryException){}}
            }
        }

        await TestWithCleanup("CR02 unchanged linked worktree reuse and command refresh",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var draft=Isolated(repo,[0,1]);var before=PreparationFiles();
            var plans=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            var prepPath=PrepPath(before);
            var replay=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(replay.Select(p=>p.Cwd).SequenceEqual(plans.Select(p=>p.Cwd)),"有效复用改变了准备目录");
            Assert(!replay.Select(p=>p.Id).Intersect(plans.Select(p=>p.Id)).Any(),"有效复用重放了启动授权 ID");
            Assert(plans.Count==2&&plans[0].Workspace.Key!=plans[1].Workspace.Key,"准备目录未保持独立");
            var missing=Path.Combine(Store.Root,"missing-node-"+Guid.NewGuid().ToString("N")+".exe");
            var record=Store.Read<PreparationRecord>(prepPath)!;
            record.Original[0]=record.Original[0] with{Command=new(missing,record.Original[0].Command.Arguments,"exe")};Store.Save(prepPath,record);
            var healed=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(healed.Select(p=>p.Cwd).SequenceEqual(plans.Select(p=>p.Cwd)),"命令刷新丢失准备目录映射");
            var healedRecord=Store.Read<PreparationRecord>(prepPath)!;
            Assert(healedRecord.Original[0].Command.Executable!=missing&&File.Exists(healedRecord.Original[0].Command.Executable),"准备记录命令未重新解析");
        });

        await TestWithCleanup("CR02 replaced prepared directory rejected without record rewrite",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var draft=Isolated(repo,[0,1]);var before=PreparationFiles();
            var plans=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            var prepPath=PrepPath(before);var record=Store.Read<PreparationRecord>(prepPath)!;
            // Replace the later target so a per-plan rewrite of the earlier completed entry would be observable.
            var victim=record.Original[1];var prepared=plans[1].Cwd;var parked=prepared+"-parked-"+Guid.NewGuid().ToString("N");
            var clone=Path.Combine(root,"clone");
            var oid=record.Baselines[victim.Workspace.Key];var branch=$"launcher/{record.Original[0].BatchId:N}/{victim.Position}";
            Directory.Move(prepared,parked);
            await GitChecked(root,"clone","--no-hardlinks",repo,clone);
            await GitChecked(clone,"checkout","-b",branch,oid);
            Directory.Move(clone,prepared);
            var prepBytes=Bytes(prepPath);var backupBytes=Bytes(prepPath+".bak");
            var workspacePath=Store.FilePath($"workspaces/{victim.Id:N}.json");var workspaceBytes=Bytes(workspacePath);
            var identity=record.Completed[victim.Id].Workspace;
            IOException? rejected=null;
            try{await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);}catch(IOException e){rejected=e;}
            Assert(rejected!=null&&rejected.Message.Contains("物理身份"),"替换后的准备目录未被身份校验拒绝："+(rejected?.Message??"成功复用"));
            Assert(Bytes(prepPath).SequenceEqual(prepBytes),"拒绝后准备记录被改写");
            Assert(Bytes(prepPath+".bak").SequenceEqual(backupBytes),"拒绝后准备备份被改写");
            Assert(Bytes(workspacePath).SequenceEqual(workspaceBytes),"拒绝后工作目录记录被改写");
            Assert(Store.Read<PreparationRecord>(prepPath)!.Completed[victim.Id].Workspace==identity,"拒绝后完成项物理身份被覆盖");
            Assert(PhysicalDirectory.Resolve(prepared).Key!=identity.Key,"替换目录保留了原物理身份，夹具无效");
        });

        await TestWithCleanup("CR02 crash intent recovery adopts the registered linked worktree",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var draft=Isolated(repo,[0,1]);var before=PreparationFiles();
            var plans=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            var prepPath=PrepPath(before);var record=Store.Read<PreparationRecord>(prepPath)!;var victim=record.Original[0];
            var branch=$"launcher/{record.Original[0].BatchId:N}/{victim.Position}";var oid=record.Baselines[victim.Workspace.Key];
            record.Completed.Remove(victim.Id);Store.Save(prepPath,record);
            Store.Save(Store.FilePath($"workspaces/{victim.Id:N}.json"),new{State="Intent",Root=victim.Workspace.Path,Target=plans[0].Cwd,Branch=branch,Oid=oid});
            var worktreesBefore=(await GitChecked(repo,"worktree","list","--porcelain")).Split('\n').Count(line=>line.StartsWith("worktree ",StringComparison.Ordinal));
            var healed=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(healed.Single(p=>p.Position==victim.Position).Cwd==plans[0].Cwd,"意图恢复改变了准备目录");
            var restored=Store.Read<PreparationRecord>(prepPath)!;
            Assert(restored.Completed.TryGetValue(victim.Id,out var completed)&&completed.Workspace.Key==PhysicalDirectory.Resolve(plans[0].Cwd).Key,"意图恢复未重建完成身份");
            var worktreesAfter=(await GitChecked(repo,"worktree","list","--porcelain")).Split('\n').Count(line=>line.StartsWith("worktree ",StringComparison.Ordinal));
            Assert(worktreesBefore==worktreesAfter,"意图恢复重复创建了工作树");
            var state=Store.Read<Dictionary<string,JsonElement>>(Store.FilePath($"workspaces/{victim.Id:N}.json"))!;
            Assert(state["State"].GetString()=="Prepared","意图恢复未写回 Prepared");
        });

        await TestWithCleanup("CR12 batch target overlap rejected before Git or preparation writes",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var draft=Isolated(repo,[0,1,2],destination,2);
            var beforePreparation=PreparationFiles();var beforeWorkspaces=WorkspaceFiles();
            var beforeWorktrees=await GitChecked(repo,"worktree","list","--porcelain");
            var beforeBranches=await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher");
            var beforeDestination=Directory.GetFileSystemEntries(destination).Order().ToArray();
            IOException? rejected=null;
            try{await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);}catch(IOException e){rejected=e;}
            Assert(rejected!=null&&rejected.Message.Contains("重叠"),"同批目标与直接目录重叠未被预检拒绝："+(rejected?.Message??"准备成功"));
            Assert(PreparationFiles().SetEquals(beforePreparation),"拒绝后新增了准备记录");
            Assert(WorkspaceFiles().SetEquals(beforeWorkspaces),"拒绝后新增了工作目录记录");
            Assert(await GitChecked(repo,"worktree","list","--porcelain")==beforeWorktrees,"拒绝后 Git 工作树发生变化");
            Assert(await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher")==beforeBranches,"拒绝后 Git 分支发生变化");
            Assert(Directory.GetFileSystemEntries(destination).Order().SequenceEqual(beforeDestination),"拒绝后目标目录出现写入");
        });

        await TestWithCleanup("CR12 sibling directory under the destination remains valid",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var sibling=Path.Combine(destination,"sibling");Directory.CreateDirectory(sibling);
            var draft=Isolated(repo,[0,1,2],sibling,2);
            var plans=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(plans.Count==3,"兄弟目录场景丢失启动项");
            var prepared=plans.Where(p=>p.Position!=3).ToArray();
            Assert(prepared.All(p=>p.Cwd.StartsWith(PhysicalDirectory.Resolve(destination).Path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)),"独立目标未落在自定义根");
            Assert(plans.Single(p=>p.Position==3).Cwd==PhysicalDirectory.Resolve(sibling).Path,"直接兄弟目录被改写");
            Assert(prepared[0].Workspace.Key!=prepared[1].Workspace.Key,"独立目标未分离");
        });
    }
}
