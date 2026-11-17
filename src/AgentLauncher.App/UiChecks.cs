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
        SessionNotifier.For(this).TestMode = true;
        void Check(string name,bool value)
        {
            if(!value){Store.Save(System.IO.Path.Combine(folder,"ui-checks.json"),new{passed=checks,failed=name});throw new Exception("UI regression: "+name);}
            checks.Add(name);
            // Record every boundary so a later layout stall is attributed to the actual last completed check.
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder,"ui-progress.json"),System.Text.Json.JsonSerializer.Serialize(new{LastPassed=name,Count=checks.Count,At=DateTimeOffset.UtcNow}));
        }
        IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
        {
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T target)yield return target;foreach(var nested in Find<T>(child))yield return nested;}
        }
        IEnumerable<T> FindLogical<T>(DependencyObject root) where T:DependencyObject
        {
            foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){if(child is T target)yield return target;foreach(var nested in FindLogical<T>(child))yield return nested;}
        }
        async Task SelectRailSlot(int position)
        {
            Find<Button>(RailSlots).First(b=>AutomationProperties.GetName(b)==$"第 {position} 项 Agent 行").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
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
        await SelectRailSlot(4);var fourth=Find<CheckBox>(SlotList).First();fourth.IsChecked=true;fourth.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check("native checkbox updates Draft",config.Draft.Slots.Count(s=>s.Enabled)==1&&config.Draft.Slots[3].Enabled);
        Check("preview keeps original slot numbers",Find<TextBlock>(Preview).First().Text.StartsWith("4"));
        await SelectRailSlot(1);
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
            FindLogical<CheckBox>(settingsDialog).Single(c=>AutomationProperties.GetName(c)=="启动全部确认后最小化窗口").IsChecked=true;
            FindLogical<CheckBox>(settingsDialog).Single(c=>AutomationProperties.GetName(c)=="会话状态变化时显示桌面通知");
            FindLogical<Button>(settingsDialog).Single(b=>b.Content as string=="应用").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        Check("settings apply selected theme scale minimize and notify values",settingsDialog.ShowDialog()==true&&settingsDialog.Values["theme"]=="dark"&&settingsDialog.Values["scale"]=="150"&&settingsDialog.Values["minimize"]=="true"&&settingsDialog.Values["notify"]=="true");
        config.Theme="dark";ApplyTheme();Render();var darkInk=((SolidColorBrush)Application.Current.Resources["Ink"]).Color;var darkPage=((SolidColorBrush)Background).Color;
        Check("dark palette complete",SystemParameters.HighContrast||((SolidColorBrush)Application.Current.Resources["Selected"]).Color==(Color)ColorConverter.ConvertFromString("#1F3B66")&&((SolidColorBrush)Application.Current.Resources["Raised"]).Color==(Color)ColorConverter.ConvertFromString("#313438")&&((SolidColorBrush)Application.Current.Resources["WarningInk"]).Color==(Color)ColorConverter.ConvertFromString("#E8B45C"));
        Check("row labels follow theme",SystemParameters.HighContrast||((SolidColorBrush)commandLabels.Values.First().Foreground).Color==(Color)ColorConverter.ConvertFromString("#C5C5C5"));
        var agentCombo=Find<ComboBox>(SlotList).First();ComboBoxItem? selectedItem=null;
        for(var i=0;i<20;i++)
        {
            // Discovery may replace the row during a render turn. Inspect the attached combo after its real popup generated containers.
            agentCombo=Find<ComboBox>(SlotList).First();agentCombo.IsDropDownOpen=true;
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(25);
            if(!Find<ComboBox>(SlotList).Contains(agentCombo))continue;
            selectedItem=agentCombo.ItemContainerGenerator.ContainerFromIndex(agentCombo.SelectedIndex) as ComboBoxItem;
            if(selectedItem is not null)break;
        }
        Check("dropdown selection container is generated on the current row",selectedItem is not null);
        var selectionBrush=Find<Border>(selectedItem!).Select(b=>b.Background).OfType<SolidColorBrush>().FirstOrDefault();
        Check("dropdown selection readable in dark",SystemParameters.HighContrast||(selectionBrush!=null&&selectionBrush.Color==(Color)ColorConverter.ConvertFromString("#1F3B66")));
        agentCombo.IsDropDownOpen=false;
        config.Theme="light";ApplyTheme();Render();var lightInk=((SolidColorBrush)Application.Current.Resources["Ink"]).Color;var lightPage=((SolidColorBrush)Background).Color;
        Check("theme resources switch",(Color)ColorConverter.ConvertFromString("#F5F5F5")==darkInk&&(Color)ColorConverter.ConvertFromString("#202020")==lightInk);
        Check("window background follows theme",(Color)ColorConverter.ConvertFromString("#202020")==darkPage&&(Color)ColorConverter.ConvertFromString("#F5F5F5")==lightPage);
        config=new Configuration();config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();await saveTask;
        await Task.Delay(1000);
        using(var process=Process.GetCurrentProcess()){process.Refresh();Store.Save(System.IO.Path.Combine(folder,"ui-idle-memory.json"),new{PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64});}
        async Task ResizeForCheckAsync(double width,double height)
        {
            Width=width;Height=height;
            var deadline=Stopwatch.StartNew();var settled=0;
            while(deadline.Elapsed<TimeSpan.FromSeconds(2))
            {
                // A Render callback can run before the native WM_SIZE message. Drain input and layout before measuring.
                await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
                var ready=Math.Abs(ActualWidth-Width)<1&&Math.Abs(ActualHeight-Height)<1&&WorkGrid.IsMeasureValid&&WorkGrid.IsArrangeValid&&ConfigurationPanel.IsArrangeValid;
                settled=ready?settled+1:0;
                if(settled>=2)return;
                await Task.Delay(25);
            }
            Store.Save(System.IO.Path.Combine(folder,"layout-timeout.json"),new{RequestedWidth=width,RequestedHeight=height,Width,Height,ActualWidth,ActualHeight,WorkGrid.IsMeasureValid,WorkGrid.IsArrangeValid});
            Check($"native resize and layout settle ({width}, {height})",false);
        }
        foreach(var variant in new[]{("light",1040d,800d),("dark",800d,650d),("light",650d,500d),("dark",1040d,800d)})
        {
            config.Theme=variant.Item1;ApplyTheme();await ResizeForCheckAsync(variant.Item2,variant.Item3);
            var point=LaunchButton.TranslatePoint(new Point(),this);Check($"launch visible {variant}",point.Y>=0&&point.Y+LaunchButton.ActualHeight<=ActualHeight&&point.X>=0&&point.X+LaunchButton.ActualWidth<=ActualWidth);
            var detailCombo=Find<ComboBox>(SlotList).FirstOrDefault();var selectedSlotIndex=detailCombo!=null&&int.TryParse(new string(AutomationProperties.GetName(detailCombo).Where(char.IsDigit).ToArray()),out var parsedSlot)?parsedSlot-1:-1;
            Store.Save(System.IO.Path.Combine(folder,$"layout-{variant.Item1}-{variant.Item2}.json"),new{RequestedWidth=variant.Item2,RequestedHeight=variant.Item3,Width,ActualWidth,Height,ActualHeight,Scale=UiScale.Value,WorkWidth=WorkGrid.ActualWidth,ConfigWidth=ConfigurationPanel.ActualWidth,SummaryWidth=SummaryPanel.ActualWidth,ConfigMargin=ConfigurationPanel.Margin.ToString(),SummaryMargin=SummaryPanel.Margin.ToString(),SummaryColumn=Grid.GetColumn(SummaryPanel),SummaryRow=Grid.GetRow(SummaryPanel),ColumnWidth=SummaryColumn.Width.ToString(),RailWidth=Rail.ActualWidth,SelectedSlotIndex=selectedSlotIndex,Dpi=VisualTreeHelper.GetDpi(this).PixelsPerInchX});
            Check($"primary configuration fills the main column without a right summary sidebar {variant}",Math.Abs(ConfigurationPanel.ActualWidth-WorkGrid.ActualWidth)<2&&SummaryColumn.Width.Value==0&&!LaunchOptionsExpander.IsExpanded&&!SummaryPanel.IsVisible);
            Check($"project accessible name {variant}",AutomationProperties.GetName(ProjectPath).Length>0);
            var expectOverflow=variant.Item2/UiScale.Value<900;
            Check($"narrow rail overflow menu {variant}",(RailOverflow.Visibility==Visibility.Visible)==expectOverflow);
            var dpi=VisualTreeHelper.GetDpi(this);var image=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);image.Render(this);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=System.IO.File.Create(System.IO.Path.Combine(folder,$"native-{variant.Item1}-{variant.Item2}.png"));encoder.Save(file);
        }
        // F5 regression: the submission gate must hold only while attempts are still Pending; a confirmed running attempt must release it for the next batch.
        Check("narrow overflow exposes rail commands",RailOverflowMenu.Items.OfType<MenuItem>().Select(i=>i.Header as string).SequenceEqual(new[]{"更换项目","资源设置","远程主机（VPS）","工作目录","恢复与诊断","运行历史与导出","恢复备份"}));
        var moreCommand=FindLogical<Button>(this).FirstOrDefault(b=>b.Content as string=="更多");
        Check("remote host command reachable from more menu",(moreCommand?.ContextMenu as ContextMenu)?.Items.OfType<MenuItem>().Any(i=>i.Header as string=="远程主机（VPS）")==true);
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
            Receipt? gateReceipt=null;for(var i=0;i<50;i++){await Task.Delay(100);gateReceipt=ReadStoreSettled<Receipt>(Store.ReceiptPath(gateItem.Id));if(gateReceipt?.Phase=="Started")break;}
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
        var originalResource=config.Draft.Resource;config.Draft.Resource=new(){Mode="custom",JobMemoryLimit=4*ResourcePolicy.GiB,PlanningBytes=ResourcePolicy.GiB,CpuRate=2500};
        using(var diagnostic=System.Text.Json.JsonDocument.Parse(DiagnosticText(false)))Check("diagnostics reports configured protection rather than fixed defaults",diagnostic.RootElement.GetProperty("Protection").GetProperty("Configured").GetProperty("JobMemoryLimit").GetUInt64()==4*ResourcePolicy.GiB&&diagnostic.RootElement.GetProperty("Protection").GetProperty("Configured").GetProperty("CpuRate").GetInt32()==2500);
        config.Draft.Resource=originalResource;
        config.Draft.Project=priorProject;
        foreach(var scale in new[]{125,150})
        {
            config.TextScalePercent=scale;config.Theme="dark";ApplyTheme();Render();await ResizeForCheckAsync(650,700);
            var buttonPoint=LaunchButton.TranslatePoint(new Point(),this);Check($"launch remains reachable at {scale}% text scale",buttonPoint.X>=0&&buttonPoint.Y>=0&&buttonPoint.X+LaunchButton.ActualWidth*UiScale.Value<=ActualWidth&&buttonPoint.Y+LaunchButton.ActualHeight*UiScale.Value<=ActualHeight);
            var scaleCombo=Find<ComboBox>(SlotList).FirstOrDefault();
            if(scaleCombo!=null){var comboOrigin=scaleCombo.TranslatePoint(new Point(),SlotList);Check($"detail combo stays inside the detail card at {scale}% text scale",comboOrigin.X>=-0.5&&comboOrigin.X+scaleCombo.ActualWidth<=SlotList.ActualWidth+0.5);}
            var dpi=VisualTreeHelper.GetDpi(this);var image=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);image.Render(this);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=System.IO.File.Create(System.IO.Path.Combine(folder,$"native-text-{scale}.png"));encoder.Save(file);
        }
        config.TextScalePercent=100;ApplyTheme();
        Check("single detail row draws no leftover separator",SlotList.Children.Count==1&&SlotList.Children[0] is Border{BorderThickness:{Bottom:0}});
        var liveBar=ContentScroll.Template.FindName("PART_VerticalScrollBar",ContentScroll) as System.Windows.Controls.Primitives.ScrollBar;
        liveBar?.ApplyTemplate();
        var liveTrack=liveBar?.Template?.FindName("PART_Track",liveBar) as System.Windows.Controls.Primitives.Track;
        var liveThumb=liveTrack?.Thumb?.Background as SolidColorBrush;var tokenThumb=(Brush)FindResource("InputLine") as SolidColorBrush;
        Check("scrollbars follow the theme tokens",liveThumb!=null&&tokenThumb!=null&&liveThumb.Color==tokenThumb.Color);
        var lockProbePath=Store.ReceiptPath(Guid.NewGuid());
        Store.Save(lockProbePath,new Receipt("Finished",0,0,0,"lock probe",DateTimeOffset.UtcNow));
        using var lockHeld=new ManualResetEventSlim(false);
        var lockHolder=Task.Run(()=>{using var held=new Mutex(false,FileMutexFor(lockProbePath));held.WaitOne();lockHeld.Set();Thread.Sleep(2300);held.ReleaseMutex();});
        lockHeld.Wait();
        var lockProbe=await Task.Run(()=>ReadStoreSettled<Receipt>(lockProbePath));
        await lockHolder;
        Check("transient store write lock is retried instead of aborting checks",lockProbe is {Phase:"Finished"});
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
        await RunTaskUsabilityChecksAsync(Check);
        await RunTaskChecksAsync(Check);
        await RunQuickQueueChecksAsync(Check);
        ReviewUiChecks.Run(Check);
        await RunReviewUiChecksAsync(Check);
        await RunMultipaneChecksAsync(Check);
        await RunSimpleUiChecksAsync(Check,folder);
        config=new Configuration();config.Draft.Project=Store.Root;Render();await CheckCommandsAsync();
        Check("rail index lists every slot with continuous names",Find<Button>(RailSlots).Count()==config.Draft.Slots.Count&&Find<Button>(RailSlots).Select(b=>AutomationProperties.GetName(b)).SequenceEqual(config.Draft.Slots.Select((_,i)=>$"第 {i+1} 项 Agent 行")));
        await SelectRailSlot(3);
        Check("rail row selection drives the detail row",AutomationProperties.GetName(Find<ComboBox>(SlotList).First())=="第 3 项 Agent"&&SlotList.Children.Count==1);
        Render();
        Check("rail keeps exactly one detail row",SlotList.Children.Count==1);
        // Rail refresh seams: the polling/task fan-outs must update the rail without a full Render(). Probes must not weaken the final launch guard, so the real snapshot is restored via finally.
        var savedStatus=status;var savedBook=taskBook;
        try
        {
            RailResourceText.Text="rail-stale-marker";
            await RefreshLocalPreviewAsync();
            Check("rail resource pill follows preview refresh",RailResourceText.Text.Contains("可用")&&RailResourceText.Text.EndsWith("GiB"));
            var railTaskId=Guid.NewGuid();
            taskBook=new(){Revision=1,Tasks=[new(){Id=railTaskId,Revision=1,Name="rail-queue-probe",Project=Store.Root}],Queue=[new(){Id=Guid.NewGuid(),TaskId=railTaskId,TaskRevision=1,Priority=1,Sequence=1,EnqueuedAt=DateTimeOffset.UtcNow,Project=Store.Root}]};
            RenderTasks();
            Check("rail queue follows task render",FindLogical<TextBlock>(RailQueue).Any(t=>t.Text.Contains("rail-queue-probe")));
            // P4 compare-group wiring: queued tasks are refused before any dialog or write.
            var compareButton=Find<Button>(TaskList).FirstOrDefault(b=>AutomationProperties.GetName(b)=="复制为对比组 rail-queue-probe");
            if(compareButton!=null){ErrorText.Text="";compareButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check("queued task is refused from compare grouping",ErrorText.Text.Contains("正在队列中"));}
            status=new(true,"rail probe",[new(){Plan=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"rail-session-probe",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new("cmd.exe",[],"exe"),ResourcePolicy.AgentBudget),State="Started"}],new MemorySnapshot(16UL*ResourcePolicy.GiB,7UL*ResourcePolicy.GiB,32UL*ResourcePolicy.GiB,20UL*ResourcePolicy.GiB,12.5,DateTimeOffset.UtcNow));
            RenderMonitorStatus();
            Check("rail sessions follow monitor refresh",FindLogical<TextBlock>(RailSessions).Any(t=>t.Text.Contains("rail-session-probe")));
            var focusButton=Find<Button>(Sessions).FirstOrDefault(b=>AutomationProperties.GetName(b)=="聚焦终端 rail-session-probe");
            Check("active session offers terminal window recall",focusButton!=null);
            if(focusButton!=null){ProgressText.Text="";focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check("missing terminal window reports instead of failing",ProgressText.Text.Contains("未找到该批次"));}
            // P2 session-card wiring: settled agent entries expose read-only change summary; review routing follows the linked run's terminal transition without any monitor-entry change.
            var settledAttempt=Guid.NewGuid();var settledTask=Guid.NewGuid();
            status=new(true,"settled probe",[new(){Plan=new LaunchItem(settledAttempt,Guid.NewGuid(),1,"settled-session-probe",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new("cmd.exe",[],"exe"),ResourcePolicy.AgentBudget),State="Settled"}],new MemorySnapshot(16UL*ResourcePolicy.GiB,7UL*ResourcePolicy.GiB,32UL*ResourcePolicy.GiB,20UL*ResourcePolicy.GiB,12.5,DateTimeOffset.UtcNow));
            taskBook=new(){Revision=1,Tasks=[new(){Id=settledTask,Revision=1,Name="settled-task-probe",Project=Store.Root}],Attempts=[new(){TaskId=settledTask,TaskRevision=1,AttemptId=settledAttempt,BatchId=Guid.NewGuid(),State="Running",At=DateTimeOffset.UtcNow}]};
            RenderMonitorStatus();
            var viewChanges=Find<Button>(Sessions).FirstOrDefault(b=>AutomationProperties.GetName(b)=="查看变更 settled-session-probe");
            Check("settled session offers change summary while run is not terminal",viewChanges!=null);
            Check("review routing hidden while linked run is still running",Find<Button>(Sessions).All(b=>AutomationProperties.GetName(b)!="审阅改动 settled-session-probe"));
            // The run alone turns terminal (FinishRun); the monitor entries stay identical, so only the signature's review bit may flip.
            taskBook=new(){Revision=2,Tasks=[new(){Id=settledTask,Revision=1,Name="settled-task-probe",Project=Store.Root}],Attempts=[new(){TaskId=settledTask,TaskRevision=1,AttemptId=settledAttempt,BatchId=Guid.NewGuid(),State="Review",At=DateTimeOffset.UtcNow}]};
            RenderMonitorStatus();
            var reviewLink=Find<Button>(Sessions).FirstOrDefault(b=>AutomationProperties.GetName(b)=="审阅改动 settled-session-probe");
            Check("review routing appears when linked run turns terminal",reviewLink!=null);
            if(viewChanges!=null)
            {
                ErrorText.Text="";viewChanges.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var waitCycles=0;while(ErrorText.Text.Length==0&&waitCycles++<50)await Task.Delay(100);
                Check("non-git worktree reports a readable change-summary error",ErrorText.Text.Contains("查看变更失败"));
            }
            // The dot must resolve the shared state brushes, not a snapshot color, and the summaries must stay bounded to three rows plus one overflow row.
            bool SameBrush(Brush? actual,Brush expected)=>ReferenceEquals(actual,expected)||actual is SolidColorBrush actualColor&&expected is SolidColorBrush expectedColor&&actualColor.Color==expectedColor.Color;
            var dotSlot=config.Draft.Slots.First(s=>s.Enabled);var dotWasEnabled=dotSlot.Enabled;
            // Render() rebuilds railDots; each mapping is read synchronously so a trailing command check cannot overwrite the probe.
            dotSlot.Enabled=true;Render();
            var dotMappedOk=SameBrush(railDots[dotSlot.Id].Fill,(Brush)FindResource("StatusOk"));
            dotSlot.Enabled=false;Render();
            var dotMappedOff=SameBrush(railDots[dotSlot.Id].Fill,(Brush)FindResource("StatusOff"));
            dotSlot.Enabled=dotWasEnabled;Render();
            Check("rail status dot maps to state brushes",dotMappedOk&&dotMappedOff);
            var railNow=DateTimeOffset.UtcNow;
            status=new(true,"rail summary probe",Enumerable.Range(0,5).Select(i=>new ControlEntry{Plan=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),i+1,$"rail-summary-{i}",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new("cmd.exe",[],"exe"),ResourcePolicy.AgentBudget,Kind:"agent"),State="Started",Created=railNow.AddMinutes(-i)}).ToArray(),new MemorySnapshot(16UL*ResourcePolicy.GiB,7UL*ResourcePolicy.GiB,32UL*ResourcePolicy.GiB,20UL*ResourcePolicy.GiB,12.5,railNow));
            RenderMonitorStatus();
            var sessionsTruncated=RailSessions.Children.Count==4&&RailSessions.Children[3] is Button sessionsMore&&sessionsMore.Content is string sessionsLabel&&sessionsLabel.Contains("全部");
            var summaryTasks=Enumerable.Range(0,5).Select(i=>new TaskCard{Id=Guid.NewGuid(),Revision=1,Name=$"rail-summary-task-{i}",Project=Store.Root}).ToArray();
            taskBook=new(){Revision=1,Tasks=[..summaryTasks],Queue=[..summaryTasks.Select((task,i)=>new QueueEntry{Id=Guid.NewGuid(),TaskId=task.Id,TaskRevision=1,Priority=1,Sequence=i+1,EnqueuedAt=railNow,Project=Store.Root})]};
            RenderTasks();
            var queueTruncated=RailQueue.Children.Count==4&&RailQueue.Children[3] is Button queueMore&&queueMore.Content is string queueLabel&&queueLabel.Contains("全部");
            Check("rail summaries truncate with overflow entry",sessionsTruncated&&queueTruncated);
            var sessionExpandBefore=SessionExpander.IsExpanded;var taskExpandBefore=TaskExpander.IsExpanded;
            if(RailSessions.Children[3] is Button jumpSessions){jumpSessions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);var sessionPoint=SessionExpander.TranslatePoint(new Point(),ContentScroll);Check("rail session overflow jumps to the session list",SessionExpander.IsExpanded&&sessionPoint.Y>=-4&&sessionPoint.Y<=ContentScroll.ViewportHeight-36);}
            if(RailQueue.Children[3] is Button jumpQueue){jumpQueue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);var queuePoint=TaskExpander.TranslatePoint(new Point(),ContentScroll);Check("rail queue overflow jumps to the task queue",TaskExpander.IsExpanded&&queuePoint.Y>=-4&&queuePoint.Y<=ContentScroll.ViewportHeight-36);}
            SessionExpander.IsExpanded=sessionExpandBefore;TaskExpander.IsExpanded=taskExpandBefore;
        }
        finally
        {
            taskBook=savedBook;RenderTasks();
            status=savedStatus;RenderMonitorStatus();
        }
        await RunQuickActionsChecksAsync(folder,Check,ResizeForCheckAsync);
        await RunSlotConvenienceChecksAsync(folder,Check,ResizeForCheckAsync);
        await RunPracticalFixChecksAsync(folder,Check);
        // P1 session notifications: runtime transitions only, deduped, rate-limited and path-safe.
        var notifier = SessionNotifier.For(this);
        notifier.Reset(); notifier.TestMode = true;
        var notifyAt = DateTimeOffset.UtcNow;
        notifier.Now = () => notifyAt;
        ControlEntry N(Guid id, string state, string detail) => new()
        {
            Plan = new LaunchItem(id, Guid.NewGuid(), 1, "会话A", Store.Root, PhysicalDirectory.Workspace(Store.Root), false, new("cmd.exe", ["--secret-arg"], "exe"), ResourcePolicy.AgentBudget),
            State = state, Detail = detail, Created = notifyAt
        };
        var nId = Guid.NewGuid(); const string finishedDetail = "全部受管进程已退出。退出码 3；目录 C:\\Users\\me\\proj；参数 --secret-arg";
        notifier.Observe([N(nId, "Started", "已启动进程")], true);
        Check("notification baseline refresh is silent", notifier.Delivered.Count == 0);
        notifier.Observe([N(nId, "Settled", finishedDetail)], true);
        Check("finished session notifies once with exit code", notifier.Delivered.Count == 1 && notifier.Delivered[0].Category == "finished" && notifier.Delivered[0].Message.Contains("退出码 3"));
        var toastText = string.Join(" ", notifier.Delivered.Select(d => d.Title + " " + d.Message));
        Check("notification text excludes arguments and full paths", !toastText.Contains("--secret-arg") && !toastText.Contains("C:\\") && !toastText.Contains(@"\Users\") && !toastText.Contains(Store.Root));
        notifier.Observe([N(nId, "Settled", finishedDetail)], true);
        Check("identical session event is not notified twice", notifier.Delivered.Count == 1);
        var mId = Guid.NewGuid();
        notifier.Observe([N(nId, "Settled", finishedDetail), N(mId, "Started", "已启动进程")], true);
        notifier.Observe([N(nId, "Settled", finishedDetail), N(mId, "Started", "共享池内存分配触及上限；分配被拒绝")], true);
        Check("memory touch notifies with shared pool wording", notifier.Delivered.Count == 2 && notifier.Delivered[1].Category == "memory" && notifier.Delivered[1].Message.Contains("共享池"));
        var f1 = Guid.NewGuid(); var f2 = Guid.NewGuid();
        notifier.Observe([N(f1, "Pending", "等待资源"), N(f2, "Pending", "等待资源")], true);
        notifier.Observe([N(f1, "FailedBeforeStart", "未启动：失败A"), N(f2, "Pending", "等待资源")], true);
        notifier.Observe([N(f2, "FailedBeforeStart", "未启动：失败B")], true);
        Check("same-category notifications are rate limited to one per two seconds", notifier.Delivered.Count(d => d.Category == "failed") == 1);
        var qId = Guid.NewGuid();
        notifier.Observe([N(qId, "Pending", "等待资源")], true);
        notifier.Observe([N(qId, "Started", "已启动进程")], true);
        Check("queue wait release notifies started", notifier.Delivered.Any(d => d.Category == "started"));
        var offId = Guid.NewGuid();
        notifier.Observe([N(offId, "Pending", "等待资源")], false);
        var beforeOff = notifier.Delivered.Count;
        notifier.Observe([N(offId, "FailedBeforeStart", "未启动：失败C")], false);
        Check("disabled setting suppresses notifications", notifier.Delivered.Count == beforeOff);
        notifier.Now = () => DateTimeOffset.UtcNow; notifier.TestMode = false;
        var pids=status?.Entries?.Where(e=>!Store.IsTerminal(e)).Count()??0;Check("UI checks do not launch agents",pids==0);
        Store.Save(System.IO.Path.Combine(folder,"ui-checks.json"),new{passed=checks,Dpi=VisualTreeHelper.GetDpi(this).PixelsPerInchX});Close();
    }
}
#endif
