#if INTEGRATION_TESTS
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;
public partial class MainWindow
{
    private async Task RunUiChecksAsync(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);var checks=new List<string>();
        // The harness owns an isolated, possibly non-existent test root. The app's preview-first startup no longer writes
        // monitor state there, so create it before the first configuration write (a missing root surfaces as a silent UI-only error).
        System.IO.Directory.CreateDirectory(Store.Root);
        void Check(string name,bool value){if(!value){Store.Save(System.IO.Path.Combine(folder,"ui-checks.json"),new{passed=checks,failed=name});throw new Exception("UI regression: "+name);}checks.Add(name);}
        IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
        {
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T target)yield return target;foreach(var nested in Find<T>(child))yield return nested;}
        }
        IEnumerable<T> FindLogical<T>(DependencyObject root) where T:DependencyObject
        {
            foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){if(child is T target)yield return target;foreach(var nested in FindLogical<T>(child))yield return nested;}
        }
        Check("config load is writable",!readOnly);
        // F6/F8 regressions: null template values and unsupported LastEdited must fail closed. Both use the real configPath in the isolated test root.
        await saveTask;
        var healthyConfig=System.IO.File.Exists(configPath)?System.IO.File.ReadAllBytes(configPath):null;
        var nullTemplate=System.Text.Json.JsonSerializer.SerializeToNode(new Configuration(),Store.Json)!;
        nullTemplate["Templates"]!["broken"]=null;
        System.IO.File.WriteAllText(configPath,nullTemplate.ToJsonString(Store.Json));
        await LoadAsync();
        Check("null template value enters fail-closed read-only",readOnly&&RestoreBackupButton.Visibility==Visibility.Visible&&!LaunchButton.IsEnabled&&ErrorText.Text.Contains("配置版本或结构不支持"));
        if(healthyConfig!=null)System.IO.File.WriteAllBytes(configPath,healthyConfig);else System.IO.File.Delete(configPath);
        readOnly=false;// white-box: exit the fail-closed state that production only clears via 恢复备份
        await LoadAsync();ErrorText.Text="";
        Check("healthy configuration reloads writable",!readOnly);
        var primaryBefore=System.IO.File.Exists(configPath)?System.IO.File.ReadAllBytes(configPath):null;
        var badBackup=new Configuration();badBackup.LastEdited=badBackup.Draft.Copy();badBackup.LastEdited.SchemaVersion=ConfigurationCompatibility.CurrentSchemaVersion+1;
        Store.Save(configPath+".bak",badBackup);
        RestoreBackupClick(this,new RoutedEventArgs());
        for(var i=0;i<50&&ErrorText.Text.Length==0;i++)await Task.Delay(100);
        Check("restore rejects backup startup would reject",ErrorText.Text.Contains("结构不支持"));
        var primaryAfter=System.IO.File.Exists(configPath)?System.IO.File.ReadAllBytes(configPath):null;
        Check("rejected restore leaves primary untouched",(primaryBefore==null)==(primaryAfter==null)&&(primaryBefore==null||primaryAfter!.SequenceEqual(primaryBefore)));
        System.IO.File.Delete(configPath+".bak");ErrorText.Text="";
        var legalBackup=new Configuration();
        legalBackup.Draft.Project="restored-v2";
        legalBackup.LastEdited=legalBackup.Draft.Copy();
        Store.Save(configPath+".bak",legalBackup);
        var confirmCalls=0;
        TestConfirm=(_,_,_)=>{confirmCalls++;return MessageBoxResult.OK;};
        try
        {
            RestoreBackupClick(this,new RoutedEventArgs());
            for(var i=0;i<50&&SaveStatus.Text!="已从备份恢复";i++)await Task.Delay(100);
            Check("legal schema 2 backup restores without modal dialog",confirmCalls==1&&!readOnly&&config.Draft.Project=="restored-v2"&&SaveStatus.Text=="已从备份恢复"&&RestoreBackupButton.Visibility==Visibility.Collapsed);
        }
        finally{TestConfirm=null;}
        System.IO.File.Delete(configPath+".bak");ErrorText.Text="";
        config=new Configuration();Render();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
        Check("empty state shows guidance not error",ErrorBanner.Visibility==Visibility.Collapsed&&LaunchHint.Text.Contains("项目")&&!LaunchButton.IsEnabled);
        ErrorText.Text="模拟错误";await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);Check("error state shows banner",ErrorBanner.Visibility==Visibility.Visible);ErrorText.Text="";
        config=new Configuration();config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();
        var first=Find<CheckBox>(SlotList).First();Keyboard.Focus(first);first.IsChecked=false;first.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check("row edit restores keyboard focus",Keyboard.FocusedElement is CheckBox focused&&AutomationProperties.GetName(focused)==AutomationProperties.GetName(first));
        readOnly=true;var roFirst=Find<CheckBox>(SlotList).First();roFirst.IsChecked=true;roFirst.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check("read-only reverts row edits",!config.Draft.Slots[0].Enabled&&roFirst.IsChecked==false);
        // F10 regression: a readOnly-refused edit must re-render so display and model stay aligned.
        DirectoryMode.SelectedIndex=2;
        Check("read-only rejects and reverts mode change",config.Draft.DirectoryMode=="isolated"&&DirectoryMode.SelectedIndex==0);
        readOnly=false;Render();
        // Regression: UIA TogglePattern (Narrator/assistive tech) raises Checked/Unchecked without Click; both must drive Edit.
        var toggle=(System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(Find<CheckBox>(SlotList).First());
        toggle.Toggle();
        Check("automation toggle updates Draft and summary",config.Draft.Slots[0].Enabled&&SelectionSummary.Text=="已选 1 个，共 4 个");
        toggle=(System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.CheckBoxAutomationPeer(Find<CheckBox>(SlotList).First());
        toggle.Toggle();
        Check("automation toggle reverts cleanly",!config.Draft.Slots[0].Enabled&&config.Draft.Slots.All(slot=>!slot.Enabled));
        var fourth=Find<CheckBox>(SlotList).Last();fourth.IsChecked=true;fourth.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check("native checkbox updates Draft",config.Draft.Slots.Count(s=>s.Enabled)==1&&config.Draft.Slots[3].Enabled);
        Check("preview keeps original slot numbers",Find<TextBlock>(Preview).First().Text.StartsWith("4"));
        config.SaveTemplate("fourth");config.Edit(d=>d.DirectoryMode="shared");config.Restore(config.Templates["fourth"]);Render();
        Check("native mode restore renders isolated",DirectoryMode.SelectedIndex==0&&config.Draft.DirectoryMode=="isolated");
        var firstId=config.Draft.Slots[0].Id;config.Edit(d=>d.Slots[0].Profiles["pi"]=new(){Executable="C:\\Example\\pi.exe",Arguments=["--only-pi"]});Render();
        var select=Find<ComboBox>(SlotList).First();select.SelectedValue="codex";Check("native Agent selector separates profile",config.Draft.Slots.Single(s=>s.Id==firstId).Current.Executable=="");
        select=Find<ComboBox>(SlotList).First();select.SelectedValue="pi";Check("native Agent selector restores profile",config.Draft.Slots.Single(s=>s.Id==firstId).Current.Arguments.SequenceEqual(new[]{"--only-pi"}));
        config.Edit(d=>{d.Slots[0].Profiles.Clear();d.Slots.ForEach(s=>s.Enabled=true);});Render();
        var firstMenu=Find<MenuButton>(SlotList).First();((Button)firstMenu.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var openItems=firstMenu.menu.Items.OfType<MenuItem>().ToArray();Check("menu enable states at open",openItems[0].IsEnabled==false&&openItems[1].IsEnabled&&openItems[2].IsEnabled);firstMenu.menu.IsOpen=false;
        var removedId=config.Draft.Slots[0].Id;openItems[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("remove drops exactly one slot",config.Draft.Slots.Count==3&&config.Draft.Slots.All(s=>s.Id!=removedId));
        config.Edit(d=>d.Layout="horizontal");Render();
        var undo=Find<Button>(Sessions).FirstOrDefault(b=>(b.Content as string)=="撤销移除");Check("undo offered after remove",undo!=null);
        undo!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("undo restores only removed slot",config.Draft.Slots.Count==4&&config.Draft.Slots[0].Id==removedId&&config.Draft.Layout=="horizontal");config.Edit(d=>d.Layout="vertical");Render();
        var menu2=Find<MenuButton>(SlotList).First();((Button)menu2.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var secondRemoved=config.Draft.Slots[0].Id;((MenuItem)menu2.menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));menu2.menu.IsOpen=false;
        while(config.Draft.Slots.Count<ResourcePolicy.MaxSlots)AddClick(this,new RoutedEventArgs());
        var undo2=Find<Button>(Sessions).FirstOrDefault(b=>(b.Content as string)=="撤销移除");Check("undo offered after second remove",undo2!=null);
        undo2!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("undo refuses when full",config.Draft.Slots.Count==ResourcePolicy.MaxSlots&&config.Draft.Slots.All(s=>s.Id!=secondRemoved)&&ProgressText.Text.Contains("无法撤销"));
        // F7 regression: a refused undo must stay retryable; freeing capacity then clicking must restore the slot.
        Check("undo stays enabled after capacity refusal",undo2.IsEnabled);
        var thirdMenu=Find<MenuButton>(SlotList).First();((Button)thirdMenu.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((MenuItem)thirdMenu.menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));thirdMenu.menu.IsOpen=false;
        undo2.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("undo retry restores after freeing capacity",config.Draft.Slots.Count==ResourcePolicy.MaxSlots&&config.Draft.Slots.Any(s=>s.Id==secondRemoved));
        var settingsDialog=CreateSettingsDialog();
        settingsDialog.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{
            var theme=FindLogical<ComboBox>(settingsDialog).Single(c=>AutomationProperties.GetName(c)=="主题");theme.SelectedIndex=2;
            var scale=FindLogical<ComboBox>(settingsDialog).Single(c=>AutomationProperties.GetName(c)=="文字与控件大小");scale.SelectedIndex=2;
            FindLogical<CheckBox>(settingsDialog).Single().IsChecked=true;
            FindLogical<Button>(settingsDialog).Single(b=>b.Content as string=="应用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Check("settings apply selected theme scale and minimize values",settingsDialog.ShowDialog()==true&&settingsDialog.Values["theme"]=="dark"&&settingsDialog.Values["scale"]=="150"&&settingsDialog.Values["minimize"]=="true");
        config.Theme="dark";ApplyTheme();Render();var darkInk=((SolidColorBrush)Application.Current.Resources["Ink"]).Color;var darkPage=((SolidColorBrush)Background).Color;
        Check("dark palette complete",SystemParameters.HighContrast||((SolidColorBrush)Application.Current.Resources["Selected"]).Color==(Color)ColorConverter.ConvertFromString("#1F3B66")&&((SolidColorBrush)Application.Current.Resources["Raised"]).Color==(Color)ColorConverter.ConvertFromString("#313438")&&((SolidColorBrush)Application.Current.Resources["WarningInk"]).Color==(Color)ColorConverter.ConvertFromString("#E8B45C"));
        Check("row labels follow theme",SystemParameters.HighContrast||((SolidColorBrush)commandLabels.Values.First().Foreground).Color==(Color)ColorConverter.ConvertFromString("#C5C5C5"));
        var agentCombo=Find<ComboBox>(SlotList).First();agentCombo.IsDropDownOpen=true;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(100);
        var selectedItem=(ComboBoxItem)agentCombo.ItemContainerGenerator.ContainerFromIndex(agentCombo.SelectedIndex);
        var selectionBrush=Find<Border>(selectedItem).Select(b=>b.Background).OfType<SolidColorBrush>().FirstOrDefault();
        Check("dropdown selection readable in dark",SystemParameters.HighContrast||(selectionBrush!=null&&selectionBrush.Color==(Color)ColorConverter.ConvertFromString("#1F3B66")));
        agentCombo.IsDropDownOpen=false;
        config.Theme="light";ApplyTheme();Render();var lightInk=((SolidColorBrush)Application.Current.Resources["Ink"]).Color;var lightPage=((SolidColorBrush)Background).Color;
        Check("theme resources switch",(Color)ColorConverter.ConvertFromString("#F5F5F5")==darkInk&&(Color)ColorConverter.ConvertFromString("#202020")==lightInk);
        Check("window background follows theme",(Color)ColorConverter.ConvertFromString("#202020")==darkPage&&(Color)ColorConverter.ConvertFromString("#F5F5F5")==lightPage);
        config=new Configuration();config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();await saveTask;
        await Task.Delay(1000);
        using(var process=Process.GetCurrentProcess()){process.Refresh();Store.Save(System.IO.Path.Combine(folder,"ui-idle-memory.json"),new{PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64});}
        foreach(var variant in new[]{("light",1040d,800d),("dark",800d,650d),("light",650d,500d),("dark",1040d,800d)})
        {
            config.Theme=variant.Item1;ApplyTheme();Width=variant.Item2;Height=variant.Item3;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(100);
            var point=LaunchButton.TranslatePoint(new Point(),this);Check($"launch visible {variant}",point.Y>=0&&point.Y+LaunchButton.ActualHeight<=ActualHeight&&point.X>=0&&point.X+LaunchButton.ActualWidth<=ActualWidth);
            var narrow=variant.Item2<900;
            Check($"panels fill width {variant}",narrow?Math.Abs(ConfigurationPanel.ActualWidth-WorkGrid.ActualWidth)<2&&Math.Abs(SummaryPanel.ActualWidth-WorkGrid.ActualWidth)<2&&Grid.GetColumn(SummaryPanel)==0&&Grid.GetRow(SummaryPanel)==1:Math.Abs(SummaryPanel.ActualWidth-320)<2&&ConfigurationPanel.ActualWidth<WorkGrid.ActualWidth&&Grid.GetColumn(SummaryPanel)==1&&Grid.GetRow(SummaryPanel)==0);
            Check($"project accessible name {variant}",AutomationProperties.GetName(ProjectPath).Length>0);
            var dpi=VisualTreeHelper.GetDpi(this);var image=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);image.Render(this);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=System.IO.File.Create(System.IO.Path.Combine(folder,$"native-{variant.Item1}-{variant.Item2}.png"));encoder.Save(file);
        }
        // F5 regression: the submission gate must hold only while attempts are still Pending; a confirmed running attempt must release it for the next batch.
        // Reserve admission depends on real free memory, which is environment-dependent; restart the monitor under the sanctioned FAKE_SAMPLE hook for determinism, then restore real sampling for the final health check.
        var realMonitor=Store.Read<int>(Store.FilePath("monitor-pid.json"));if(realMonitor>0){try{using var m=Process.GetProcessById(realMonitor);m.Kill();m.WaitForExit(5000);}catch(ArgumentException){}}
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE","1");
        await RefreshAsync();Check("fake-sample monitor healthy",status is{Ok:true});
        var gateItem=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"ui-gate",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new(Commands.FindExe("node.exe")!,["-e","setTimeout(()=>{},1500)"],"exe"),ResourcePolicy.AgentBudget);
        var gateReserve=await Ipc.ReserveWithCleanupAsync([gateItem]);Check("gate reserve admitted ("+gateReserve.Message+")",gateReserve.Ok);
        submittedAttempts=[gateItem.Id];submittedRevision=config.Draft.Revision;
        await RefreshAsync();
        Check("pending attempt keeps launch gate",submittedAttempts.Length==1&&submittedRevision==config.Draft.Revision);
        await RefreshAsync();
        Check("ACC-S03 unchanged snapshot preserves pending submission",status?.Unchanged==true&&status.Entries?.Any(e=>e.Plan.Id==gateItem.Id&&e.State=="Pending")==true&&submittedAttempts.SequenceEqual(new[]{gateItem.Id})&&submittedRevision==config.Draft.Revision);
        var gateStart=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};gateStart.ArgumentList.Add("run");gateStart.ArgumentList.Add(gateItem.Id.ToString());
        using(var gateRunner=Process.Start(gateStart)!)
        {
            gateRunner.StandardInput.Close();
            Receipt? gateReceipt=null;for(var i=0;i<50;i++){await Task.Delay(100);gateReceipt=Store.Read<Receipt>(Store.ReceiptPath(gateItem.Id));if(gateReceipt?.Phase=="Started")break;}
            Check("gate runner started",gateReceipt?.Phase=="Started");
            await RefreshAsync();
            Check("confirmed running attempt releases launch gate",submittedAttempts.Length==0&&submittedRevision==-1);
            await gateRunner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        for(var i=0;i<50;i++){await RefreshAsync();if(status?.Entries?.Any(e=>e.Plan.Id==gateItem.Id&&Store.IsTerminal(e))==true)break;await Task.Delay(100);}
        Check("gate attempt settled",status?.Entries?.Any(e=>e.Plan.Id==gateItem.Id&&Store.IsTerminal(e))==true);
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE",null);
        var fakeMonitor=Store.Read<int>(Store.FilePath("monitor-pid.json"));if(fakeMonitor>0){try{using var m=Process.GetProcessById(fakeMonitor);m.Kill();m.WaitForExit(5000);}catch(ArgumentException){}}
        await RefreshAsync();
        Check("monitor reports healthy",status is{Ok:true});
        var argumentSnapshot=new[]{"","first\nsecond","中文 value","tail\\"};
        var argumentDialog=new FormWindow(this,"参数往返",[("args","启动参数","",false)],"",argumentLists:new(){["args"]=argumentSnapshot});
        argumentDialog.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>FindLogical<Button>(argumentDialog).Single(b=>b.Content as string=="应用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        Check("F05 dialog preserves empty multiline and spaced arguments",argumentDialog.ShowDialog()==true&&argumentDialog.ArgumentValues["args"].SequenceEqual(argumentSnapshot));
        var reorderDialog=new FormWindow(this,"参数排序",[("args","启动参数","",false)],"",argumentLists:new(){["args"]=["first","second",""]});
        reorderDialog.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{
            FindLogical<Button>(reorderDialog).Single(b=>AutomationProperties.GetName(b)=="第 2 个参数上移").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FindLogical<Button>(reorderDialog).Single(b=>AutomationProperties.GetName(b)=="第 2 个参数移除").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            FindLogical<Button>(reorderDialog).Single(b=>b.Content as string=="应用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Check("F05 argument reorder and removal preserve remaining values",reorderDialog.ShowDialog()==true&&reorderDialog.ArgumentValues["args"].SequenceEqual(new[]{"second",""}));
        var presetDialog=new FormWindow(this,"预设套用",[("args","启动参数","",false)],"",argumentLists:new(){["args"]=["--stale"]});
        var presetArgs=AgentPresets.For("omp",AgentPresets.OmpVerifiedVersion).Single(p=>p.Id=="omp-resume").Fill("session-中文").Arguments;
        presetDialog.SetArguments("args",presetArgs);
        var presetRows=FindLogical<TextBox>(presetDialog).Select(t=>t.Text).ToArray();
        presetDialog.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>FindLogical<Button>(presetDialog).Single(b=>b.Content as string=="应用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        Check("ACC-A01 applied preset previews and saves every argument exactly",presetDialog.ShowDialog()==true&&presetDialog.ArgumentValues["args"].SequenceEqual(presetArgs)&&presetRows.SequenceEqual(presetArgs));
        Check("ACC-A02 presets stay unavailable without the exact verified version",AgentPresets.For("omp","19.0.0").Count==0&&AgentPresets.For("omp",AgentPresets.OmpVerifiedVersion).Count>0);
        var priorProject=config.Draft.Project;config.Draft.Project="C:\\PrivateProject\\DoNotExport";config.Draft.Slots[0].Profiles[config.Draft.Slots[0].AgentId]=new(){Arguments=["SECRET_ARGUMENT_SENTINEL"]};
        var redacted=DiagnosticText(false);var explicitPaths=DiagnosticText(true);
        Check("diagnostics excludes arguments and redacts paths by default",!redacted.Contains("DoNotExport")&&!redacted.Contains("SECRET_ARGUMENT_SENTINEL")&&!explicitPaths.Contains("SECRET_ARGUMENT_SENTINEL"));
        using(var diagnostic=System.Text.Json.JsonDocument.Parse(explicitPaths))Check("diagnostics includes paths only on explicit opt in",diagnostic.RootElement.GetProperty("Project").GetString()==config.Draft.Project);
        config.Draft.Project=priorProject;
        foreach(var scale in new[]{125,150})
        {
            config.TextScalePercent=scale;config.Theme="dark";ApplyTheme();Render();Width=650;Height=700;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
            var buttonPoint=LaunchButton.TranslatePoint(new Point(),this);Check($"launch remains reachable at {scale}% text scale",buttonPoint.X>=0&&buttonPoint.Y>=0&&buttonPoint.X+LaunchButton.ActualWidth*UiScale.Value<=ActualWidth&&buttonPoint.Y+LaunchButton.ActualHeight*UiScale.Value<=ActualHeight);
            var dpi=VisualTreeHelper.GetDpi(this);var image=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);image.Render(this);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=System.IO.File.Create(System.IO.Path.Combine(folder,$"native-text-{scale}.png"));encoder.Save(file);
        }
        config.TextScalePercent=100;ApplyTheme();
        config=new Configuration();config.Draft.Slots.ForEach(s=>s.Enabled=false);Render();var projectBefore=config.Draft.Project;var targetSlot=config.Draft.Slots[2].Id;
        var dropDirectory=System.IO.Path.Combine(Store.Root,"drop-directory");System.IO.Directory.CreateDirectory(dropDirectory);
        await ApplyDroppedDirectoriesAsync([dropDirectory],targetSlot,config.Draft.Revision);
        Check("F04 per-slot drop preserves project other slots and enablement",config.Draft.Project==projectBefore&&config.Draft.Slots[2].Directory==PhysicalDirectory.Resolve(dropDirectory).Path&&config.Draft.Slots.Where(s=>s.Id!=targetSlot).All(s=>s.Directory=="")&&config.Draft.Slots.All(s=>!s.Enabled));
        var dropBefore=System.Text.Json.JsonSerializer.Serialize(config.Draft,Store.Json);var badFile=System.IO.Path.Combine(Store.Root,"not-a-directory.txt");System.IO.File.WriteAllText(badFile,"test");
        var rejectedDrop=false;try{await ApplyDroppedDirectoriesAsync([dropDirectory,badFile],null,config.Draft.Revision);}catch(System.IO.IOException){rejectedDrop=true;}
        Check("F04 mixed directory and file drop refuses entire change",rejectedDrop&&System.Text.Json.JsonSerializer.Serialize(config.Draft,Store.Json)==dropBefore);
        rejectedDrop=false;try{await ApplyDroppedDirectoriesAsync([dropDirectory],null,config.Draft.Revision-1);}catch(System.IO.IOException){rejectedDrop=true;}
        Check("F04 stale directory check cannot overwrite later edit",rejectedDrop&&System.Text.Json.JsonSerializer.Serialize(config.Draft,Store.Json)==dropBefore);
        var pendingDrop=ApplyDroppedDirectoriesAsync([dropDirectory],targetSlot,config.Draft.Revision);ProjectPath.Text="new-uncommitted-path";
        rejectedDrop=false;try{await pendingDrop;}catch(System.IO.IOException){rejectedDrop=true;}
        Check("F04 directory checks preserve newer uncommitted path input",rejectedDrop&&ProjectPath.Text=="new-uncommitted-path"&&System.Text.Json.JsonSerializer.Serialize(config.Draft,Store.Json)==dropBefore);Render();
        foreach(var name in new[]{"none","last"})
        {
            config=new Configuration();config.Edit(d=>{d.Slots.ForEach(s=>s.Enabled=false);d.Slots[3].Enabled=true;d.DirectoryMode="prepared";});config.SaveTemplate(name);config.Edit(d=>{d.Slots[0].Enabled=true;d.Slots[3].Enabled=false;d.DirectoryMode="shared";});Render();
            Templates.SelectedItem=Templates.Items.OfType<ComboBoxItem>().Single(item=>item.Content as string==name);
            Check($"F12 named template {name} restores its own snapshot",config.Draft.DirectoryMode=="prepared"&&config.Draft.Slots[3].Enabled&&!config.Draft.Slots[0].Enabled);
        }
        await RunTaskChecksAsync(Check);
        await RunQuickQueueChecksAsync(Check);
        ReviewUiChecks.Run(Check);
        await RunReviewUiChecksAsync(Check);
        await RunMultipaneChecksAsync(Check);
        var pids=status?.Entries?.Where(e=>!Store.IsTerminal(e)).Count()??0;Check("UI checks do not launch agents",pids==0);
        Store.Save(System.IO.Path.Combine(folder,"ui-checks.json"),new{passed=checks,Dpi=VisualTreeHelper.GetDpi(this).PixelsPerInchX,Resource=ResourceText.Text});Close();
    }
}
#endif
