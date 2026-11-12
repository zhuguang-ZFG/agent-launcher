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
                var slot=draft.Slots[index];slot.Enabled=true;if(index==directIndex){slot.Directory=directDirectory??"";if(!string.IsNullOrWhiteSpace(slot.Directory))Directory.CreateDirectory(slot.Directory);}
                slot.Profiles[slot.AgentId]=new(){Executable=Environment.ProcessPath!,Arguments=["--hold","1"]};
            }
            return draft;
        }
        string OwnedRoot(){var root=Path.GetFullPath(Path.Combine(Store.Root,"AgentLauncher-tests-workspace-"+Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);return root;}
        HashSet<string> PreparationFiles()=>Directory.Exists(Store.FilePath("preparations"))?Directory.GetFiles(Store.FilePath("preparations"),"*.json").ToHashSet():[];
        HashSet<string> WorkspaceFiles()=>Directory.Exists(Store.FilePath("workspaces"))?Directory.GetFiles(Store.FilePath("workspaces"),"*.json").ToHashSet():[];
        byte[] Bytes(string path)=>File.Exists(path)?File.ReadAllBytes(path):[];
        string PrepPath(HashSet<string> before)=>Directory.GetFiles(Store.FilePath("preparations"),"*.json").Single(path=>!before.Contains(path));
        bool SameDirectory(string left,string right)
        {
            var a=Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Replace('/',Path.DirectorySeparatorChar)));var b=Path.TrimEndingDirectorySeparator(Path.GetFullPath(right.Replace('/',Path.DirectorySeparatorChar)));
            if(StringComparer.OrdinalIgnoreCase.Equals(a,b))return true;
            return Directory.Exists(a)&&Directory.Exists(b)&&PhysicalDirectory.Resolve(a).Key==PhysicalDirectory.Resolve(b).Key;
        }
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

        await TestWithCleanup("ACC-S05 sparse fifth and sixteenth slots reuse prepared directories",async()=>
        {
            var root=OwnedRoot();
            foreach(var position in new[]{5,16})
            {
                var project=Path.Combine(root,"slot-"+position);Directory.CreateDirectory(project);
                var draft=new Draft{Project=project,DirectoryMode="shared",Slots=Enumerable.Range(0,16).Select(_=>new Slot()).ToList()};
                var slot=draft.Slots[position-1];slot.Enabled=true;slot.Profiles[slot.AgentId]=new(){Executable=Environment.ProcessPath!,Arguments=["--hold","1"]};
                var first=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),default);
                var second=await new WorkspacePreparer().PrepareAsync(draft,_=>{},_=>Task.FromResult(false),default);
                Assert(first.Count==1&&second.Count==1&&second[0].Position==position&&second[0].SourceSlotId==slot.Id&&second[0].Cwd==first[0].Cwd&&second[0].Id!=first[0].Id,"sparse high slot replay lost identity or reused attempt");
            }
        });

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
            var sibling=Path.Combine(root,"sibling");Directory.CreateDirectory(sibling);
            var draft=Isolated(repo,[0,1,2],sibling,2);
            var plans=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(plans.Count==3,$"兄弟目录场景丢失启动项：实际 {plans.Count}，位置 {string.Join(",",plans.Select(p=>p.Position))}，CWD {string.Join(" | ",plans.Select(p=>p.Cwd))}");
            var prepared=plans.Where(p=>p.Position!=3).ToArray();
            Assert(prepared.All(p=>p.Cwd.StartsWith(PhysicalDirectory.Resolve(destination).Path+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)),"独立目标未落在自定义根");
            Assert(plans.Single(p=>p.Position==3).Cwd==PhysicalDirectory.Resolve(sibling).Path,"直接兄弟目录被改写");
            Assert(prepared[0].Workspace.Key!=prepared[1].Workspace.Key,"独立目标未分离");
        });
        await TestWithCleanup("CR12 active session workspace also blocks the batch before Git writes",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var draft=Isolated(repo,[0,1]);
            // An earlier single-slot launch may point straight at the prepared-target root; the monitor refuses that overlap at reserve,
            // which is after the worktree, branch and preparation record exist. The preflight must refuse it before any write.
            var active=PhysicalDirectory.Workspace(destination);
            var beforePreparation=PreparationFiles();var beforeWorkspaces=WorkspaceFiles();
            var beforeWorktrees=await GitChecked(repo,"worktree","list","--porcelain");
            var beforeBranches=await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher");
            var beforeDestination=Directory.GetFileSystemEntries(destination).Order().ToArray();
            IOException? rejected=null;
            try{await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,new[]{active});}catch(IOException e){rejected=e;}
            Assert(rejected!=null&&rejected.Message.Contains("重叠"),"运行中会话占用的目标未被预检拒绝："+(rejected?.Message??"准备成功"));
            Assert(PreparationFiles().SetEquals(beforePreparation),"拒绝后新增了准备记录");
            Assert(WorkspaceFiles().SetEquals(beforeWorkspaces),"拒绝后新增了工作目录记录");
            Assert(await GitChecked(repo,"worktree","list","--porcelain")==beforeWorktrees,"拒绝后 Git 工作树发生变化");
            Assert(await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher")==beforeBranches,"拒绝后 Git 分支发生变化");
            Assert(Directory.GetFileSystemEntries(destination).Order().SequenceEqual(beforeDestination),"拒绝后目标目录出现写入");
            var unrelatedActive=Path.Combine(root,"unrelated-active");Directory.CreateDirectory(unrelatedActive);
            var disjoint=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,new[]{PhysicalDirectory.Workspace(unrelatedActive)});
            Assert(disjoint.Count==2&&disjoint[0].Workspace.Key!=disjoint[1].Workspace.Key,"不重叠的活动会话工作区被误拒");
        });

        // Quick launch forces one frozen input into its own linked worktree even when it is the only item for that source.
        await TestWithCleanup("QQ forced single slot prepares a registered worktree and replays it",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var draft=Isolated(repo,[0]);
            var source=PhysicalDirectory.Resolve(repo).Path;var sourceHead=await GitChecked(repo,"rev-parse","HEAD");
            var sourceStatus=await GitChecked(repo,"status","--porcelain");
            var sourceEntries=Directory.GetFileSystemEntries(repo).Select(Path.GetFileName).Order().ToArray();
            var beforePreparation=PreparationFiles();var beforeWorkspaces=WorkspaceFiles();
            var plans=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true);
            Assert(plans.Count==1&&plans[0].Position==1,"强制独立目录没有只准备唯一启动项");
            var target=Path.GetFullPath(plans[0].Cwd);
            Assert(!SameDirectory(target,source),"强制独立目录仍在原始源目录");
            Assert(target.StartsWith(Path.GetFullPath(destination)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"强制独立目录未落在目标根："+target);
            Assert(!StringComparer.Ordinal.Equals(plans[0].Workspace.Key,PhysicalDirectory.Resolve(repo).Key),"强制独立目录复用了源工作区身份");
            var common=await GitChecked(target,"rev-parse","--git-common-dir");
            Assert(SameDirectory(Path.GetFullPath(common.Trim().Replace('/',Path.DirectorySeparatorChar),target),Path.Combine(repo,".git")),"强制独立目录不是源仓库的工作树");
            var listing=await GitChecked(repo,"worktree","list","--porcelain");
            Assert(listing.Split('\n').Any(line=>line.StartsWith("worktree ",StringComparison.Ordinal)&&SameDirectory(line[9..].TrimEnd('\r'),target)),"强制独立目录未登记在源仓库工作树列表");
            Assert(await GitChecked(target,"rev-parse","HEAD")==sourceHead,"强制独立目录基线不是源 HEAD");
            Assert((await GitChecked(target,"symbolic-ref","--short","HEAD")).StartsWith("launcher/",StringComparison.Ordinal),"强制独立目录分支未使用 launcher 前缀");
            Assert(await GitChecked(repo,"status","--porcelain")==sourceStatus,"强制准备改动了源工作区");
            Assert(await GitChecked(repo,"rev-parse","HEAD")==sourceHead,"强制准备改动了源 HEAD");
            Assert(Directory.GetFileSystemEntries(repo).Select(Path.GetFileName).Order().SequenceEqual(sourceEntries),"强制准备在源目录新增或删除了条目");
            Assert(PreparationFiles().Count==beforePreparation.Count+1&&WorkspaceFiles().Count==beforeWorkspaces.Count+1,"强制准备未各自写入准备和工作目录记录");
            var replay=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true);
            Assert(replay.Count==1&&SameDirectory(replay[0].Cwd,target),"同一强制输入改变了准备目录");
            Assert(replay[0].Id!=plans[0].Id&&replay[0].BatchId!=plans[0].BatchId,"同一强制输入复用了启动授权 ID");
            Assert(PreparationFiles().Count==beforePreparation.Count+1&&WorkspaceFiles().Count==beforeWorkspaces.Count+1,"强制重放新增了准备或工作目录记录");
            var unforced=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None);
            Assert(unforced.Count==1&&SameDirectory(unforced[0].Cwd,source),"默认准备路径不再停留在原始源目录");
            Assert(PreparationFiles().Count==beforePreparation.Count+2&&WorkspaceFiles().Count==beforeWorkspaces.Count+1,"强制与默认准备摘要未分离");
            var forcedAgain=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true);
            Assert(forcedAgain.Count==1&&SameDirectory(forcedAgain[0].Cwd,target)&&PreparationFiles().Count==beforePreparation.Count+2,"强制记录被默认准备覆盖或重复");
        });

        await TestWithCleanup("QQ sequential forced single slots keep distinct worktrees",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var source=PhysicalDirectory.Resolve(repo).Path;var sourceStatus=await GitChecked(repo,"status","--porcelain");
            var sourceEntries=Directory.GetFileSystemEntries(repo).Select(Path.GetFileName).Order().ToArray();
            var beforePreparation=PreparationFiles();
            var first=await new WorkspacePreparer(destination).PrepareAsync(Isolated(repo,[0]),_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true);
            var second=await new WorkspacePreparer(destination).PrepareAsync(Isolated(repo,[1]),_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true);
            Assert(first.Count==1&&second.Count==1&&first[0].Position!=second[0].Position,"顺序强制输入丢失位置身份");
            Assert(!SameDirectory(first[0].Cwd,second[0].Cwd),"两个顺序任务复用了同一工作树");
            Assert(!SameDirectory(first[0].Cwd,source)&&!SameDirectory(second[0].Cwd,source),"顺序任务有运行在原始源目录的一项");
            var sourceKey=PhysicalDirectory.Resolve(repo).Key;
            Assert(first[0].Workspace.Key!=second[0].Workspace.Key&&first[0].Workspace.Key!=sourceKey&&second[0].Workspace.Key!=sourceKey,"顺序任务未获得独立工作区身份");
            Assert(PreparationFiles().Count==beforePreparation.Count+2,"顺序强制输入未各自写入准备记录");
            var listing=await GitChecked(repo,"worktree","list","--porcelain");
            foreach(var plan in new[]{first[0],second[0]})Assert(listing.Split('\n').Any(line=>line.StartsWith("worktree ",StringComparison.Ordinal)&&SameDirectory(line[9..].TrimEnd('\r'),plan.Cwd)),"顺序强制目录未登记工作树："+plan.Cwd);
            Assert(File.ReadAllText(Path.Combine(first[0].Cwd,"hello.txt"))=="baseline"&&File.ReadAllText(Path.Combine(second[0].Cwd,"hello.txt"))=="baseline","顺序工作树内容不是基线");
            Assert(await GitChecked(repo,"status","--porcelain")==sourceStatus,"顺序强制准备改动了源工作区");
            Assert(Directory.GetFileSystemEntries(repo).Select(Path.GetFileName).Order().SequenceEqual(sourceEntries),"顺序强制准备在源目录新增或删除了条目");
        });

        await TestWithCleanup("QQ forced target collision with an active workspace rejected before writes",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var draft=Isolated(repo,[0]);
            var sourceHead=await GitChecked(repo,"rev-parse","HEAD");
            var beforePreparation=PreparationFiles();var beforeWorkspaces=WorkspaceFiles();
            var beforeWorktrees=await GitChecked(repo,"worktree","list","--porcelain");
            var beforeBranches=await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher");
            var beforeDestination=Directory.GetFileSystemEntries(destination).Order().ToArray();
            IOException? rejected=null;
            try{await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,new[]{PhysicalDirectory.Workspace(destination)},null,true);}catch(IOException e){rejected=e;}
            Assert(rejected!=null&&rejected.Message.Contains("重叠"),"强制目标与运行中会话重叠未被预检拒绝："+(rejected?.Message??"准备成功"));
            Assert(PreparationFiles().SetEquals(beforePreparation),"拒绝后新增了准备记录");
            Assert(WorkspaceFiles().SetEquals(beforeWorkspaces),"拒绝后新增了工作目录记录");
            Assert(await GitChecked(repo,"worktree","list","--porcelain")==beforeWorktrees,"拒绝后 Git 工作树发生变化");
            Assert(await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher")==beforeBranches,"拒绝后 Git 分支发生变化");
            Assert(Directory.GetFileSystemEntries(destination).Order().SequenceEqual(beforeDestination),"拒绝后目标目录出现写入");
            Assert(await GitChecked(repo,"rev-parse","HEAD")==sourceHead,"拒绝后源 HEAD 变化");
            var unrelated=Path.Combine(root,"unrelated-active");Directory.CreateDirectory(unrelated);
            var allowed=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,new[]{PhysicalDirectory.Workspace(unrelated)},null,true);
            Assert(allowed.Count==1&&!SameDirectory(allowed[0].Cwd,PhysicalDirectory.Resolve(repo).Path),"不重叠的活动会话阻止了强制独立目录");
        });

        await TestWithCleanup("QQ forced isolation rejects multi-slot and non-isolated drafts before writes",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var shared=Isolated(repo,[0]);shared.DirectoryMode="shared";
            var beforePreparation=PreparationFiles();var beforeWorkspaces=WorkspaceFiles();
            var beforeWorktrees=await GitChecked(repo,"worktree","list","--porcelain");
            var beforeDestination=Directory.GetFileSystemEntries(destination).Order().ToArray();
            foreach(var draft in new[]{Isolated(repo,[0,1]),shared})
            {
                IOException? refused=null;
                try{await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true);}catch(IOException e){refused=e;}
                Assert(refused!=null,"强制独立目录接受了不合法草稿："+draft.DirectoryMode+"（启用 "+draft.Slots.Count(s=>s.Enabled)+" 项）");
            }
            Assert(PreparationFiles().SetEquals(beforePreparation),"非法强制草稿新增了准备记录");
            Assert(WorkspaceFiles().SetEquals(beforeWorkspaces),"非法强制草稿新增了工作目录记录");
            Assert(await GitChecked(repo,"worktree","list","--porcelain")==beforeWorktrees,"非法强制草稿创建了工作树");
            Assert(Directory.GetFileSystemEntries(destination).Order().SequenceEqual(beforeDestination),"非法强制草稿写入了目标目录");
        });

        await TestWithCleanup("QQ expected source workspace key blocks a replaced source before writes",async()=>{
            var root=OwnedRoot();var repo=Path.Combine(root,"source");await NewRepository(repo);
            var destination=Path.Combine(root,"destination");Directory.CreateDirectory(destination);
            var draft=Isolated(repo,[0]);var expected=PhysicalDirectory.Resolve(repo).Key;
            var parked=repo+"-parked-"+Guid.NewGuid().ToString("N");Directory.Move(repo,parked);await NewRepository(repo);
            var replaced=PhysicalDirectory.Resolve(repo).Key;
            Assert(!StringComparer.Ordinal.Equals(expected,replaced),"源目录替换后物理身份未变化，夹具无效");
            var beforePreparation=PreparationFiles();var beforeWorkspaces=WorkspaceFiles();
            var beforeWorktrees=await GitChecked(repo,"worktree","list","--porcelain");
            var beforeBranches=await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher");
            var beforeDestination=Directory.GetFileSystemEntries(destination).Order().ToArray();
            IOException? rejected=null;
            try{await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true,expected);}catch(IOException e){rejected=e;}
            Assert(rejected!=null&&rejected.Message.Contains("物理身份"),"替换后的源工作区未被身份核对拒绝："+(rejected?.Message??"准备成功"));
            Assert(PreparationFiles().SetEquals(beforePreparation),"身份拒绝后新增了准备记录");
            Assert(WorkspaceFiles().SetEquals(beforeWorkspaces),"身份拒绝后新增了工作目录记录");
            Assert(await GitChecked(repo,"worktree","list","--porcelain")==beforeWorktrees,"身份拒绝后 Git 工作树发生变化");
            Assert(await GitChecked(repo,"for-each-ref","--format=%(refname)","refs/heads/launcher")==beforeBranches,"身份拒绝后 Git 分支发生变化");
            Assert(Directory.GetFileSystemEntries(destination).Order().SequenceEqual(beforeDestination),"身份拒绝后目标目录出现写入");
            var accepted=await new WorkspacePreparer(destination).PrepareAsync(draft,_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true,replaced);
            Assert(accepted.Count==1&&!SameDirectory(accepted[0].Cwd,PhysicalDirectory.Resolve(repo).Path),"匹配的源身份被拒绝或仍在原始源目录");
            IOException? multi=null;
            try{await new WorkspacePreparer(destination).PrepareAsync(Isolated(repo,[0,1]),_=>{},_=>Task.FromResult(false),CancellationToken.None,null,null,true,PhysicalDirectory.Resolve(repo).Key);}catch(IOException e){multi=e;}
            Assert(multi!=null,"源身份核对接受了多个启用项");
        });
    }
}
