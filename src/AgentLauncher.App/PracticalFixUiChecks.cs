#if INTEGRATION_TESTS
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunPracticalFixChecksAsync(string folder,Action<string,bool> check)
    {
        var original=config;var polling=timer.IsEnabled;var pumping=taskTimer.IsEnabled;var enabled=queueEnabled;var wasLoading=loading;
        timer.Stop();taskTimer.Stop();await saveTask;
        try
        {
            config=new Configuration();config.Draft.Project=Store.Root;loading=false;Render();
            var cancelled=false;var opened=false;var clock=Stopwatch.StartNew();
            detailProbeForTest=async token=>{try{await Task.Delay(8000,token);}catch(OperationCanceledException){cancelled=true;throw;}return new();};
            var inspect=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            inspect.Tick+=(_,_)=>
            {
                var dialog=OwnedWindows.OfType<FormWindow>().FirstOrDefault();
                if(dialog is not null)
                {
                    opened=clock.Elapsed<TimeSpan.FromSeconds(2);
                    CaptureQuickActions(dialog,System.IO.Path.Combine(folder,"practical-details-loading.png"));
                    dialog.Close();inspect.Stop();
                }
                else if(clock.Elapsed>TimeSpan.FromSeconds(10))inspect.Stop();
            };
            inspect.Start();await EditSlotAsync(config.Draft.Slots[0].Id);inspect.Stop();
            check("UX12 details open during slow probe and closing cancels promptly",opened&&cancelled&&!editingSlot&&clock.Elapsed<TimeSpan.FromSeconds(3));
            detailProbeForTest=null;
            queueEnabled=true;EnterBackground();
            check("UX12 background tray hides window while queue remains enabled",!IsVisible&&queueEnabled&&backgroundIcon is not null&&backgroundTimer.IsEnabled);
            showRequest!.Set();await Task.Delay(1200);
            check("UX12 second-launch signal restores hidden window without stopping queue",IsVisible&&queueEnabled&&!backgroundTimer.IsEnabled);
            check("UX12 invalid terminal handle never reports foreground success",!TerminalWindowFinder.Focus(IntPtr.Zero));
            var notifier=SessionNotifier.For(this);notifier.Reset();notifier.TestMode=true;
            var id=Guid.NewGuid();var plan=new LaunchItem(id,Guid.NewGuid(),1,"测试",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new("fixture.exe",[],"exe"),ResourcePolicy.AgentBudget);
            ControlEntry Entry(string state)=>new(){Plan=plan,State=state,ExitCode=9};
            notifier.Observe([Entry("Started")],true);notifier.Observe([Entry("Exited")],true);notifier.Observe([Entry("Settled")],true);
            check("UX12 notifications distinguish root exit from full tree drain",notifier.Delivered.Count==2&&notifier.Delivered[0].Message.Contains("仍在运行")&&notifier.Delivered[1].Message.Contains("退出码 9"));
            notifier.Reset();
            var repo=Path.Combine(Store.Root,"cleanup-inspection");Directory.CreateDirectory(repo);
            var git=Commands.FindExe("git.exe")??throw new IOException("清理检查夹具需要 Git。");
            var initialized=await AgentProbe.RunAsync(new(git,["init","-q"],"exe"),repo,TimeSpan.FromSeconds(10),4096);
            if(initialized.ExitCode!=0||initialized.TimedOut)throw new IOException("无法初始化清理检查夹具。");
            File.WriteAllText(Path.Combine(repo,".gitignore"),".env\n");File.WriteAllText(Path.Combine(repo,"tracked.txt"),"kept\n");
            var staged=await AgentProbe.RunAsync(new(git,["add","--",".gitignore","tracked.txt"],"exe"),repo,TimeSpan.FromSeconds(10),4096);
            if(staged.ExitCode!=0||staged.TimedOut)throw new IOException("无法暂存清理检查夹具。");
            File.WriteAllText(Path.Combine(repo,"新增 文件.txt"),"untracked\n");File.WriteAllText(Path.Combine(repo,".env"),"fixture only\n");
            await ShowCleanupInventoryAsync(this,repo,"目录包含本地文件，请先检查：");
            var inventory=OwnedWindows.Cast<Window>().Single(window=>window.Title=="清理前检查文件");
            try
            {
                var text=ReviewFind<TextBox>(inventory).Single().Text;
                check("UX12 cleanup inspection shows tracked untracked and ignored files without deleting",text.Contains("已跟踪修改")&&text.Contains("未跟踪")&&text.Contains("忽略内容")&&text.Contains("新增 文件.txt")&&text.Contains(".env")&&File.Exists(Path.Combine(repo,".env")));
                await Dispatcher.InvokeAsync(inventory.UpdateLayout,DispatcherPriority.ApplicationIdle);
                CaptureQuickActions(inventory,Path.Combine(folder,"practical-cleanup-inventory.png"));
            }
            finally{inventory.Close();}
            var deliveryChoices=false;var taskDialog=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            taskDialog.Tick+=(_,_)=>
            {
                var dialog=OwnedWindows.OfType<FormWindow>().FirstOrDefault();
                if(dialog is null)return;
                taskDialog.Stop();
                var choice=ReviewFind<ComboBox>(dialog).Single(combo=>AutomationProperties.GetName(combo)=="任务交付方式");
                deliveryChoices=choice.Items.Count==3&&((choice.SelectedItem as ComboBoxItem)?.Tag as string)=="manual";
                choice.BringIntoView();dialog.UpdateLayout();CaptureQuickActions(dialog,Path.Combine(folder,"practical-task-delivery.png"));dialog.Close();
            };
            taskDialog.Start();try{await NewTaskAsync();}finally{taskDialog.Stop();}
            check("UX12 new task offers all delivery modes and defaults to manual",deliveryChoices);
        }
        finally
        {
            detailProbeForTest=null;if(!IsVisible)RestoreBackground();config=original;queueEnabled=enabled;loading=wasLoading;Render();
            if(polling)timer.Start();if(pumping)taskTimer.Start();
        }
    }
}
#endif
