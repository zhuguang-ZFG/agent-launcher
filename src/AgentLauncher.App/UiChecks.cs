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
        System.IO.File.WriteAllText(configPath,"{\"SchemaVersion\":1,\"Draft\":{\"SchemaVersion\":1,\"DirectoryMode\":\"isolated\",\"Layout\":\"vertical\",\"Slots\":[{\"AgentId\":\"pi\",\"Profiles\":{}}]},\"Templates\":{\"broken\":null},\"TerminalPath\":\"\",\"RecentProjects\":[]}");
        await LoadAsync();
        Check("null template value enters fail-closed read-only",readOnly&&RestoreBackupButton.Visibility==Visibility.Visible&&!LaunchButton.IsEnabled);
        if(healthyConfig!=null)System.IO.File.WriteAllBytes(configPath,healthyConfig);else System.IO.File.Delete(configPath);
        readOnly=false;// white-box: exit the fail-closed state that production only clears via 恢复备份
        await LoadAsync();ErrorText.Text="";
        Check("healthy configuration reloads writable",!readOnly);
        var primaryBefore=System.IO.File.Exists(configPath)?System.IO.File.ReadAllBytes(configPath):null;
        var badBackup=new Configuration();badBackup.LastEdited=badBackup.Draft.Copy();badBackup.LastEdited.SchemaVersion=2;
        Store.Save(configPath+".bak",badBackup);
        RestoreBackupClick(this,new RoutedEventArgs());
        for(var i=0;i<50&&ErrorText.Text.Length==0;i++)await Task.Delay(100);
        Check("restore rejects backup startup would reject",ErrorText.Text.Contains("结构不支持"));
        var primaryAfter=System.IO.File.Exists(configPath)?System.IO.File.ReadAllBytes(configPath):null;
        Check("rejected restore leaves primary untouched",(primaryBefore==null)==(primaryAfter==null)&&(primaryBefore==null||primaryAfter!.SequenceEqual(primaryBefore)));
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
        Check("automation toggle reverts cleanly",!config.Draft.Slots[0].Enabled&&SelectionSummary.Text=="已选 0 个，共 4 个");
        var fourth=Find<CheckBox>(SlotList).Last();fourth.IsChecked=true;fourth.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Check("native checkbox updates Draft",config.Draft.Slots.Count(s=>s.Enabled)==1&&config.Draft.Slots[3].Enabled);
        Check("preview keeps original slot numbers",Find<TextBlock>(Preview).First().Text.StartsWith("4"));
        config.SaveTemplate("fourth");config.Edit(d=>d.DirectoryMode="shared");config.Restore(config.Templates["fourth"]);Render();
        Check("native mode restore renders isolated",DirectoryMode.SelectedIndex==0&&config.Draft.DirectoryMode=="isolated");
        var firstId=config.Draft.Slots[0].Id;config.Edit(d=>d.Slots[0].Profiles["pi"]=new(){Executable="C:\\Example\\pi.exe",Arguments=["--only-pi"]});Render();
        var select=Find<ComboBox>(SlotList).First();select.SelectedValue="codex";Check("native Agent selector separates profile",config.Draft.Slots.Single(s=>s.Id==firstId).Current.Executable=="");
        select=Find<ComboBox>(SlotList).First();select.SelectedValue="pi";Check("native Agent selector restores profile",config.Draft.Slots.Single(s=>s.Id==firstId).Current.Arguments.SequenceEqual(new[]{"--only-pi"}));
        config.Edit(d=>{d.Slots[0].Profiles.Clear();d.Slots.ForEach(s=>s.Enabled=true);});Render();
        Check("four pane preview positions",Preview.Children.Count==4&&Grid.GetColumn(Preview.Children[2])==0&&Grid.GetRow(Preview.Children[2])==1);
        config.Edit(d=>d.Slots[3].Enabled=false);Render();Check("three pane left spans rows",Grid.GetRowSpan(Preview.Children[0])==2);
        Check("slot rows place fixed columns",Grid.GetColumn(Find<CheckBox>(SlotList).First())==0&&Grid.GetColumn(Find<ComboBox>(SlotList).First())==2&&Grid.GetColumn(Find<Button>(SlotList).First(b=>AutomationProperties.GetName(b)=="第 1 项详情"))==4);
        Check("overflow menu keeps move and remove",Find<MenuButton>(SlotList).First().MenuLabels.SequenceEqual(new[]{"上移","下移","移除"}));
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
        AddClick(this,new RoutedEventArgs());
        var undo2=Find<Button>(Sessions).FirstOrDefault(b=>(b.Content as string)=="撤销移除");Check("undo offered after second remove",undo2!=null);
        undo2!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("undo refuses when full",config.Draft.Slots.Count==4&&config.Draft.Slots.All(s=>s.Id!=secondRemoved)&&ProgressText.Text.Contains("无法撤销"));
        // F7 regression: a refused undo must stay retryable; freeing capacity then clicking must restore the slot.
        Check("undo stays enabled after capacity refusal",undo2.IsEnabled);
        var thirdMenu=Find<MenuButton>(SlotList).First();((Button)thirdMenu.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((MenuItem)thirdMenu.menu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));thirdMenu.menu.IsOpen=false;
        undo2.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("undo retry restores after freeing capacity",config.Draft.Slots.Count==4&&config.Draft.Slots.Any(s=>s.Id==secondRemoved));
        var settingsDialog=CreateSettingsDialog();
        var settingCombos=FindLogical<ComboBox>(settingsDialog).ToArray();var settingChecks=FindLogical<CheckBox>(settingsDialog).ToArray();
        Check("settings use typed controls",settingCombos.Length==1&&settingCombos[0].Items.Count==3&&(settingCombos[0].SelectedItem as ComboBoxItem)?.Tag as string==config.Theme&&settingChecks.Length==1&&FindLogical<TextBox>(settingsDialog).Count()==1);
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
        System.IO.Directory.CreateDirectory(Store.Root);
        var realMonitor=Store.Read<int>(Store.FilePath("monitor-pid.json"));if(realMonitor>0){try{using var m=Process.GetProcessById(realMonitor);m.Kill();m.WaitForExit(5000);}catch(ArgumentException){}}
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE","1");
        await RefreshAsync();Check("fake-sample monitor healthy",status is{Ok:true});
        var gateItem=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"ui-gate",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new(Commands.FindExe("node.exe")!,["-e","setTimeout(()=>{},1500)"],"exe"),ResourcePolicy.AgentBudget);
        var gateReserve=await Ipc.ReserveWithCleanupAsync([gateItem]);Check("gate reserve admitted ("+gateReserve.Message+")",gateReserve.Ok);
        submittedAttempts=[gateItem.Id];submittedRevision=config.Draft.Revision;
        await RefreshAsync();
        Check("pending attempt keeps launch gate",submittedAttempts.Length==1&&submittedRevision==config.Draft.Revision);
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
        static IEnumerable<object> LogicalTree(DependencyObject root){yield return root;foreach(var child in LogicalTreeHelper.GetChildren(root).Cast<object>())if(child is DependencyObject d)foreach(var nested in LogicalTree(d))yield return nested;}
        var suggestDialog=new FormWindow(this,"suggestions",[("exe","程序路径","",false)],"n",suggestions:new(){["exe"]=["C:\\Tools\\pi.cmd"]});
        Check("discovered installs render editable dropdown",LogicalTree(suggestDialog).OfType<ComboBox>().FirstOrDefault() is {IsEditable:true} discovered&&discovered.Items.Count==1);
        var plainDialog=new FormWindow(this,"plain",[("exe","程序路径","",false)],"n");
        Check("without discovery stays plain text box",!LogicalTree(plainDialog).OfType<ComboBox>().Any()&&LogicalTree(plainDialog).OfType<TextBox>().Any());
        var pids=status?.Entries?.Where(e=>!Store.IsTerminal(e)).Count()??0;Check("UI checks do not launch agents",pids==0);
        Store.Save(System.IO.Path.Combine(folder,"ui-checks.json"),new{passed=checks,Dpi=VisualTreeHelper.GetDpi(this).PixelsPerInchX,Resource=ResourceText.Text});Close();
    }
}
#endif
