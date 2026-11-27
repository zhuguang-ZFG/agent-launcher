#if INTEGRATION_TESTS
using System.IO;
using System.Windows;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private void RunDailyUsabilityChecks(Action<string,bool> check)
    {
        var original=config;
        try
        {
            if(!Store.Root.Contains("AgentLauncher-tests-",StringComparison.Ordinal))throw new IOException("Isolated UI root required");
            string DirectoryFixture(string name)=>PhysicalDirectory.Resolve(Directory.CreateDirectory(Path.Combine(Store.Root,"change-review-daily",name)).FullName).Path;
            var source=DirectoryFixture("source");var worktreeOne=DirectoryFixture("worktree-one");var worktreeTwo=DirectoryFixture("worktree-two");var changedProject=DirectoryFixture("different-project");
            var fixture=Path.Combine(AppContext.BaseDirectory,"AgentLauncher.App.exe");var command=new CommandSpec(fixture,[],"exe");
            config=new Configuration();var slot=config.Draft.Slots[0];slot.Profiles[slot.AgentId]=new(){Executable=fixture};config.Draft.Project=source;
            var firstPlan=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"first",worktreeOne,PhysicalDirectory.Workspace(worktreeOne),false,command,ResourcePolicy.AgentBudget,SourceSlotId:slot.Id,AgentId:slot.AgentId);
            var latestPlan=firstPlan with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid(),Cwd=worktreeTwo,Workspace=PhysicalDirectory.Workspace(worktreeTwo)};
            var first=new ControlEntry{Plan=firstPlan,Digest=Store.Digest(firstPlan),Created=DateTimeOffset.UtcNow.AddMinutes(-1)};
            var latest=new ControlEntry{Plan=latestPlan,Digest=Store.Digest(latestPlan),Created=DateTimeOffset.UtcNow};
            var choices=ChangeWorkspaces(slot,[first,latest]);
            check("UX17 changes use actual attempted worktrees newest first",choices.Select(item=>item.Path).SequenceEqual(new[]{worktreeTwo,worktreeOne})&&choices.All(item=>item.WorkspaceKey==PhysicalDirectory.Workspace(item.Path).Key));
            config.Draft.Project=changedProject;
            check("UX17 changing project cannot redirect old attempt change review",ChangeWorkspaces(slot,[first])[0].Path==first.Plan.Cwd);
            check("UX17 unlaunched slot changes use current configured directory",ChangeWorkspaces(slot,[])[0].Path==config.Draft.Project);
            var quickBook=new TaskBook();var quickDraft=new Draft{Project=source,Slots=[slot.Copy()]};
            var imported=TaskBookOps.EnqueueQuickLaunch(quickBook,quickDraft,[new QuickLaunchItem(slot.Id,source,PhysicalDirectory.Workspace(source),command,AgentProbe.Fingerprint(command))],"");
            var quickEntry=quickBook.Queue.Single();
            var quickPlan=first.Plan with{Id=Guid.NewGuid(),SourceSlotId=imported.Single()};
            var quickControl=new ControlEntry{Plan=quickPlan,Digest=Store.Digest(quickPlan),Created=DateTimeOffset.UtcNow};
            var quickRun=TaskBookOps.StartRun(quickBook,quickEntry,quickPlan.Id,quickPlan.BatchId,quickPlan.Workspace.Key,workspacePath:quickPlan.Cwd);
            check("UX17 queued launch resolves original slot through task provenance",ChangeWorkspaces(slot,[quickControl],quickBook).Single().Path==quickPlan.Cwd&&quickPlan.SourceSlotId!=slot.Id);
            TaskBookOps.Edit(quickBook,imported.Single(),task=>task.Project=changedProject);
            check("UX17 editing quick task cannot erase frozen original slot mapping",quickBook.Tasks.Single().QuickLaunch is null&&quickRun.SourceSlotId==slot.Id&&ChangeWorkspaces(slot,[quickControl],quickBook).Single().Path==quickPlan.Cwd);
            quickRun.State="Completed";
            var history=new TaskHistoryArchive(1,DateTimeOffset.UtcNow,[quickBook.Tasks.Single().Copy()],[quickRun.Copy()],[quickControl]);
            check("UX17 migrated quick task history preserves original slot review mapping",ChangeWorkspaces(slot,[quickControl],new TaskBook(),[history]).Single().Path==quickPlan.Cwd);
            var unknown=new TaskBook{Attempts=[new TaskRun{AttemptId=Guid.NewGuid(),TaskId=imported.Single(),SourceSlotId=slot.Id}]};
            var refused=false;try{ChangeWorkspaces(slot,[],unknown);}catch(IOException){refused=true;}
            check("UX17 known attempt without directory never falls back to source project",refused);
            config.EmbeddedTerminal=true;RenderLaunchPreview([slot]);
            check("UX17 embedded preview exposes tabs and hides split layout",PreviewHeading.Text=="标签预览"&&PaneCount.Text=="1 个标签"&&ExternalLayoutOptions.Visibility==Visibility.Collapsed);
            config.EmbeddedTerminal=false;RenderLaunchPreview([slot]);
            check("UX17 external preview exposes actual split layout",PreviewHeading.Text=="分屏预览"&&ExternalLayoutOptions.Visibility==Visibility.Visible&&PreviewHostLabel.Text=="Windows Terminal");
            var explicitNode=new AgentProfile{Executable="C:\\tools\\node.exe",Arguments=["C:\\tools\\agent.mjs","--old"]};
            check("F17 presets preserve the explicit Node entry and replace CLI flags",PresetArguments(explicitNode,["exec","--new"]).SequenceEqual(new[]{"C:\\tools\\agent.mjs","exec","--new"}));
            var form=new FormWindow(this,"live arguments",[("args","参数","",false)],"",argumentLists:new(){["args"]=explicitNode.Arguments});
            try{form.SetArguments("args",["C:\\tools\\updated.mjs","--edited"]);check("F17 capability probes read current unsaved argument edits",form.ReadArguments("args").SequenceEqual(new[]{"C:\\tools\\updated.mjs","--edited"}));}finally{form.Close();}
        }
        finally{config=original;Render();}
    }

    private async Task RunChangeWorkspaceArchiveChecksAsync(Action<string,bool> check)
    {
        if(!Store.Root.Contains("AgentLauncher-tests-",StringComparison.Ordinal))throw new IOException("Isolated UI root required");
        var ledgerPath=Store.FilePath("control.json");var priorLedger=File.Exists(ledgerPath)?File.ReadAllBytes(ledgerPath):null;
        var priorBackup=File.Exists(ledgerPath+".bak")?File.ReadAllBytes(ledgerPath+".bak"):null;
        var priorTasks=File.ReadAllBytes(taskStore.FilePath);var priorTaskBackup=File.Exists(taskStore.FilePath+".bak")?File.ReadAllBytes(taskStore.FilePath+".bak"):null;
        var slot=new Slot{Enabled=true,AgentId="codex"};var project=Directory.CreateDirectory(Path.Combine(Store.Root,"archived-change-worktree")).FullName;
        var plan=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"pruned-worktree",project,PhysicalDirectory.Workspace(project),false,new("fixture.exe",[],"exe"),ResourcePolicy.AgentBudget,SourceSlotId:slot.Id,AgentId:slot.AgentId);
        var outcomePath=Path.Combine(Store.Root,"attempts",plan.Id.ToString("N"),"outcome.json");
        ReviewKillMonitor();
        try
        {
            Store.Save(taskStore.FilePath,new TaskBook());Store.Save(ledgerPath,new Ledger{BootId=Store.BootId()});
            var terminal=new ControlEntry{Plan=plan,Digest=Store.Digest(plan),State="Settled"};
            Store.Save(outcomePath,new ArchivedAttempt(1,plan.Id,terminal.Digest,terminal));
            var choices=await Task.Run(()=>LoadChangeWorkspaces(slot,[]));
            check("UX17 pruned ledger resolves verified outcome worktree without source fallback",choices.Single() is {} choice&&choice.Path==project&&choice.AttemptId==plan.Id&&choice.WorkspaceKey==plan.Workspace.Key);
            File.WriteAllText(outcomePath,"{unreadable outcome");
            var refused=false;try{await Task.Run(()=>LoadChangeWorkspaces(slot,[]));}catch(IOException){refused=true;}
            check("UX17 unreadable outcome history refuses speculative source review",refused);
        }
        finally
        {
            File.WriteAllBytes(taskStore.FilePath,priorTasks);
            if(priorTaskBackup is null)File.Delete(taskStore.FilePath+".bak");else File.WriteAllBytes(taskStore.FilePath+".bak",priorTaskBackup);
            if(priorLedger is null)File.Delete(ledgerPath);else File.WriteAllBytes(ledgerPath,priorLedger);
            if(priorBackup is null)File.Delete(ledgerPath+".bak");else File.WriteAllBytes(ledgerPath+".bak",priorBackup);
            File.Delete(outcomePath);File.Delete(outcomePath+".bak");
        }
    }
}
#endif
