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
        // The harness may be launched from a non-interactive/background process whose startup show state is minimized;
        // focus and hit-testing assertions require a normal, active window (production starts visible and normal).
        if (WindowState != WindowState.Normal) { Show(); WindowState = WindowState.Normal; }
        Activate();
        void Check(string name,bool value)
        {
            if(!value){Store.Save(System.IO.Path.Combine(folder,"ui-checks.json"),new{passed=checks,failed=name});throw new Exception("UI regression: "+name);}
            checks.Add(name);
            // Record every boundary so a later layout stall is attributed to the actual last completed check.
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder,"ui-progress.json"),System.Text.Json.JsonSerializer.Serialize(new{LastPassed=name,Count=checks.Count,At=DateTimeOffset.UtcNow}));
        }
        async Task CheckWindowPlacementRoundTripAsync()
        {
            // Native WM_SIZE transitions lag the WPF property under load. Wait for the HWND state so a late
            // minimize message cannot land after the fixture restores the window and break later focus checks.
            async Task WaitForNativeStateAsync(bool iconic,bool zoomed=false)
            {
                var deadline=Stopwatch.StartNew();
                while(deadline.Elapsed<TimeSpan.FromSeconds(3))
                {
                    if(windowHandle!=IntPtr.Zero&&WindowPlacementNative.IsIconic(windowHandle)==iconic&&WindowPlacementNative.IsZoomed(windowHandle)==zoomed)return;
                    await Task.Delay(25);
                }
            }
            var path=Store.FilePath("window-placement.json");var backupPath=path+".bak";
            var savedPlacement=System.IO.File.Exists(path)?System.IO.File.ReadAllBytes(path):null;
            var savedBackup=System.IO.File.Exists(backupPath)?System.IO.File.ReadAllBytes(backupPath):null;
            var originalState=WindowState;var originalStartup=WindowStartupLocation;var originalLeft=Left;var originalTop=Top;var originalWidth=Width;var originalHeight=Height;
            var originalMinWidth=MinWidth;var originalMinHeight=MinHeight;
            var originalWindowedBounds=windowedBounds;var originalLastNonMinimizedState=lastNonMinimizedState;
            try
            {
                var screen=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
                var testWidth=Math.Min(960,screen.Width-64);var testHeight=Math.Min(700,screen.Height-64);
                if(testWidth<MinWidth||testHeight<MinHeight)throw new InvalidOperationException("屏幕尺寸不足以验证窗口位置记忆。");
                WindowState=WindowState.Normal;WindowStartupLocation=WindowStartupLocation.Manual;
                Left=Math.Clamp(screen.Left+24,screen.Left,screen.Right-testWidth);Top=Math.Clamp(screen.Top+24,screen.Top,screen.Bottom-testHeight);Width=testWidth;Height=testHeight;
                await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(100);
                var expected=new Rect(Left,Top,Width,Height);
                WindowState=WindowState.Maximized;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(100);
                WindowState=WindowState.Minimized;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await WaitForNativeStateAsync(true);
                SaveWindowPlacement();
                var actual=Store.Read<WindowPlacement>(path);
                Check("UX16 minimized placement retains normal position size and max state",actual is {Maximized:true}&&Math.Abs(actual.Left-expected.Left)<0.5&&Math.Abs(actual.Top-expected.Top)<0.5&&Math.Abs(actual.Width-expected.Width)<0.5&&Math.Abs(actual.Height-expected.Height)<0.5);
                RestoreBackground();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
                Check("UX16 restoring a minimized maximized window keeps its max state",WindowState==WindowState.Maximized);
                EnterBackground();
                Check("UX16 backgrounding a maximized window keeps it maximized",!IsVisible&&WindowState==WindowState.Maximized);
                RestoreBackground();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);SaveWindowPlacement();
                Check("UX16 background restore and tray exit preserve maximized placement",IsVisible&&WindowState==WindowState.Maximized&&Store.Read<WindowPlacement>(path) is {Maximized:true});
                WindowState=WindowState.Normal;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
                EnterBackground();RestoreBackground();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);SaveWindowPlacement();
                Check("UX16 background restore keeps a normal window normal",IsVisible&&WindowState==WindowState.Normal&&Store.Read<WindowPlacement>(path) is {Maximized:false});
                if(!TryGetWindowWorkArea(out var work))throw new InvalidOperationException("无法读取测试窗口工作区。");
                Store.Save(path,new WindowPlacement(work.Left,work.Top,work.Width,work.Height+128,false));
                ApplyWindowPlacement();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
                Check("UX16 restored normal bounds fit the selected monitor work area",Math.Abs(Left-work.Left)<0.5&&Math.Abs(Top-work.Top)<0.5&&Math.Abs(Width-work.Width)<0.5&&Math.Abs(Height-work.Height)<0.5);
                Width=testWidth;Height=testHeight;Left=expected.Left;Top=expected.Top;
                await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
                Store.Save(path,new WindowPlacement(work.Left,work.Top,work.Width,work.Height+128,true));
                ApplyWindowPlacement();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);SaveWindowPlacement();
                var maxPlacement=Store.Read<WindowPlacement>(path);
                Check("UX16 maximized restore retains work-area-safe windowed bounds",WindowState==WindowState.Maximized&&maxPlacement is {Maximized:true}&&Math.Abs(maxPlacement.Left-work.Left)<0.5&&Math.Abs(maxPlacement.Top-work.Top)<0.5&&Math.Abs(maxPlacement.Width-work.Width)<0.5&&Math.Abs(maxPlacement.Height-work.Height)<0.5);
                var compactWidth=Math.Max(MinWidth,Math.Min(800,work.Width-64));var compactHeight=Math.Max(MinHeight,Math.Min(600,work.Height-64));
                var compact=new Rect(work.Left+(work.Width-compactWidth)/2,work.Top+(work.Height-compactHeight)/2,compactWidth,compactHeight);
                Store.Save(path,new WindowPlacement(compact.Left,compact.Top,compact.Width,compact.Height,true));
                ApplyWindowPlacement();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);SaveWindowPlacement();
                var compactPlacement=Store.Read<WindowPlacement>(path);
                Check("UX16 applying distinct bounds while maximized preserves the requested geometry",WindowState==WindowState.Maximized&&compactPlacement is {Maximized:true}&&Math.Abs(compactPlacement.Left-compact.Left)<0.5&&Math.Abs(compactPlacement.Top-compact.Top)<0.5&&Math.Abs(compactPlacement.Width-compact.Width)<0.5&&Math.Abs(compactPlacement.Height-compact.Height)<0.5);
                Store.Save(path,new WindowPlacement(work.Left,work.Top,work.Width,work.Height+128,false));
                var beforeShow=new MainWindow([]);
                try
                {
                    new System.Windows.Interop.WindowInteropHelper(beforeShow).EnsureHandle();
                    if(!beforeShow.TryGetWindowWorkArea(out var sourceWork))throw new InvalidOperationException("无法读取未显示窗口工作区。");
                    Check("UX16 source initialization fits saved bounds before first show",!beforeShow.IsVisible&&beforeShow.Left>=sourceWork.Left-0.5&&beforeShow.Top>=sourceWork.Top-0.5&&beforeShow.Left+beforeShow.Width<=sourceWork.Right+0.5&&beforeShow.Top+beforeShow.Height<=sourceWork.Bottom+0.5);
                }
                finally{beforeShow.closing=true;beforeShow.Close();}
            }
            finally
            {
                static void RestoreFile(string file,byte[]? bytes){if(bytes is null){if(System.IO.File.Exists(file))System.IO.File.Delete(file);}else System.IO.File.WriteAllBytes(file,bytes);}
                RestoreFile(path,savedPlacement);RestoreFile(backupPath,savedBackup);
                if(!IsVisible)RestoreBackground();
                MinWidth=originalMinWidth;MinHeight=originalMinHeight;
                WindowState=WindowState.Normal;WindowStartupLocation=originalStartup;Left=originalLeft;Top=originalTop;Width=originalWidth;Height=originalHeight;WindowState=originalState;
                await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
                await WaitForNativeStateAsync(false,originalState==WindowState.Maximized);
                windowedBounds=originalWindowedBounds;lastNonMinimizedState=originalLastNonMinimizedState;
            }
        }
        IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
        {
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T target)yield return target;foreach(var nested in Find<T>(child))yield return nested;}
        }
        ComboBox AgentCombo()=>Find<ComboBox>(SlotList).First(c=>AutomationProperties.GetName(c).Contains("项 Agent"));
        ComboBox PlaceCombo()=>Find<ComboBox>(SlotList).First(c=>AutomationProperties.GetName(c).Contains("运行位置"));
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
        await CheckWindowPlacementRoundTripAsync();
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
        var select=AgentCombo();select.SelectedValue="local:codex";Check("native Agent selector separates profile",config.Draft.Slots.Single(s=>s.Id==firstId).Current.Executable=="");
        select=AgentCombo();select.SelectedValue="local:pi";Check("native Agent selector restores profile",config.Draft.Slots.Single(s=>s.Id==firstId).Current.Arguments.SequenceEqual(new[]{"--only-pi"}));
        var cloudHost=new RemoteHost(Guid.NewGuid(),"候选云 VPS","choices.example.com",22,"root","agent",["pi","kimi"],null);
        config.RemoteHosts=[cloudHost];Render();
        await SelectRailSlot(2);
        Check("UX14 agent list does not mix cloud hosts",AgentCombo().Items.OfType<AgentChoice>().All(c=>c.HostId==null));
        var cloudPlace=PlaceCombo();
        Check("UX14 unbound slot lists the cloud place separately",cloudPlace.Items.OfType<PlaceChoice>().Any(c=>c.HostId==cloudHost.Id));
        cloudPlace.SelectedValue=cloudHost.Id.ToString();
        var boundSlot=config.Draft.Slots[1];
        Check("UX14 picking cloud place binds host and derives remote dir",boundSlot.RemoteHostId==cloudHost.Id&&boundSlot.AgentId=="pi"&&RemoteWorkspaceMappingHelper.IsValidRemoteDirectory(boundSlot.RemoteDirectory??"")&&boundSlot.RemoteDirectory!.StartsWith("~/work/"));
        PlaceCombo().SelectedValue="local";
        Check("UX14 picking local place unbinds host",config.Draft.Slots[1].RemoteHostId==null&&config.Draft.Slots[1].RemoteDirectory==null);
        Check("UX15 shared directory risk stays outside the collapsed options",SharedDirectoryRisk.Visibility==Visibility.Collapsed&&!LaunchOptionsExpander.IsExpanded);
        Edit(d=>d.DirectoryMode="shared");
        Check("UX15 shared directory risk is visible while options stay collapsed",SharedDirectoryRisk.Visibility==Visibility.Visible&&SharedDirectoryRiskText.Text.Contains("互相覆盖")&&!LaunchOptionsExpander.IsExpanded);
        Edit(d=>d.DirectoryMode="isolated");
        var otherHost=new RemoteHost(Guid.NewGuid(),"另一云","other.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.None);
        config.RemoteHosts=[cloudHost,otherHost];
        Edit(d=>{d.Slots[0].Enabled=true;d.Slots[0].RemoteHostId=cloudHost.Id;d.Slots[1].Enabled=true;d.Slots[1].RemoteHostId=otherHost.Id;d.Slots[1].RemoteDirectory="~/work/other";});
        var mixedLabel=LaunchButton.Content as string??"";
        Check("UX15 mixed hosts do not collapse to one target",mixedLabel.Contains("多个目标")&&!mixedLabel.Contains(cloudHost.Name)&&!mixedLabel.Contains(otherHost.Name));
        await SelectRailSlot(2);
        var noneNote=Find<TextBlock>(SlotList).FirstOrDefault(t=>AutomationProperties.GetName(t)=="同步说明 "+config.Draft.Slots[1].DisplayName);
        Check("UX15 none says this run is not peer and is refused",noneNote!=null&&noneNote.Text.Contains("这次不是同权")&&noneNote.Text.Contains("拒收")&&!noneNote.Text.Contains("仍可启动"));
        config.RemoteHosts=[];Edit(d=>{foreach(var s in d.Slots){s.RemoteHostId=null;s.RemoteDirectory=null;s.Enabled=s==d.Slots[0];}});
        await SelectRailSlot(1);
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
        Check("dark palette complete",SystemParameters.HighContrast||((SolidColorBrush)Application.Current.Resources["Selected"]).Color==(Color)ColorConverter.ConvertFromString("#1E2A3F")&&((SolidColorBrush)Application.Current.Resources["Raised"]).Color==(Color)ColorConverter.ConvertFromString("#232733")&&((SolidColorBrush)Application.Current.Resources["WarningInk"]).Color==(Color)ColorConverter.ConvertFromString("#E8B45C"));
        Check("row labels follow theme",SystemParameters.HighContrast||((SolidColorBrush)commandLabels.Values.First().Foreground).Color==(Color)ColorConverter.ConvertFromString("#9AA3B2"));
        var agentCombo=AgentCombo();ComboBoxItem? selectedItem=null;
        for(var i=0;i<20;i++)
        {
            // Discovery may replace the row during a render turn. Inspect the attached combo after its real popup generated containers.
            agentCombo=AgentCombo();agentCombo.IsDropDownOpen=true;
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);await Task.Delay(25);
            if(!Find<ComboBox>(SlotList).Contains(agentCombo))continue;
            selectedItem=agentCombo.ItemContainerGenerator.ContainerFromIndex(agentCombo.SelectedIndex) as ComboBoxItem;
            if(selectedItem is not null)break;
        }
        Check("dropdown selection container is generated on the current row",selectedItem is not null);
        var selectionBrush=Find<Border>(selectedItem!).Select(b=>b.Background).OfType<SolidColorBrush>().FirstOrDefault();
        Check("dropdown selection readable in dark",SystemParameters.HighContrast||(selectionBrush!=null&&selectionBrush.Color==(Color)ColorConverter.ConvertFromString("#1E2A3F")));
        agentCombo.IsDropDownOpen=false;
        config.Theme="light";ApplyTheme();Render();var lightInk=((SolidColorBrush)Application.Current.Resources["Ink"]).Color;var lightPage=((SolidColorBrush)Background).Color;
        Check("theme resources switch",(Color)ColorConverter.ConvertFromString("#ECEFF5")==darkInk&&(Color)ColorConverter.ConvertFromString("#1F2430")==lightInk);
        Check("window background follows theme",(Color)ColorConverter.ConvertFromString("#14161C")==darkPage&&(Color)ColorConverter.ConvertFromString("#F4F6FB")==lightPage);
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
            var detailCombo=AgentCombo();var selectedSlotIndex=detailCombo!=null&&int.TryParse(new string(AutomationProperties.GetName(detailCombo).Where(char.IsDigit).ToArray()),out var parsedSlot)?parsedSlot-1:-1;
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
            foreach(var scaleCombo in Find<ComboBox>(SlotList)){var comboOrigin=scaleCombo.TranslatePoint(new Point(),SlotList);Check($"detail combo stays inside the detail card at {scale}% text scale",comboOrigin.X>=-0.5&&comboOrigin.X+scaleCombo.ActualWidth<=SlotList.ActualWidth+0.5);}
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
        Check("rail row selection drives the detail row",AutomationProperties.GetName(AgentCombo())=="第 3 项 Agent"&&AutomationProperties.GetName(PlaceCombo())=="第 3 项运行位置"&&SlotList.Children.Count==1);
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
            var focusButton=Find<Button>(ActiveSessions).FirstOrDefault(b=>AutomationProperties.GetName(b)=="聚焦终端 rail-session-probe");
            Check("active session offers terminal window recall",focusButton!=null);
            if(focusButton!=null){ProgressText.Text="";focusButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));for(var i=0;i<50&&ProgressText.Text.Length==0;i++)await Task.Delay(100);Check("missing terminal window reports instead of failing",ProgressText.Text.Contains("未找到该批次"));}
            // R28 operator-confirmed release: Disconnected remote entries expose judgment + reason + second confirm;
            // non-Gone / missing reason / unconfirmed paths must never reach the release request (hermetic, no monitor IPC).
            var releaseAttempt=Guid.NewGuid();
            status=new(true,"remote release probe",[new(){Plan=new LaunchItem(releaseAttempt,Guid.NewGuid(),1,"remote-release-probe",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new("pi",[],"remote"),0,RemoteExecution:new RemoteExecutionSpec("release.example.com",22,"root","agent","pi","/home/agent/work",[],null,null)),State="Disconnected",Detail="SSH 连接断开（退出码 255）；远程进程可能仍在运行。"}],new MemorySnapshot(16UL*ResourcePolicy.GiB,7UL*ResourcePolicy.GiB,32UL*ResourcePolicy.GiB,20UL*ResourcePolicy.GiB,12.5,DateTimeOffset.UtcNow));
            RenderMonitorStatus();
            var releaseButton=Find<Button>(ActiveSessions).FirstOrDefault(b=>AutomationProperties.GetName(b)=="核对并确认释放 remote-release-probe");
            Check("disconnected remote session offers operator release entry",releaseButton!=null);
            var releaseEntry=status.Entries!.Single(e=>e.Plan.Id==releaseAttempt);
            var releaseDialog=CreateRemoteReleaseDialog(releaseEntry);
            string? defaultJudgment=null;
            releaseDialog.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{
                var judgment=FindLogical<ComboBox>(releaseDialog).Single(c=>AutomationProperties.GetName(c)=="远程进程判断");
                defaultJudgment=(judgment.SelectedItem as ComboBoxItem)?.Tag as string;
                judgment.SelectedIndex=0;
                FindLogical<TextBox>(releaseDialog).Single(t=>AutomationProperties.GetName(t)=="释放原因（必填）").Text="远程仍在运行";
                FindLogical<Button>(releaseDialog).Single(b=>b.Content as string=="提交判断").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            var releaseApplied=releaseDialog.ShowDialog();
            var releaseNote=FindLogical<TextBlock>(releaseDialog).FirstOrDefault(t=>t.Text.Contains("release.example.com"));
            Check("release dialog defaults to Unknown and records judgment and reason",releaseApplied==true&&defaultJudgment=="Unknown"&&releaseDialog.Values["judgment"]=="Running"&&releaseDialog.Values["reason"]=="远程仍在运行");
            Check("release dialog names host and remote directory",releaseNote!=null&&releaseNote.Text.Contains("/home/agent/work"));
            Check("release dialog keeps Enter apply and Esc cancel",FindLogical<Button>(releaseDialog).Single(b=>b.Content as string=="提交判断").IsDefault&&FindLogical<Button>(releaseDialog).Single(b=>b.Content as string=="取消").IsCancel);
            var priorConfirm=TestConfirm;var releaseConfirms=0;var releasePrompt="";
            try
            {
                TestConfirm=(text,_,_)=>{releaseConfirms++;releasePrompt=text;return MessageBoxResult.No;};
                ProgressText.Text="";await SubmitRemoteReleaseAsync(releaseEntry,"Running","进程仍在运行");
                Check("Running judgment never asks to release",releaseConfirms==0&&ProgressText.Text.Contains("不释放"));
                ProgressText.Text="";await SubmitRemoteReleaseAsync(releaseEntry,"Unknown","无法判断");
                Check("Unknown judgment never asks to release",releaseConfirms==0&&ProgressText.Text.Contains("不释放"));
                ProgressText.Text="";await SubmitRemoteReleaseAsync(releaseEntry,"Gone","");
                Check("missing reason refuses before second confirm",releaseConfirms==0&&ProgressText.Text.Contains("释放原因"));
                ProgressText.Text="";await SubmitRemoteReleaseAsync(releaseEntry,"Gone",new string('x',2049));
                Check("overlong reason refuses before second confirm",releaseConfirms==0&&ProgressText.Text.Contains("2048"));
                ProgressText.Text="";await SubmitRemoteReleaseAsync(releaseEntry,"Gone","已核对，进程已退出");
                Check("Gone judgment requires second confirm and No keeps the entry",releaseConfirms==1&&ProgressText.Text.Contains("已取消二次确认")&&releaseEntry.State=="Disconnected");
                Check("second confirm names host and never ends remote processes",releasePrompt.Contains("release.example.com")&&releasePrompt.Contains("不会结束远程主机"));
                Check("release confirm does not start reattach",releasePrompt.Contains("也不会重新挂接"));
            }
            finally{TestConfirm=priorConfirm;ProgressText.Text="";}
            var reattachButton=Find<Button>(ActiveSessions).FirstOrDefault(b=>AutomationProperties.GetName(b)=="核对并确认重新挂接 remote-release-probe");
            Check("disconnected remote session offers reattach beside release",reattachButton!=null&&releaseButton!=null&&(reattachButton.Content as string)!=(releaseButton.Content as string));
            Check("UX15 reattach and release effects stay in separate sentences",Find<TextBlock>(ActiveSessions).Any(t=>t.Text.Contains("新的 SSH 窗格"))&&Find<TextBlock>(ActiveSessions).Any(t=>t.Text.Contains("不会结束远端进程")&&t.Text.Contains("也不会重新挂接")));
            var narrowWidth=Width;Width=650;
            for(var i=0;i<20&&ActualWidth>=900;i++){await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);await Task.Delay(25);}
            Check("UX15 disconnected actions stay reachable when the rail sessions hide",ActiveNow.Visibility==Visibility.Visible&&RailSessionsSection.Visibility==Visibility.Collapsed&&releaseButton!.IsVisible&&reattachButton!.IsVisible);
            Width=narrowWidth;await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);
            var reattachDialog=CreateRemoteReattachDialog(releaseEntry);
            // 取消钮只标了 IsCancel；测试里直接 RaiseEvent(Click) 不会走 Button.OnClick，对话框关不上，整段 UI 会卡到 90 秒。
            // 与释放对话框相同，点会设置 DialogResult 的「提交挂接」。二次确认取消仍由下面的 SubmitRemoteReattachAsync 覆盖。
            reattachDialog.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>FindLogical<Button>(reattachDialog).Single(b=>b.Content as string=="提交挂接").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            var reattachApplied=reattachDialog.ShowDialog();
            var reattachNote=FindLogical<TextBlock>(reattachDialog).FirstOrDefault(t=>t.Text.Contains("release.example.com"));
            Check("reattach dialog names host, directory, and refuses to pretend sync",reattachApplied==true&&reattachNote!=null&&reattachNote.Text.Contains("/home/agent/work")&&reattachNote.Text.Contains("不会把它说成已同步")&&reattachNote.Text.Contains("不会自动重连"));
            var reattachConfirms=0;var reattachPrompt="";
            try
            {
                TestConfirm=(text,_,_)=>{reattachConfirms++;reattachPrompt=text;return MessageBoxResult.No;};
                ProgressText.Text="";ErrorText.Text="";await SubmitRemoteReattachAsync(releaseEntry);
                Check("reattach requires second confirm and No does not change the entry",reattachConfirms==1&&ProgressText.Text.Contains("已取消二次确认")&&releaseEntry.State=="Disconnected"&&ErrorText.Text.Length==0);
                Check("reattach confirm is distinct from release",reattachPrompt.Contains("release.example.com")&&reattachPrompt.Contains("不释放占用")&&reattachPrompt.Contains("不会自动重连")&&!reattachPrompt.Contains("operator-release.json"));
            }
            finally{TestConfirm=priorConfirm;ProgressText.Text="";ErrorText.Text="";}
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
            if(RailSessions.Children[3] is Button jumpSessions){jumpSessions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.ApplicationIdle);var nowPoint=ActiveNow.TranslatePoint(new Point(),ContentScroll);Check("rail session overflow jumps to the current sessions",ActiveNow.Visibility==Visibility.Visible&&nowPoint.Y>=-4&&nowPoint.Y<=ContentScroll.ViewportHeight-36);}
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
        await RunInstalledAgentsChecksAsync(folder, Check);
        await RunSyncReviewUiChecksAsync(folder,Check);
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

    // A1 同步徽章终态回退 / B3 会话名持久化恢复 / B1 逐文件只读 diff 审阅的 UI 回归。
    // 不依赖 mutagen/VPS：list 失败或无匹配会话时徽章必须走 sync-final 回退链；git 夹具在隔离测试根内构造。
    private async Task RunSyncReviewUiChecksAsync(string folder,Action<string,bool> check)
    {
        var originalConfig=config;var originalStatus=status;var polling=timer.IsEnabled;var memoryPolling=memoryTimer.IsEnabled;
        timer.Stop();memoryTimer.Stop();await saveTask;
        for(var i=0;syncProbing&&i<600;i++)await Task.Delay(20);
        check("R39 controlled sync fixtures start without an in-flight probe",!syncProbing);
        IEnumerable<T> FindLogical<T>(DependencyObject root) where T:DependencyObject
        {
            foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){if(child is T target)yield return target;foreach(var nested in FindLogical<T>(child))yield return nested;}
        }
        try
        {
            var git=Commands.FindExe("git.exe")??throw new System.IO.IOException("同步审阅 UI 检查需要 Git。");
            var repo=System.IO.Path.Combine(Store.Root,"sync-review-repo");System.IO.Directory.CreateDirectory(repo);
            async Task Git(params string[] args)
            {
                var result=await AgentProbe.RunAsync(new(git,args,"exe"),repo,TimeSpan.FromSeconds(15),64*1024);
                if(result.TimedOut||result.ExitCode!=0)throw new System.IO.IOException("同步审阅夹具 git 失败："+result.Error);
            }
            await Git("init","-q");
            var tracked=System.IO.Path.Combine(repo,"tracked.txt");
            await System.IO.File.WriteAllTextAsync(tracked,string.Join("\n",Enumerable.Range(1,500).Select(i=>"line "+i))+"\n");
            await Git("add","--","tracked.txt");
            await Git("-c","user.email=fixture@example.com","-c","user.name=fixture","commit","-qm","init");
            await System.IO.File.WriteAllTextAsync(tracked,string.Join("\n",Enumerable.Range(1,500).Select(i=>"changed "+i))+"\n");
            var small=System.IO.Path.Combine(repo,"small.txt");
            await System.IO.File.WriteAllTextAsync(small,"base\n");
            await Git("add","--","small.txt");
            await Git("-c","user.email=fixture@example.com","-c","user.name=fixture","commit","-qm","small");
            await System.IO.File.WriteAllTextAsync(small,"base\nchanged line\n");
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(repo,"新增.txt"),"untracked\n");
            var host=new RemoteHost(Guid.NewGuid(),"同步审阅 VPS","sync-review.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.Mutagen);
            config=new Configuration();config.RemoteHosts=[host];
            config.Edit(d=>{d.Project=repo;d.Slots.ForEach(s=>s.Enabled=false);d.Slots[0].Enabled=true;d.Slots[0].Directory=repo;d.Slots[0].RemoteHostId=host.Id;d.Slots[0].RemoteDirectory="/home/agent/work";});
            Render();
            var railButton=FindLogical<Button>(RailSlots).FirstOrDefault(b=>AutomationProperties.GetName(b)=="第 1 项 Agent 行");
            railButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
            var slot=config.Draft.Slots[0];
            var badge=FindLogical<TextBlock>(this).FirstOrDefault(t=>AutomationProperties.GetName(t)==$"同步状态 {slot.DisplayName}");
            check("UX12 VPS slot renders sync badge",badge!=null);
            var attempt=Guid.NewGuid();
            var plan=new LaunchItem(attempt,Guid.NewGuid(),1,"sync-review",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:slot.Id,RemoteExecution:new RemoteExecutionSpec(host.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            ControlEntry Entry(string state)=>new(){Plan=plan,State=state};
            status=new(true,"sync review probe",[Entry("Settled")]);
            Store.Save(Store.SyncFinalPath(attempt),new SyncFinalRecord(1,SyncFinalRecord.Synced,"Mutagen flush 成功，会话已终止。",DateTimeOffset.UtcNow));
            await ApplySyncListToBadgesAsync("No sessions found",true,"",DateTimeOffset.UtcNow);
            check("UX12 A1 terminal synced attempt shows 已同步 badge",badge!.Text=="同步：已同步"&&badge.ToolTip!.ToString()!.Contains("sync-final"));
            Store.Save(Store.SyncFinalPath(attempt),new SyncFinalRecord(1,SyncFinalRecord.Problem,"Mutagen flush 失败，会话已保留。",DateTimeOffset.UtcNow));
            await ApplySyncListToBadgesAsync("No sessions found",true,"",DateTimeOffset.UtcNow);
            check("UX12 A1 terminal problem attempt shows 同步有问题 badge",badge.Text=="同步：同步有问题");
            System.IO.File.Delete(Store.SyncFinalPath(attempt));
            // 徽章文案的确定性断言显式投递 list 结果：真实探针依赖本机 mutagen，CI 未安装时按规格 fail-closed
            // 显示 未知（R39 探针失败检查覆盖该路径），不能把环境差异当产品回归。探针本身仍在此处实跑一次，
            // 只断言与安装状态无关的不变量：无 sync-final 记录时绝不宣称已同步。
            await ProbeSyncHealthAsync(force:true);
            check("UX12 A1 probe without record never claims 已同步",badge.Text!="同步：已同步");
            await ApplySyncListToBadgesAsync("No sessions found",true,"",DateTimeOffset.UtcNow);
            check("UX12 A1 terminal attempt without record shows 未同步",badge.Text=="同步：未同步");
            Store.Save(Store.SyncFinalPath(attempt),new SyncFinalRecord(1,SyncFinalRecord.Synced,"Mutagen flush 成功，会话已终止。",DateTimeOffset.UtcNow));
            status=new(true,"sync review probe",[Entry("Disconnected")]);
            await ApplySyncListToBadgesAsync("No sessions found",true,"",DateTimeOffset.UtcNow);
            check("UX12 A1 Disconnected stays live-evaluated and never claims 已同步",badge.Text=="同步：未同步");
            status=new(true,"sync review probe",[Entry("Settled")]);
            var sessionName="agent-launcher-"+Guid.NewGuid().ToString("N");
            Store.Save(Store.SyncSessionPath(attempt),new SyncSessionRecord(1,sessionName,repo,"/home/agent/work",DateTimeOffset.UtcNow));
            var listOutput=$"Name: {sessionName}\nConfiguration:\n\tSynchronization mode: Two Way Safe\nAlpha:\n\tURL: {repo}\n\tConnected: Yes\nBeta:\n\tURL: ssh://agent@{host.Address}:22/home/agent/work\n\tConnected: Yes\nStatus: Watching for changes\n";
            await ApplySyncListToBadgesAsync(listOutput,true,"",DateTimeOffset.UtcNow);
            check("UX12 B3 persisted session name recovers live health after restart",badge.Text=="同步：已收敛"&&badge.ToolTip!.ToString()!.Contains(sessionName));
            await ApplySyncListToBadgesAsync("No sessions found",true,"",DateTimeOffset.UtcNow);
            check("UX12 B3 terminated session falls back to sync-final",badge.Text=="同步：已同步");
            await ApplySyncListToBadgesAsync(listOutput.Replace("Two Way Safe","Two Way Resolved"),true,"",DateTimeOffset.UtcNow);
            check("R39 terminal fallback never returns unchecked live readiness",badge.Text=="同步：未收敛");
            config.Edit(d=>d.Slots[0].Directory=System.IO.Path.Combine(repo,"different-workspace"));
            await ApplySyncListToBadgesAsync(listOutput,true,"",DateTimeOffset.UtcNow);
            var oldLiveRejected=badge.Text=="同步：未同步";
            await ApplySyncListToBadgesAsync("No sessions found",true,"",DateTimeOffset.UtcNow);
            check("R39 old endpoint receipts never certify a changed workspace",oldLiveRejected&&badge.Text=="同步：未同步");
            config.Edit(d=>d.Slots[0].Directory=repo);
            // B1：文件列表 + 只读 diff 对话框（未跟踪提示、已跟踪 diff、400 行截断）。
            var dialogSeen=false;
            var inspect=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            inspect.Tick+=(_,_)=>{
                if(dialogSeen)return;
                var window=OwnedWindows.OfType<Window>().FirstOrDefault(w=>w.Title=="查看改动 · "+slot.DisplayName);
                if(window is null)return;
                dialogSeen=true;inspect.Stop();
                _=Dispatcher.InvokeAsync(async()=>{
                    try
                    {
                        var list=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="改动文件列表");
                        var diffBox=FindLogical<TextBox>(window).Single(t=>AutomationProperties.GetName(t)=="文件 diff");
                        check("UX12 B1 changes dialog lists files with read-only diff",diffBox.IsReadOnly&&list.Items.OfType<ListBoxItem>().Count()>=2);
                        async Task WaitDiff(Func<string,bool> ready){for(var i=0;i<100&&!ready(diffBox.Text);i++)await Task.Delay(100);}
                        list.SelectedItem=list.Items.OfType<ListBoxItem>().First(i=>(i.Content as string)!.Contains("未跟踪"));
                        await WaitDiff(t=>t.Contains("未跟踪，无 diff"));
                        check("UX12 B1 untracked file shows 未跟踪无 diff",diffBox.Text.Contains("未跟踪，无 diff"));
                        list.SelectedItem=list.Items.OfType<ListBoxItem>().First(i=>(i.Content as string)!.Contains("small.txt"));
                        await WaitDiff(t=>t.Contains("+changed line"));
                        check("UX12 B1 tracked file shows exact read-only diff",diffBox.Text.Contains("+changed line")&&!diffBox.Text.Contains("已截断"));
                        list.SelectedItem=list.Items.OfType<ListBoxItem>().First(i=>(i.Content as string)!.Contains("tracked.txt"));
                        await WaitDiff(t=>t.Contains("已截断"));
                        check("UX12 B1 diff truncates at 400 lines with notice",diffBox.Text.Contains("-line 1")&&diffBox.Text.Split('\n').Length<=401);
                        await Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);
                        CaptureQuickActions(window,System.IO.Path.Combine(folder,"sync-review-diff.png"));
                        window.Close();
                    }
                    catch(Exception e){check("UX12 B1 dialog inspection failed: "+e.Message,false);try{window.Close();}catch{}}
                });
            };
            inspect.Start();
            try{await ShowSlotChangesAsync(slot.Id);}finally{inspect.Stop();}
            check("UX12 B1 changes dialog opened for inspection",dialogSeen);
            check("UX12 B1 review left worktree untouched",await System.IO.File.ReadAllTextAsync(tracked)==string.Join("\n",Enumerable.Range(1,500).Select(i=>"changed "+i))+"\n");
            // ---- 同步可靠性 R34/R39/R40/R41/R36：链路徽章 + 上次观测 + 只读冲突清单 ----
            var jyHost=new RemoteHost(Guid.NewGuid(),"京云夹具","jy.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.Mutagen);
            var alyHost=new RemoteHost(Guid.NewGuid(),"阿里云夹具","aly.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.Mutagen);
            var sshfsHost=new RemoteHost(Guid.NewGuid(),"SSHFS 夹具","sshfs.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.SshfsMount);
            var noneHost=new RemoteHost(Guid.NewGuid(),"None 遗留","none.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.None);
            config=new Configuration();config.RemoteHosts=[jyHost,alyHost,sshfsHost,noneHost];
            config.Edit(d=>{d.Project=repo;
                d.Slots[0].Enabled=true;d.Slots[0].Directory=repo;d.Slots[0].RemoteHostId=jyHost.Id;d.Slots[0].RemoteDirectory="/home/agent/work";
                d.Slots[1].Enabled=true;d.Slots[1].Directory=repo;d.Slots[1].RemoteHostId=alyHost.Id;d.Slots[1].RemoteDirectory="/home/agent/work";
                d.Slots[2].Enabled=false;d.Slots[2].Directory=repo;d.Slots[2].RemoteHostId=sshfsHost.Id;d.Slots[2].RemoteDirectory="/home/agent/work";
                d.Slots[3].Enabled=false;d.Slots[3].Directory=repo;d.Slots[3].RemoteHostId=noneHost.Id;d.Slots[3].RemoteDirectory="/home/agent/work";
                foreach(var s in d.Slots.Skip(4))s.Enabled=false;});
            Render();
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
            TextBlock Badge(int i)
            {
                FindLogical<Button>(RailSlots).Single(b=>AutomationProperties.GetName(b)==$"第 {i+1} 项 Agent 行").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                return FindLogical<TextBlock>(SlotList).Single(t=>AutomationProperties.GetName(t)==$"同步状态 {config.Draft.Slots[i].DisplayName}");
            }
            var renderedBadge=FindLogical<TextBlock>(SlotList).Single(t=>AutomationProperties.GetName(t)==$"同步状态 {config.Draft.Slots[0].DisplayName}");
            check("R34 render retains registration for the visible sync badge",syncBadges.GetValueOrDefault(config.Draft.Slots[0].Id)==renderedBadge);
            string RelSession(string name,string mode,string status,string betaConnected="Yes",string conflicts="",string lastError="")=>
                "-------------------------------------------------------------------------------\n"+
                $"Name: {name}\nIdentifier: sync_fake{name}\nConfiguration:\n\tSynchronization mode: {mode}\n"+
                $"Alpha:\n\tURL: {repo}\n\tConnected: Yes\nBeta:\n\tURL: ssh://agent@jy.example.com:22/home/agent/work\n\tConnected: {betaConnected}\n"+
                conflicts+(lastError.Length>0?$"Last error: {lastError}\n":"")+$"Status: {status}\n"+
                "-------------------------------------------------------------------------------\n";
            var jyAttempt=Guid.NewGuid();var alyAttempt=Guid.NewGuid();
            var jyPlan=new LaunchItem(jyAttempt,Guid.NewGuid(),1,"r34-jy",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[0].Id,RemoteExecution:new RemoteExecutionSpec(jyHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            var alyPlan=new LaunchItem(alyAttempt,Guid.NewGuid(),2,"r34-aly",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[1].Id,RemoteExecution:new RemoteExecutionSpec(alyHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            status=new(true,"sync reliability probe",[new ControlEntry{Plan=jyPlan,State="Started"},new ControlEntry{Plan=alyPlan,State="Started"}]);
            var jyName="agent-launcher-"+Guid.NewGuid().ToString("N");var alyName="agent-launcher-"+Guid.NewGuid().ToString("N");
            Store.Save(Store.SyncSessionPath(jyAttempt),new SyncSessionRecord(1,jyName,repo,"/home/agent/work",DateTimeOffset.UtcNow));
            Store.Save(Store.SyncSessionPath(alyAttempt),new SyncSessionRecord(1,alyName,repo,"/home/agent/work",DateTimeOffset.UtcNow));
            // R34：两条链路都 Watching+two-way-safe → 各自已收敛，详情含上次观测；整体全部已收敛。
            var bothOk=RelSession(jyName,"Two Way Safe","Watching for changes")+RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com");
            await ApplySyncListToBadgesAsync(bothOk,true,"",DateTimeOffset.UtcNow);
            check("R34 both healthy links show 已收敛 with last observation",Badge(0).Text=="同步：已收敛"&&Badge(1).Text=="同步：已收敛"&&Badge(0).ToolTip!.ToString()!.Contains("上次观测"));
            // 同步详情对话框：整体标签 + 逐链路 + 只读冲突清单（无处理按钮）。
            var detailsSeen=false;Exception? detailsFailure=null;
            var detailsInspect=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            detailsInspect.Tick+=(_,_)=>{
                if(detailsSeen)return;
                var window=OwnedWindows.OfType<Window>().FirstOrDefault(w=>w.Title=="同步详情");
                if(window is null)return;
                detailsSeen=true;detailsInspect.Stop();
                _=Dispatcher.InvokeAsync(async()=>{
                    try
                    {
                        var overall=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步整体标签");
                        var links=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步链路列表");
                        var conflicts=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步冲突清单");
                        var remaining=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步冲突剩余计数");
                        check("R34 details dialog shows overall 全部已收敛 and per-link observation",overall.Text=="全部已收敛"&&links.Items.Count>=2&&links.Items.OfType<ListBoxItem>().Any(i=>(i.Content is TextBlock text?text.Text:i.Content.ToString()!).Contains("上次观测")));
                        check("R36 conflict list is read-only with no action buttons",conflicts.IsEnabled&&FindLogical<Button>(window).All(b=>(string)b.Content=="关闭"));
                        check("R36 empty conflict list shows zero remainder",remaining.Text.Contains("0"));
                        check("R34 sync detail lists follow the current theme",Equals(links.Background,FindResource("Surface"))&&Equals(links.Foreground,FindResource("Ink"))&&Equals(conflicts.Background,FindResource("Surface")));
                        await Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);
                        CaptureQuickActions(window,System.IO.Path.Combine(folder,"sync-details-converged.png"));
                        syncLinkObservedAt[config.Draft.Slots[0].Id]=DateTimeOffset.UtcNow.AddSeconds(-31);
                        for(var i=0;i<65&&overall.Text!="未知";i++)await Task.Delay(100);
                        check("R39 open sync details expire without a new network probe",overall.Text=="未知");
                        syncLinkObservedAt[config.Draft.Slots[0].Id]=DateTimeOffset.UtcNow;
                        var close=FindLogical<Button>(window).Single(b=>(string)b.Content=="关闭");
                        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch(Exception e){detailsFailure=e;window.Close();}
                });
            };
            detailsInspect.Start();
            try{ShowSyncDetails(config.Draft.Slots[0].Id);}finally{detailsInspect.Stop();}
            if(detailsFailure!=null)throw detailsFailure;
            check("R34 details dialog opened for inspection",detailsSeen);
            check("R34 details dialog closes via DialogResult button",!OwnedWindows.OfType<Window>().Any(w=>w.Title=="同步详情"));
            void CheckDetails(string expected,int expectedLinks,string? expectedConflict=null)
            {
                Exception? failure=null;var seen=false;
                var inspectDetails=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
                inspectDetails.Tick+=(_,_)=>{
                    var window=OwnedWindows.OfType<Window>().FirstOrDefault(w=>w.Title=="同步详情");
                    if(window==null)return;
                    seen=true;inspectDetails.Stop();
                    try
                    {
                        var overall=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步整体标签");
                        var links=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步链路列表");
                        check($"R34 scoped details show {expected} with {expectedLinks} links",overall.Text==expected&&links.Items.Count==expectedLinks);
                        if(expectedConflict!=null)
                        {
                            var conflicts=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步冲突清单");
                            var remaining=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步冲突剩余计数");
                            check("R36 shared endpoint retains one conflict row per session",conflicts.Items.Count==1&&((string)((ListBoxItem)conflicts.Items[0]).Content).Contains(expectedConflict)&&remaining.Text.StartsWith("冲突清单：显示 1 行，剩余 0 条",StringComparison.Ordinal));
                        }
                    }
                    catch(Exception e){failure=e;}
                    finally{window.Close();}
                };
                inspectDetails.Start();
                try{ShowSyncDetails(config.Draft.Slots[0].Id);}finally{inspectDetails.Stop();}
                if(failure!=null)throw failure;
                check("R34 scoped details opened",seen);
            }
            var savedStatus=status;
            config.RemoteHosts=[jyHost,alyHost with{Address=jyHost.Address},sshfsHost,noneHost];
            status=new(true,"duplicate endpoint fixture",[]);
            await ApplySyncListToBadgesAsync(RelSession(jyName,"Two Way Safe","Watching for changes"),true,"",DateTimeOffset.UtcNow);
            CheckDetails("单链路已收敛",1);
            config.RemoteHosts=[jyHost,alyHost,sshfsHost,noneHost];
            config.Edit(d=>d.Slots[1].RemoteHostId=jyHost.Id);
            status=new(true,"shared endpoint sessions",[new ControlEntry{Plan=jyPlan,State="Started"},new ControlEntry{Plan=alyPlan with{RemoteExecution=jyPlan.RemoteExecution},State="Started"}]);
            var sharedConflict="Conflicts:\n\t(alpha) shared.txt (<non-existent> -> File (aa))\n\t(beta) shared.txt (<non-existent> -> File (bb))\n";
            await ApplySyncListToBadgesAsync(RelSession(jyName,"Two Way Safe","Watching for changes")+RelSession(alyName,"Two Way Safe","Watching for changes",conflicts:sharedConflict),true,"",DateTimeOffset.UtcNow);
            check("R36 second session on a shared endpoint reports conflict",Badge(1).Text=="同步：冲突");
            CheckDetails("冲突",1,"shared.txt");
            var sharedStatus=status;status=new(true,"shared session aliases",[]);
            await ApplySyncListToBadgesAsync(RelSession(jyName,"Two Way Safe","Watching for changes",conflicts:sharedConflict),true,"",DateTimeOffset.UtcNow);
            CheckDetails("冲突",1,"shared.txt");
            status=sharedStatus;
            await ApplySyncListToBadgesAsync(RelSession(jyName,"Two Way Safe","Watching for changes")+RelSession(alyName,"Two Way Safe","Watching for changes"),true,"",DateTimeOffset.UtcNow);
            CheckDetails("单链路已收敛",1);
            syncLinkObservedAt[config.Draft.Slots[1].Id]=DateTimeOffset.UtcNow.AddSeconds(-31);
            CheckDetails("未知",1);
            await ApplySyncListToBadgesAsync(RelSession(jyName,"Two Way Safe","Watching for changes")+RelSession(alyName,"Two Way Safe","Halted due to root deletion"),true,"",DateTimeOffset.UtcNow);
            CheckDetails("安全暂停",1);
            config.Edit(d=>d.Slots[1].RemoteHostId=alyHost.Id);status=savedStatus;
            await ApplySyncListToBadgesAsync(bothOk,true,"",DateTimeOffset.UtcNow);
            config.Edit(d=>d.Slots[1].Directory=System.IO.Path.Combine(repo,"other-project"));
            Render();CheckDetails("单链路已收敛",1);
            check("R39 changed workspace never reuses an old healthy badge",Badge(1).Text!="同步：已收敛");
            config.Edit(d=>{d.Slots[1].Directory=repo;d.Slots[1].RemoteHostId=jyHost.Id;});
            Render();CheckDetails("未知",1);
            await ApplySyncListToBadgesAsync(bothOk,true,"",DateTimeOffset.UtcNow);
            CheckDetails("单链路已收敛",1);
            config.Edit(d=>d.Slots[1].RemoteHostId=alyHost.Id);Render();
            await ApplySyncListToBadgesAsync(bothOk.Replace("ssh://agent@jy.example.com:22/","ssh://other@jy.example.com:2222/"),true,"",DateTimeOffset.UtcNow);
            check("R34 mismatched SSH user and port cannot claim convergence",Badge(0).Text=="同步：未知");
            await ApplySyncListToBadgesAsync(bothOk,true,"",DateTimeOffset.UtcNow);
            syncLinkObservedAt[config.Draft.Slots[0].Id]=DateTimeOffset.UtcNow.AddSeconds(-31);
            check("R39 selecting a stale cached link immediately shows 未知",Badge(0).Text=="同步：未知");
            // R39：观测超过 30 秒 → 未知并保留上次观测；探针失败 → 未知（非终态条目不回退 sync-final）。
            await ApplySyncListToBadgesAsync(bothOk,true,"",DateTimeOffset.UtcNow.AddSeconds(-31));
            check("R39 stale observation shows 未知 with last observation",Badge(0).Text=="同步：未知"&&Badge(0).ToolTip!.ToString()!.Contains("上次观测"));
            await ApplySyncListToBadgesAsync(null,false,"mutagen sync list 超时（10 秒），已终止。",DateTimeOffset.UtcNow);
            check("R39 probe failure shows 未知 not 未同步",Badge(0).Text=="同步：未知"&&Badge(1).Text=="同步：未知");
            // R39：命名会话在输出里但无法匹配/解析 → fail-closed 未知，不回退成未同步。
            await ApplySyncListToBadgesAsync($"Name: {jyName}\ngarbage\nName: {alyName}\ngarbage\n",true,"",DateTimeOffset.UtcNow);
            check("R39 unparsable named sessions show 未知",Badge(0).Text=="同步：未知");
            await ApplySyncListToBadgesAsync("",true,"",DateTimeOffset.UtcNow);
            check("R39 empty successful output is still unknown",Badge(0).Text=="同步：未知");
            await ApplySyncListToBadgesAsync("unexpected output",true,"",DateTimeOffset.UtcNow);
            check("R39 unrecognized successful output is still unknown",Badge(0).Text=="同步：未知");
            // R40：安全暂停徽章与原因文案。
            var halted=RelSession(jyName,"Two Way Safe","Halted due to root deletion")+RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com");
            await ApplySyncListToBadgesAsync(halted,true,"",DateTimeOffset.UtcNow);
            check("R40 root deletion shows 安全暂停 badge",Badge(0).Text=="同步：安全暂停"&&Badge(0).ToolTip!.ToString()!.Contains("根被删除"));
            // R36：冲突徽章 + 详情清单 50 行截断与剩余计数（60 条冲突 → 50 行 + 剩余 10）。
            var manyConflicts="Conflicts:\n"+string.Concat(Enumerable.Range(1,60).Select(i=>$"\t(alpha) f{i:D3}.txt (<non-existent> -> File (aa))\n\t(beta)  f{i:D3}.txt (<non-existent> -> File (bb))\n"));
            var conflicted=RelSession(jyName,"Two Way Safe","Watching for changes",conflicts:manyConflicts)+RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com");
            await ApplySyncListToBadgesAsync(conflicted,true,"",DateTimeOffset.UtcNow);
            check("R36 conflict shows 冲突 badge",Badge(0).Text=="同步：冲突");
            var conflictSeen=false;
            var conflictInspect=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            conflictInspect.Tick+=(_,_)=>{
                if(conflictSeen)return;
                var window=OwnedWindows.OfType<Window>().FirstOrDefault(w=>w.Title=="同步详情");
                if(window is null)return;
                conflictSeen=true;conflictInspect.Stop();
                _=Dispatcher.InvokeAsync(async()=>{
                    try
                    {
                        var overall=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步整体标签");
                        var conflicts=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步冲突清单");
                        var remaining=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步冲突剩余计数");
                        check("R36 conflict list caps at 50 rows with remaining 10 and overall 冲突",overall.Text=="冲突"&&conflicts.Items.Count==50&&remaining.Text.Contains("10"));
                        check("R36 conflict rows carry relative path and link label",conflicts.Items.OfType<ListBoxItem>().All(i=>((string)i.Content).Contains("本机—京云")));
                        await Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);
                        CaptureQuickActions(window,System.IO.Path.Combine(folder,"sync-details-conflicts.png"));
                        var close=FindLogical<Button>(window).Single(b=>(string)b.Content=="关闭");
                        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch(Exception e){check("R36 conflict dialog inspection failed: "+e.Message,false);try{window.Close();}catch{}}
                });
            };
            conflictInspect.Start();
            try{ShowSyncDetails(config.Draft.Slots[0].Id);}finally{conflictInspect.Stop();}
            check("R36 conflict details dialog opened for inspection",conflictSeen);
            // R41：启用 SSHFS/None 槽位后按最近尝试状态显示挂载提示与拒收文案（都不是已收敛）。
            var sshfsAttempt=Guid.NewGuid();var noneAttempt=Guid.NewGuid();
            var sshfsPlan=new LaunchItem(sshfsAttempt,Guid.NewGuid(),3,"r41-sshfs",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[2].Id,RemoteExecution:new RemoteExecutionSpec(sshfsHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null,new(WorkspaceSyncMode.SshfsMount,repo,"/home/agent/work",null,null)));
            var nonePlan=new LaunchItem(noneAttempt,Guid.NewGuid(),4,"r41-none",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[3].Id,RemoteExecution:new RemoteExecutionSpec(noneHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            status=new(true,"sync reliability probe",[new ControlEntry{Plan=jyPlan,State="Started"},new ControlEntry{Plan=alyPlan,State="Started"},new ControlEntry{Plan=sshfsPlan,State="Started"},new ControlEntry{Plan=nonePlan,State="FailedBeforeStart"}]);
            config.Edit(d=>{d.Slots[2].Enabled=true;d.Slots[3].Enabled=true;});
            Render();
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
            check("R41 SSHFS alive shows 已挂载 not convergence",Badge(2).Text=="同步：已挂载"&&Badge(2).ToolTip!.ToString()!.Contains("不是已收敛"));
            check("R41 None shows rejection copy",Badge(3).Text=="同步：未同步"&&Badge(3).ToolTip!.ToString()!.Contains("拒收"));
            config.Edit(d=>d.Slots[0].RemoteHostId=sshfsHost.Id);
            check("R41 old Mutagen attempt cannot certify a new SSHFS mount",Badge(0).Text!="同步：已挂载");
            config.Edit(d=>d.Slots[0].RemoteHostId=jyHost.Id);
            // A1 回归：会话已终止（list 无该会话）时终态条目仍回退 sync-final，新状态机不吞掉旧回退链。
            Store.Save(Store.SyncFinalPath(jyAttempt),new SyncFinalRecord(1,SyncFinalRecord.Synced,"Mutagen flush 成功，会话已终止。",DateTimeOffset.UtcNow));
            status=new(true,"sync reliability probe",[new ControlEntry{Plan=jyPlan,State="Settled"},new ControlEntry{Plan=alyPlan,State="Started"},new ControlEntry{Plan=sshfsPlan,State="Started"},new ControlEntry{Plan=nonePlan,State="FailedBeforeStart"}]);
            await ApplySyncListToBadgesAsync(RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com"),true,"",DateTimeOffset.UtcNow);
            check("A1 terminal fallback preserved under link badges",Badge(0).Text=="同步：已同步"&&Badge(1).Text=="同步：已收敛");
        }
        finally
        {
            config=originalConfig;status=originalStatus;Render();RenderMonitorStatus();
            if(polling)timer.Start();
            if(memoryPolling)memoryTimer.Start();
        }
    }
}
#endif
