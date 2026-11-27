#if INTEGRATION_TESTS
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunConfigurationFormFailureChecksAsync(Action<string,bool> check)
    {
        await saveTask;
        var original=config;var wasPolling=timer.IsEnabled;var wasPumping=taskTimer.IsEnabled;
        Guid? fixtureSlotId=null;
        timer.Stop();taskTimer.Stop();
        var host=new RemoteHost(Guid.NewGuid(),"原主机","fixture.invalid",22,"user","agent",["codex"],null);
        try
        {
            config=new Configuration();config.Draft.Project=Store.Root;config.Draft.Slots=[new(){AgentId="codex",CustomName="原名称"}];
            config.RemoteHosts=[host];remoteResourceCache[host.Id]=(DateTimeOffset.UtcNow,Task.FromResult<RemoteResourceSample?>(null));
            Render();await SaveFormConfigurationAsync();
            async Task ExerciseForm(string label,Action open,Action<FormWindow> edit,Func<Configuration,bool> unchanged)
            {
                using(var blocked=new FileStream(configPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    var exercise=Dispatcher.InvokeAsync(async()=>
                    {
                        var form=OwnedWindows.OfType<FormWindow>().Single();
                        try
                        {
                            edit(form);var apply=FindLogical(form).OfType<Button>().Single(button=>button.IsDefault);
                            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            for(var i=0;i<150&&form.ApplyError.Length==0;i++)await Task.Delay(20);
                            check("UX17 "+label+" failed save retains draft and rolls back live configuration",form.IsVisible&&form.ApplyError.Length>0&&unchanged(config));
                        }
                        finally{form.Close();}
                    });
                    open();await exercise.Task.Unwrap();
                }
                await RetrySaveAsync();
                check("UX17 "+label+" cancelled draft is absent after global save retry",unchanged(Store.Read<Configuration>(configPath)!));
            }
            var slotId=config.Draft.Slots[0].Id;
            fixtureSlotId=slotId;presetSlotDefaults[slotId]=config.Draft.Slots[0].Copy();
            await ExerciseForm("rename",()=>RenameSlot(slotId),form=>form.SetValue("name","取消的名称"),value=>value.Draft.Slots[0].CustomName=="原名称");
            check("UX17 failed slot edit restores scale preset edit tracking",!presetEditedSlots.Contains(slotId));
            var originalTheme=config.Theme;
            await ExerciseForm("settings",()=>SettingsClick(this,new RoutedEventArgs()),form=>form.SetValue("theme",originalTheme=="dark"?"light":"dark"),value=>value.Theme==originalTheme);
            await ExerciseForm("template",()=>SaveTemplateClick(this,new RoutedEventArgs()),form=>form.SetValue("name","取消的组合"),value=>!value.Templates.ContainsKey("取消的组合"));

            using(var blocked=new FileStream(configPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var exercise=Dispatcher.InvokeAsync(async()=>
                {
                    var manager=OwnedWindows.OfType<Window>().Single(window=>window.Title=="管理远程主机（VPS）");
                    try
                    {
                        var edit=FindLogical(manager).OfType<Button>().Single(button=>AutomationProperties.GetName(button)=="编辑远程主机 原主机");
                        var fill=Dispatcher.InvokeAsync(()=>
                        {
                            var form=manager.OwnedWindows.OfType<FormWindow>().Single();
                            form.SetValue("name","取消的主机");FindLogical(form).OfType<Button>().Single(button=>button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        });
                        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await fill.Task;
                        FindLogical(manager).OfType<Button>().Single(button=>AutomationProperties.GetName(button)=="应用远程主机更改").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var error=FindLogical(manager).OfType<TextBlock>().Single(text=>AutomationProperties.GetName(text)=="远程主机保存错误");
                        for(var i=0;i<150&&error.Text.Length==0;i++)await Task.Delay(20);
                        check("UX17 remote host save failure keeps staged edits separate from live hosts",manager.IsVisible&&error.Text.Length>0&&config.RemoteHosts.Single().Name==host.Name);
                    }
                    finally{manager.Close();}
                });
                ManageRemoteHosts(this);await exercise.Task.Unwrap();
            }
            await RetrySaveAsync();
            check("UX17 cancelled remote hosts are absent after global save retry",Store.Read<Configuration>(configPath)!.RemoteHosts.Single().Name==host.Name);
        }
        finally
        {
            if(fixtureSlotId is {} slotId){presetSlotDefaults.Remove(slotId);presetEditedSlots.Remove(slotId);}
            remoteResourceCache.Remove(host.Id);config=original;await SaveFormConfigurationAsync();ApplyTheme();Render();
            if(wasPolling)timer.Start();if(wasPumping)taskTimer.Start();
        }
    }
}
#endif
