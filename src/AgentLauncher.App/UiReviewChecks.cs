#if INTEGRATION_TESTS
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;
public partial class MainWindow
{
    private static MemorySnapshot ReviewSample()=>new(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);
    private static ControlEntry ReviewEntry(string name,string state="Pending")
    {
        var path=Path.Combine(Store.Root,name);
        return new ControlEntry{Plan=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,name,path,new WorkspaceIdentity(path,"review"),false,new CommandSpec(Path.Combine(Environment.SystemDirectory,"cmd.exe"),[],"exe"),ResourcePolicy.AgentBudget),State=state,Detail=name};
    }
    private static IEnumerable<T> ReviewFind<T>(DependencyObject root)where T:DependencyObject
    {
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){if(child is T item)yield return item;foreach(var nested in ReviewFind<T>(child))yield return nested;}
    }
    private static void ReviewRemoveFirstSlot(MainWindow owner)
    {
        var host=ReviewFind<MenuButton>(owner.SlotList).First();((Button)host.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        host.menu.Items.OfType<MenuItem>().Single(item=>(item.Header as string)=="移除").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));host.menu.IsOpen=false;
    }
    private static void ReviewKillMonitor()
    {
        if(!Path.GetFileName(Store.Root).StartsWith("AgentLauncher-tests-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Monitor cleanup requires an isolated test root.");
        var file=Store.FilePath("monitor-pid.json");var pid=Store.Read<int>(file);if(pid<=0)return;
        try{using var process=Process.GetProcessById(pid);var started=process.StartTime;if(process.HasExited||Store.Read<int>(file)!=pid)return;using var current=Process.GetProcessById(pid);if(current.StartTime!=started||!StringComparer.OrdinalIgnoreCase.Equals(current.MainModule?.FileName,Store.WorkerPath))throw new InvalidOperationException("Fixture monitor identity changed.");current.Kill();current.WaitForExit(5000);}catch(ArgumentException){}
    }
    private async Task RunReviewUiChecksAsync(Action<string,bool> Check)
    {
        // CR08: a failed save keeps the latest draft and an explicit retry must persist it after the lock is gone.
        config=new Configuration();config.Draft.Project=Store.Root;config.Draft.Slots.ForEach(s=>s.Enabled=false);
        config.Draft.Slots[0].Enabled=true;config.Draft.Slots[0].Profiles["pi"]=new(){Executable=Commands.FindExe("node.exe")??Path.Combine(Environment.SystemDirectory,"cmd.exe")};
        Render();await CheckCommandsAsync();await saveTask;
        using(var hold=new FileStream(configPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
        {
            config.Draft.Project=Path.Combine(Store.Root,"review-save-blocked");config.Draft.Revision++;QueueSave();await saveTask;
            Check("CR08 failed save keeps draft and offers explicit retry",saveError!=null&&RetrySaveButton.Visibility==Visibility.Visible&&config.Draft.Project.EndsWith("review-save-blocked"));
        }
        config.Draft.Project=Path.Combine(Store.Root,"review-save-latest");config.Draft.Revision++;QueueSave();await RetrySaveAsync();
        var persisted=Store.Read<Configuration>(configPath);
        Check("CR08 retry persists latest snapshot and clears failure",saveError==null&&RetrySaveButton.Visibility==Visibility.Collapsed&&persisted?.Draft.Project==config.Draft.Project&&SaveStatus.Text.Contains("已保存"));
        readOnly=true;var readOnlyRefused=false;try{await RetrySaveAsync();}catch(IOException){readOnlyRefused=true;}
        Check("CR08 retry never clears read-only protection",readOnlyRefused&&readOnly);readOnly=false;

        // CR07: effective directories are checked before launch; stale results and uncommitted text cannot turn the gate green.
        config=new Configuration();config.Draft.Project=Store.Root;config.Draft.Slots.ForEach(s=>s.Enabled=false);
        config.Draft.Slots[0].Enabled=true;config.Draft.Slots[0].Profiles["pi"]=new(){Executable=Commands.FindExe("node.exe")??Path.Combine(Environment.SystemDirectory,"cmd.exe")};
        Render();await CheckCommandsAsync();
        Check("CR07 valid directory passes preflight",validationError.Length==0);
        var missingDirectory=Path.Combine(Store.Root,"review-missing-"+Guid.NewGuid().ToString("N"));
        config.Draft.Project=missingDirectory;Render();await CheckCommandsAsync();
        Check("CR07 missing effective directory blocks launch",validationError.Length>0&&validationError.Contains("目录")&&!LaunchButton.IsEnabled);
        config.Draft.Project=Store.Root;Render();
        var pendingCheck=CheckCommandsAsync();
        config.Draft.Project=missingDirectory;config.Draft.Revision++;Render();
        config.Draft.Project=Store.Root;config.Draft.Revision++;Render();
        await pendingCheck;await CheckCommandsAsync();
        Check("CR07 stale directory result cannot block newer input",validationError.Length==0&&!commandsChecking);
        ProjectPath.Text="  ";
        Check("CR07 uncommitted project text cannot stay launch-ready",!LaunchButton.IsEnabled&&LaunchHint.Text.Contains("尚未应用"));
        ProjectPath.Text=missingDirectory;ProjectLostFocus(this,new RoutedEventArgs());
        for(var i=0;i<100&&(commandsChecking||commandsRunning);i++)await Task.Delay(50);
        Check("CR07 manual path blur surfaces the directory error",validationError.Length>0&&!LaunchButton.IsEnabled&&config.Draft.Project==missingDirectory);
        config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();QueueSave();await saveTask;
        var healthy=ReviewSample();
        status=new Response(true,"healthy",[],healthy,Epoch:null,AdmissionBlockedReason:"资源压力尚未连续恢复，稍后重新检查。");UpdateLaunch();
        Check("CR07 monitor admission reason blocks a healthy sample",!LaunchButton.IsEnabled&&LaunchHint.Text.Contains("资源压力"));
        status=new Response(true,"healthy",[],healthy);UpdateLaunch();
        Check("CR07 healthy admission allows launch",LaunchButton.IsEnabled);
        await RefreshAsync();

        // CR05: only history is bounded; an older Pending row and its revoke action must stay reachable.
        var active=ReviewEntry("cr05-active");active.Created=DateTimeOffset.UtcNow.AddMinutes(-10);
        var entries=new List<ControlEntry>{active};
        for(var i=0;i<13;i++){var history=ReviewEntry("cr05-history-"+i,"Settled");entries.Add(history);}
        status=new Response(true,"healthy",entries.ToArray(),healthy);RenderSessions();
        var sessionRows=ReviewFind<TextBox>(Sessions).ToArray();
        Check("CR05 active pending row survives thirteen histories",sessionRows.Any(row=>row.Text==active.Plan.Cwd)&&sessionRows.Length==13);
        Check("CR05 active pending revoke survives thirteen histories",ReviewFind<Button>(Sessions).Any(button=>button.Content as string=="撤销未启动项"));
        await RefreshAsync();

        // CR09: undo is model state, not a child of the session tree; multiple removals and capacity refusal stay retryable.
        undoRemovals.Clear();config=new Configuration();config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();
        var removedFirst=config.Draft.Slots[0].Id;ReviewRemoveFirstSlot(this);
        Check("CR09 removal offers undo",ReviewFind<Button>(Sessions).Any(button=>button.Content as string=="撤销移除"));
        var changed=ReviewEntry("cr09-unrelated","Settled");status=new Response(true,"healthy",[changed],healthy);RenderSessions();
        var undo=ReviewFind<Button>(Sessions).FirstOrDefault(button=>button.Content as string=="撤销移除");
        Check("CR09 undo survives unrelated session render",undo!=null);
        undo!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("CR09 undo restores the removed slot",config.Draft.Slots.Any(slot=>slot.Id==removedFirst));
        await RefreshAsync();
        undoRemovals.Clear();config=new Configuration();config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();
        var oldest=config.Draft.Slots[0].Id;ReviewRemoveFirstSlot(this);
        var newest=config.Draft.Slots[0].Id;ReviewRemoveFirstSlot(this);
        var undos=ReviewFind<Button>(Sessions).Where(button=>button.Content as string=="撤销移除").ToArray();
        Check("CR09 multiple removals keep multiple undo actions",undos.Length==2);
        undos[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("CR09 newest undo restores its slot",config.Draft.Slots.Any(slot=>slot.Id==newest));
        var olderUndo=ReviewFind<Button>(Sessions).FirstOrDefault(button=>button.Content as string=="撤销移除");
        Check("CR09 older undo remains after one restore",olderUndo!=null);
        status=new Response(true,"healthy",[changed],healthy);RenderSessions();
        olderUndo=ReviewFind<Button>(Sessions).FirstOrDefault(button=>button.Content as string=="撤销移除");
        Check("CR09 older undo survives another session render",olderUndo!=null);
        olderUndo!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("CR09 older undo restores after session render",config.Draft.Slots.Any(slot=>slot.Id==oldest));
        await RefreshAsync();
        undoRemovals.Clear();config=new Configuration();config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();
        var refusedSlot=config.Draft.Slots[0].Id;ReviewRemoveFirstSlot(this);AddClick(this,new RoutedEventArgs());
        var refusedUndo=ReviewFind<Button>(Sessions).FirstOrDefault(button=>button.Content as string=="撤销移除");
        refusedUndo!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("CR09 capacity refusal keeps undo retryable",config.Draft.Slots.Count==4&&config.Draft.Slots.All(slot=>slot.Id!=refusedSlot)&&ProgressText.Text.Contains("无法撤销"));
        status=new Response(true,"healthy",[changed],healthy);RenderSessions();
        Check("CR09 capacity-refused undo survives session render",ReviewFind<Button>(Sessions).Any(button=>button.Content as string=="撤销移除"));
        ReviewRemoveFirstSlot(this);refusedUndo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("CR09 undo retry restores after freeing capacity",config.Draft.Slots.Any(slot=>slot.Id==refusedSlot));
        await RefreshAsync();

        // CR03: stop revokes the frozen batch through the monitor and late begin is denied; session revoke stays usable while busy.
        ReviewKillMonitor();Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE","1");await RefreshAsync();
        var node=Commands.FindExe("node.exe")!;
        var cancelItem=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"cr03-cancel",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new CommandSpec(node,["-e","setTimeout(()=>{},4000)"],"exe"),ResourcePolicy.AgentBudget);
        var cancelReserve=await Ipc.ReserveWithCleanupAsync([cancelItem]);
        Check("CR03 cancel reservation admitted ("+cancelReserve.Message+")",cancelReserve.Ok);
        submittedAttempts=[cancelItem.Id];submittedRevision=config.Draft.Revision;launchCancellation=new();
        SetBusy(true);
        Check("CR03 session revoke stays usable while config is busy",Sessions.IsEnabled&&!SlotList.IsEnabled&&CancelButton.IsEnabled);
        CancelClick(this,new RoutedEventArgs());
        var revoked=false;
        for(var i=0;i<60&&!revoked;i++){var probe=await Ipc.SendAsync(new("status",Guid.NewGuid()));revoked=probe.Entries?.Any(entry=>entry.Plan.Id==cancelItem.Id&&entry.State=="Revoked")==true;if(!revoked)await Task.Delay(50);}
        Check("CR03 stop revokes a frozen submitted attempt",revoked&&launchCancellation?.IsCancellationRequested==true);
        var late=await Ipc.SendAsync(new("begin",Guid.NewGuid(),Attempt:cancelItem.Id));
        Check("CR03 late begin is denied after cancel",!late.Ok);
        submittedAttempts=[];submittedRevision=-1;launchCancellation?.Dispose();launchCancellation=null;SetBusy(false);
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE",null);ReviewKillMonitor();await RefreshAsync();
        Check("CR03 monitor healthy after cancel",status is{Ok:true});

        config=new Configuration();config.Draft.Slots.ForEach(slot=>slot.Enabled=false);Render();await CheckCommandsAsync();await saveTask;
    }
}
#endif
