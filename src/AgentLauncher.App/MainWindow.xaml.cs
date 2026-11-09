using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;
public partial class MainWindow : Window
{
    private Configuration config=new();
    private bool rendering,busy,loading=true,readOnly,refreshing,closing;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(2)};
    private byte[]? pendingSave;
    private Task saveTask=Task.CompletedTask;
    private string? saveError;
    private Response? status;
    private CancellationTokenSource? launchCancellation;
    private Guid[] submittedAttempts=[];
    private long submittedRevision=-1;
    private readonly string[] arguments;
    private string sessionSignature="";
    private readonly Dictionary<Guid,TextBlock> commandLabels=[];
    private readonly string configPath=Store.FilePath("configuration.json");
    private bool commandsChecking,commandsRunning;
    private Draft? pendingCommandCheck;
    private string validationError="";
    public MainWindow(string[] args)
    {
        arguments=args;InitializeComponent();
        var work=SystemParameters.WorkArea;Width=Math.Min(1120,work.Width-32);Height=Math.Min(800,work.Height-32);MinWidth=Math.Min(560,work.Width-32);MinHeight=Math.Min(460,work.Height-32);
        SizeChanged+=(_,_)=>ArrangePanels();
        Loaded+=async (_,_)=>await GuardAsync(LoadAsync);
        timer.Tick+=async (_,_)=>await GuardAsync(RefreshAsync);
        PreviewKeyDown+=(_,e)=>{if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.O){ChooseClick(this,new());e.Handled=true;}else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.Enter&&LaunchButton.IsEnabled){LaunchClick(this,new());e.Handled=true;}};
        Closing+=ClosingWindow;
    }
    private async Task GuardAsync(Func<Task> action)
    {
        try{await action();}catch(OperationCanceledException){ErrorText.Text="已停止后续步骤。已创建的目录和已运行任务保留。";}catch(Exception e)when(e is not OutOfMemoryException){ErrorText.Text=e.Message;}
    }
    private async Task LoadAsync()
    {
        var configMissing=!File.Exists(configPath)&&File.Exists(configPath+".bak");
        try
        {
            config=await Task.Run(()=>Store.Read<Configuration>(configPath))??new();
            if(config.SchemaVersion!=1||config.Draft==null||config.Templates==null||config.RecentProjects==null||config.TerminalPath==null)throw new InvalidDataException("配置版本或结构不支持，禁止覆盖。");config.Draft.Validate();foreach(var template in config.Templates.Values)template.Validate();config.LastEdited?.Validate();
        }
        catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {readOnly=true;config=new Configuration();ErrorText.Text="配置无法读取，已禁止覆盖和启动。请在设置中检查配置文件及 .bak 备份。\n"+e.Message;}
        if(configMissing){readOnly=true;ErrorText.Text="配置文件丢失，检测到 .bak 备份。请核对后使用「恢复备份」；当前不会写入配置。";}
        loading=false;RestoreBackupButton.Visibility=readOnly?Visibility.Visible:Visibility.Collapsed;ApplyTheme();Render();await RefreshAsync();timer.Start();
#if INTEGRATION_TESTS
        if(arguments is ["--idle-check",var reportPath])
        {
            using var process=Process.GetCurrentProcess();var samples=new List<object>();var started=DateTime.UtcNow;var cpu=process.TotalProcessorTime;
            for(var i=0;i<30;i++){await Task.Delay(2000);process.Refresh();samples.Add(new{At=DateTimeOffset.UtcNow,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64});}
            var percent=(process.TotalProcessorTime-cpu).TotalSeconds/(DateTime.UtcNow-started).TotalSeconds/Environment.ProcessorCount*100;
            Store.Save(reportPath,new{Samples=samples,CpuPercent=percent,LogicalProcessors=Environment.ProcessorCount});Close();return;
        }
        if(arguments is ["--ui-check",var folder]){await RunUiChecksAsync(folder);return;}
        if(arguments is ["--screenshot",var path])
        {
            await Task.Delay(500);UpdateLayout();var dpi=VisualTreeHelper.GetDpi(this);var bitmap=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(this);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
            using(var process=Process.GetCurrentProcess()){process.Refresh();Store.Save(path+".json",new{Width=ActualWidth,Height=ActualHeight,Dpi=dpi.PixelsPerInchX,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,LaunchEnabled=LaunchButton.IsEnabled,Resource=ResourceText.Text,Error=ErrorText.Text});}
            Close();
        }
#endif
    }
    private void Edit(Action<Draft> change)
    {
        if(rendering||loading||busy||readOnly)return;
        var focusName=Keyboard.FocusedElement is DependencyObject focused&&Within(focused,SlotList)?AutomationProperties.GetName(focused):null;
        config.Edit(change);validationError="";ErrorText.Text="";QueueSave();Render();
        if(focusName is {Length:>0}&&FindLogical(SlotList).FirstOrDefault(el=>AutomationProperties.GetName(el)==focusName) is IInputElement target)Keyboard.Focus(target);
    }
    private static bool Within(DependencyObject? node,DependencyObject root){while(node!=null){if(node==root)return true;node=LogicalTreeHelper.GetParent(node)??(node is Visual v?VisualTreeHelper.GetParent(v):null);}return false;}
    private static IEnumerable<DependencyObject> FindLogical(DependencyObject root){foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){yield return child;foreach(var nested in FindLogical(child))yield return nested;}}
    private void QueueSave()
    {
        if(readOnly)return;SaveStatus.Text="正在保存…";
        pendingSave=JsonSerializer.SerializeToUtf8Bytes(config,Store.Json);
        if(saveTask.IsCompleted)saveTask=SaveAsync();
    }
    private async Task SaveAsync()
    {
        await Task.Delay(100);
        while(pendingSave is {} bytes)
        {
            pendingSave=null;
            try
            {
                await Task.Run(()=>{var snapshot=JsonSerializer.Deserialize<Configuration>(bytes,Store.Json)!;Store.Save(configPath,snapshot);});
                saveError=null;SaveStatus.Text=pendingSave==null?"配置已保存":"正在保存…";
            }
            catch(Exception e)when(e is not OutOfMemoryException){saveError=e.Message;SaveStatus.Text="尚未保存 · "+e.Message;}
        }
    }
    private void Render()
    {
        commandsChecking=true;
        rendering=true;
        try
        {
            ProjectPath.Text=config.Draft.Project;ProjectTitle.Text=string.IsNullOrWhiteSpace(config.Draft.Project)?"从一个项目开始":Path.GetFileName(config.Draft.Project.TrimEnd('\\','/')) is {Length:>0} title?title:config.Draft.Project;
            Recent.ItemsSource=config.RecentProjects;Recent.SelectedIndex=-1;Recent.Visibility=config.RecentProjects.Count>0?Visibility.Visible:Visibility.Collapsed;
            DirectoryMode.SelectedIndex=config.Draft.DirectoryMode switch{"prepared"=>1,"shared"=>2,_=>0};Layout.SelectedIndex=config.Draft.Layout=="horizontal"?1:0;
            DirectoryHelp.Text=config.Draft.DirectoryMode switch{"shared"=>"修改彼此可见，可能相互覆盖。已有独立会话仍受占用保护。","prepared"=>"在各项详情中选择不同的工作目录。",_=>"同项目多开时分别创建目录与分支。完成后需人工合并。"};
            Templates.Items.Clear();Templates.Items.Add(new ComboBoxItem{Content="选择组合",Tag="none",IsEnabled=false});
            Templates.Items.Add(new ComboBoxItem{Content="上次组合",Tag="last",IsEnabled=config.LastEdited!=null,ToolTip="最近一次切换组合前编辑的配置"});
            foreach(var name in config.Templates.Keys)Templates.Items.Add(new ComboBoxItem{Content=name,Tag=name});Templates.SelectedIndex=0;
            SlotList.Children.Clear();commandLabels.Clear();
            foreach(var slot in config.Draft.Slots)AddSlot(slot);
            AddButton.IsEnabled=config.Draft.Slots.Count<4;
            Preview.Children.Clear();Preview.RowDefinitions.Clear();Preview.ColumnDefinitions.Clear();var active=config.Draft.Slots.Where(s=>s.Enabled).ToArray();
            var columns=active.Length<=1||active.Length==2&&config.Draft.Layout=="horizontal"?1:2;var rows=active.Length>2||active.Length==2&&columns==1?2:1;
            for(var i=0;i<columns;i++)Preview.ColumnDefinitions.Add(new());for(var i=0;i<rows;i++)Preview.RowDefinitions.Add(new());
            for(var i=0;i<active.Length;i++)
            {
                var slot=active[i];var pane=CreatePreviewPane(slot);
                Grid.SetColumn(pane,columns==1?0:active.Length==3&&i>0?1:i%2);Grid.SetRow(pane,active.Length==3?Math.Max(0,i-1):active.Length==4?i/2:columns==1?i:0);if(active.Length==3&&i==0)Grid.SetRowSpan(pane,2);Preview.Children.Add(pane);
            }
            if(active.Length==0)Preview.Children.Add(new TextBlock{Text="勾选 Agent 后预览布局",Foreground=new SolidColorBrush(Color.FromRgb(165,171,182)),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center});Layout.IsEnabled=active.Length==2;
            SelectionSummary.Text=$"{active.Length} / {config.Draft.Slots.Count} 项启用";PaneCount.Text=$"{active.Length} 个窗格";ArrangePanels();
            UpdateLaunch();
        }
        finally{rendering=false;}
        _=GuardAsync(CheckCommandsAsync);
    }
    private Border CreatePreviewPane(Slot slot)
    {
        var position=config.Draft.Slots.IndexOf(slot)+1;
        var shell=new Border{Background=(Brush)new BrushConverter().ConvertFromString("#0F1116")!,BorderBrush=(Brush)new BrushConverter().ConvertFromString("#2B303A")!,BorderThickness=new(1),CornerRadius=new(6),Padding=new(10),Margin=new(3)};
        var text=new TextBlock{Text=$"{position}  {Agents.Get(slot.AgentId).Name}",Foreground=new SolidColorBrush(Color.FromRgb(215,219,226)),FontSize=13,FontWeight=FontWeights.SemiBold};
        var detail=new TextBlock{Text="将在 Windows Terminal 中打开",Foreground=new SolidColorBrush(Color.FromRgb(126,133,145)),FontSize=11,Margin=new(0,4,0,0)};
        shell.Child=new StackPanel{Children={text,detail}};return shell;
    }
    private void ArrangePanels()
    {
        var wide=ActualWidth>=900;
        Grid.SetColumn(ConfigurationPanel,0);Grid.SetRow(ConfigurationPanel,0);Grid.SetColumn(SummaryPanel,wide?1:0);Grid.SetRow(SummaryPanel,wide?0:1);
        SummaryPanel.Margin=wide?new(0):new(0,20,0,0);ConfigColumn.Width=new GridLength(1,GridUnitType.Star);SummaryColumn.Width=wide?new GridLength(320):new GridLength(0);
        ConfigurationPanel.Margin=wide?new(0,0,20,0):new(0);
    }
    private void AddSlot(Slot slot)
    {
        var position=config.Draft.Slots.IndexOf(slot)+1;
        var row=new Grid{Margin=new(0),MinHeight=48};
        row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var enabled=new CheckBox{IsChecked=slot.Enabled,VerticalAlignment=VerticalAlignment.Center,Margin=new(16,0,8,0),MinWidth=22};
        AutomationProperties.SetName(enabled,$"第 {position} 项本次启动");
        enabled.Click+=(_,_)=>Edit(d=>d.Slots.Single(s=>s.Id==slot.Id).Enabled=enabled.IsChecked==true);row.Children.Add(enabled);
        var number=new TextBlock{Text=$"{position:00}",Foreground=(Brush)FindResource("Muted"),FontFamily=new FontFamily("Consolas"),VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,12,0)};Grid.SetColumn(number,1);row.Children.Add(number);
        var choice=new ComboBox{ItemsSource=Agents.All,DisplayMemberPath="Name",SelectedValuePath="Id",SelectedValue=slot.AgentId,VerticalAlignment=VerticalAlignment.Center,MinWidth=150,MaxWidth=220,MinHeight=32,BorderBrush=(Brush)FindResource("Line"),BorderThickness=new(1)};AutomationProperties.SetName(choice,$"第 {position} 项 Agent");
        choice.SelectionChanged+=(_,_)=>{if(choice.SelectedValue is string id)Edit(d=>d.Slots.Single(s=>s.Id==slot.Id).AgentId=id);};Grid.SetColumn(choice,2);row.Children.Add(choice);
        var command=new TextBlock{Text=slot.Enabled?"正在检查…":"未启用",Foreground=(Brush)FindResource("Muted"),FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new(12,0,4,0),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};commandLabels[slot.Id]=command;Grid.SetColumn(command,3);row.Children.Add(command);
        var detail=new Button{Content="详情",Style=(Style)FindResource("QuietButton"),MinHeight=28,Padding=new(8,2,8,2),VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(detail,$"第 {position} 项详情");
        detail.Click+=(_,_)=>EditSlot(slot.Id);Grid.SetColumn(detail,4);row.Children.Add(detail);
        var actions=new MenuButton(slot,position);Grid.SetColumn(actions,5);row.Children.Add(actions);
        SlotList.Children.Add(new Border{BorderBrush=(Brush)FindResource("Line"),BorderThickness=new(0,0,0,position<config.Draft.Slots.Count?1:0),Child=row});
    }
    private sealed class MenuButton : ContentControl
    {
        internal readonly ContextMenu menu=new();
        internal string[] MenuLabels=>menu.Items.OfType<MenuItem>().Select(i=>(string)i.Header).ToArray();
        public MenuButton(Slot slot,int position)
        {
            var button=new Button{Content="⋯",Style=(Style)Application.Current.Resources["QuietButton"],Padding=new Thickness(10,4,10,4),MinWidth=40,MinHeight=28};
            AutomationProperties.SetName(button,$"第 {position} 项更多操作");
            var items=new List<(MenuItem Item,Func<bool> Enabled)>();
            Menu("上移",()=>Owner.Edit(d=>{var i=d.Slots.FindIndex(s=>s.Id==slot.Id);if(i>0)(d.Slots[i-1],d.Slots[i])=(d.Slots[i],d.Slots[i-1]);}),()=>position>1);Menu("下移",()=>Owner.Edit(d=>{var i=d.Slots.FindIndex(s=>s.Id==slot.Id);if(i>=0&&i<d.Slots.Count-1)(d.Slots[i+1],d.Slots[i])=(d.Slots[i],d.Slots[i+1]);}),()=>position<Owner.config.Draft.Slots.Count);Menu("移除",()=>{var snapshot=Owner.config.Draft.Copy();Owner.Edit(d=>d.Slots.RemoveAll(s=>s.Id==slot.Id));Owner.ProgressText.Text="已移除配置，不影响现有会话。";var undo=new Button{Content="撤销移除",Style=(Style)Application.Current.Resources["QuietButton"]};undo.Click+=(_,_)=>{Owner.config.Restore(snapshot);Owner.QueueSave();Owner.Render();};Owner.Sessions.Children.Insert(0,undo);},()=>Owner.config.Draft.Slots.Count>1);
            button.Click+=(_,_)=>{foreach(var (item,enabled) in items)item.IsEnabled=enabled();menu.PlacementTarget=button;menu.IsOpen=true;};
            Content=button;
            void Menu(string text,Action action,Func<bool> enabled){var item=new MenuItem{Header=text};item.Click+=(_,_)=>action();menu.Items.Add(item);items.Add((item,enabled));}
        }
        private MainWindow Owner=>Window.GetWindow(this) as MainWindow ?? throw new InvalidOperationException();
    }
    private async Task CheckCommandsAsync()
    {
        pendingCommandCheck=config.Draft.Copy();if(commandsRunning)return;commandsRunning=true;
        try
        {
        while(pendingCommandCheck is {} draft)
        {
        pendingCommandCheck=null;var errors=new List<string>();
        foreach(var slot in draft.Slots.Where(s=>s.Enabled))
        {
            string detail;
            try{var cmd=await Task.Run(()=>Commands.Resolve(slot.AgentId,slot.Current));detail=$"{Path.GetFileName(cmd.Executable)} 可用";}
            catch(Exception e)when(e is not OutOfMemoryException){detail=e.Message;errors.Add(detail);}
            if(draft.Revision!=config.Draft.Revision)break;if(commandLabels.TryGetValue(slot.Id,out var text))text.Text=detail;
        }
        if(draft.Revision==config.Draft.Revision){commandsChecking=false;validationError=string.Join("\n",errors.Distinct());UpdateLaunch();}
        }
        }
        catch(Exception e)when(e is not OutOfMemoryException){commandsChecking=false;validationError="命令检查中断："+e.Message;UpdateLaunch();}
        finally{commandsRunning=false;}
    }
    private void UpdateLaunch()
    {
        var count=config.Draft.Slots.Count(s=>s.Enabled);var reserved=status?.Entries?.Where(e=>!Store.IsTerminal(e)).Aggregate(0UL,(sum,e)=>sum+e.Plan.Budget)??0;
        var admission=status?.Memory is {} memory?ResourcePolicy.Check(memory,reserved,(ulong)count*ResourcePolicy.AgentBudget):new Admission(false,"等待资源检测");
        var missing=config.Draft.Slots.Where(s=>s.Enabled).Any(s=>string.IsNullOrWhiteSpace(s.Directory)&&string.IsNullOrWhiteSpace(config.Draft.Project));
        LaunchButton.Content=busy?"正在处理…":$"启动 {count} 个 Agent";
        LaunchButton.IsEnabled=!busy&&!readOnly&&!commandsChecking&&count>0&&!missing&&admission.Allowed&&status?.Ok==true&&validationError.Length==0&&submittedRevision!=config.Draft.Revision&&submittedAttempts.Length==0;
        LaunchHint.Text=busy?"正在准备目录并提交终端":submittedAttempts.Length>0?"本次启动等待逐项确认；可在运行记录中撤销未领取项":missing?"先选择项目文件夹":!admission.Allowed?admission.Reason:validationError.Length>0?validationError:"目录、命令和资源检查已通过";
        LaunchHint.Foreground=(Brush)FindResource(LaunchButton.IsEnabled?"Ink":"Muted");
        if(status?.Memory is {} m){AvailableValue.Text=$"{GiB(m.Available)} GiB";BudgetValue.Text=$"{count*2} GiB";ResourceText.Text=$"{admission.Reason} · {m.At.LocalDateTime:HH:mm:ss} 采样";}
        else{AvailableValue.Text="—";BudgetValue.Text=$"{count*2} GiB";ResourceText.Text="资源检测不可用，暂缓启动";}
        if(status?.Entries?.Any(e=>!Store.IsTerminal(e))==true)SessionExpander.IsExpanded=true;
    }
    private static string GiB(ulong value)=>(value/(double)ResourcePolicy.GiB).ToString("F1");
    private string? commError;
    private async Task RefreshAsync()
    {
        if(refreshing)return;refreshing=true;
        try{status=await Ipc.EnsureMonitorAsync();if(submittedAttempts.Length>0&&submittedAttempts.All(id=>status.Entries?.Any(e=>e.Plan.Id==id&&Store.IsTerminal(e))==true)){submittedRevision=-1;submittedAttempts=[];}RenderSessions();UpdateLaunch();
            var err=status.Ok?null:status.Message;if(commError!=null&&ErrorText.Text==commError)ErrorText.Text="";commError=err;if(err!=null)ErrorText.Text=err;}
        catch(Exception e)when(e is IOException or OperationCanceledException or TimeoutException){status=null;commError="监控器不可用 · "+e.Message;ErrorText.Text=commError;UpdateLaunch();}
        finally{refreshing=false;}
    }
    private void RenderSessions()
    {
        var entries=status?.Entries??[];var signature=string.Join("|",entries.Select(e=>$"{e.Plan.Id}:{e.State}:{e.Detail}:{(e.State=="Pending"&&DateTimeOffset.UtcNow-e.Created>TimeSpan.FromSeconds(10))}"));
        if(signature==sessionSignature&&Sessions.Children.Count>0)return;sessionSignature=signature;Sessions.Children.Clear();
        if(entries.Length==0){Sessions.Children.Add(new TextBlock{Text="尚无启动记录。关闭配置窗口不会结束已运行的 Agent。",Foreground=(Brush)FindResource("Muted")});return;}
        foreach(var e in entries.Where(e=>e.Plan.Kind=="agent").OrderByDescending(e=>e.Created).Take(12))
        {
            var row=new StackPanel{Margin=new(0,0,0,8)};
            var detail=e.State=="Pending"&&DateTimeOffset.UtcNow-e.Created>TimeSpan.FromSeconds(10)?"结果未确认；可撤销尚未领取的请求":e.Detail;
            row.Children.Add(new TextBlock{Text=$"{e.Plan.Position} {e.Plan.Name} · {detail}",FontWeight=FontWeights.SemiBold});
            var path=new TextBox{Text=e.Plan.Cwd,IsReadOnly=true,FontSize=12,MinHeight=28};row.Children.Add(path);
            if(e.State=="Pending"){var revoke=new Button{Content="撤销未启动项",HorizontalAlignment=HorizontalAlignment.Left};revoke.Click+=async(_,_)=>await GuardAsync(async()=>{var result=await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:e.Plan.Id));ProgressText.Text=result.Message;await RefreshAsync();});row.Children.Add(revoke);}
            Sessions.Children.Add(row);
        }
    }
    private async void LaunchClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        if(busy||readOnly||submittedAttempts.Length>0)return;ProjectLostFocus(this,new());
        var draft=config.Draft.Copy();busy=true;Editor.IsEnabled=false;CancelButton.IsEnabled=true;UpdateLaunch();ErrorText.Text="";launchCancellation=new();
        try
        {
            await saveTask;if(saveError!=null)throw new IOException("先解决配置保存失败，再启动。");
            var terminal=await Task.Run(()=>Commands.Terminal(config.TerminalPath));await RefreshAsync();
            var reserved=status?.Entries?.Where(x=>!Store.IsTerminal(x)).Aggregate(0UL,(sum,x)=>sum+x.Plan.Budget)??0;
            var admission=ResourcePolicy.Check(status?.Memory??throw new IOException("资源不可用"),reserved,(ulong)draft.Slots.Count(s=>s.Enabled)*ResourcePolicy.AgentBudget);if(!admission.Allowed)throw new IOException(admission.Reason);
            ProgressText.Text="正在核对命令和工作目录…";
            var plans=await Task.Run(()=>new WorkspacePreparer().PrepareAsync(draft,text=>Dispatcher.Invoke(()=>ProgressText.Text=text),text=>Dispatcher.InvokeAsync(()=>Task.FromResult(MessageBox.Show(this,text,"独立目录基线",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)).Task.Unwrap(),launchCancellation.Token));
            launchCancellation.Token.ThrowIfCancellationRequested();
            var reserve=await Ipc.SendAsync(new("reserve",Guid.NewGuid(),plans.ToArray()));if(!reserve.Ok)throw new IOException(reserve.Message);
            submittedRevision=draft.Revision;submittedAttempts=plans.Select(p=>p.Id).ToArray();
            try{await Task.Run(()=>Commands.SubmitTerminal(terminal,plans,draft.Layout));}
            catch{ProgressText.Text="Terminal 提交结果未确认。请核对记录或撤销未领取项，不要重复提交。";throw;}
            ProgressText.Text="已提交到 Windows Terminal，等待逐项确认。进程启动不代表 Agent 已就绪。";
            for(var i=0;i<10;i++){await Task.Delay(1000);await RefreshAsync();if(plans.All(p=>status?.Entries?.Any(x=>x.Plan.Id==p.Id&&x.Detail.StartsWith("已启动进程"))==true)){if(config.MinimizeAfterLaunch)WindowState=WindowState.Minimized;break;}}
        }
        finally{busy=false;Editor.IsEnabled=true;CancelButton.IsEnabled=false;launchCancellation.Dispose();launchCancellation=null;await RefreshAsync();}
    });
    private void CancelClick(object sender,RoutedEventArgs e){launchCancellation?.Cancel();ProgressText.Text="已请求停止后续步骤；正在进行的 Git 写操作会先完成。已提交项请使用逐项撤销。";}
    private void SelectProject(string path){Edit(d=>d.Project=path);if(path.Length>0){config.RecentProjects.Remove(path);config.RecentProjects.Insert(0,path);config.RecentProjects=config.RecentProjects.Take(8).ToList();QueueSave();}}
    private void ProjectLostFocus(object sender,RoutedEventArgs e){if(ProjectPath.Text!=config.Draft.Project)SelectProject(ProjectPath.Text.Trim());}
    private void ChooseClick(object sender,RoutedEventArgs e){if(busy||readOnly)return;var picker=new OpenFolderDialog{Title="选择项目文件夹"};if(picker.ShowDialog(this)==true)SelectProject(picker.FolderName);}
    private void RecentChanged(object sender,SelectionChangedEventArgs e){if(!rendering&&Recent.SelectedItem is string path)Edit(d=>d.Project=path);}
    private void AddClick(object sender,RoutedEventArgs e){if(config.Draft.Slots.Count<4)Edit(d=>d.Slots.Add(new()));}
    private void LayoutChanged(object sender,SelectionChangedEventArgs e){if(Layout.SelectedItem is ComboBoxItem {Tag:string value})Edit(d=>d.Layout=value);}
    private void DirectoryChanged(object sender,SelectionChangedEventArgs e){if(DirectoryMode.SelectedItem is ComboBoxItem {Tag:string value})Edit(d=>d.DirectoryMode=value);}
    private void TemplateChanged(object sender,SelectionChangedEventArgs e)
    {
        if(rendering||Templates.SelectedItem is not ComboBoxItem{Tag:string key}||key=="none")return;
        try{config.Restore(key=="last"?config.LastEdited??throw new InvalidOperationException("尚无上次组合"):config.Templates[key]);QueueSave();validationError="";ErrorText.Text="";Render();}catch(Exception ex)when(ex is not OutOfMemoryException){ErrorText.Text=ex.Message;}
    }
    private void SaveTemplateClick(object sender,RoutedEventArgs e)
    {
        var dialog=new FormWindow(this,"保存完整组合",[("name","组合名称","",false)],"保存选中项、程序/参数、目录策略、布局和项目绑定；不会覆盖现有同名组合。");
        if(dialog.ShowDialog()==true){try{config.SaveTemplate(dialog.Values["name"]);QueueSave();Render();}catch(ArgumentException ex){ErrorText.Text=ex.Message;}}
    }
    private void EditSlot(Guid id)
    {
        var slot=config.Draft.Slots.Single(s=>s.Id==id);var revision=config.Draft.Revision;var profile=slot.Current;
        var dialog=new FormWindow(this,$"{Agents.Get(slot.AgentId).Name} · 参数与目录",[("exe","程序路径（留空自动查找）",profile.Executable,false),("args","参数：每行一个，保留空格，不加外层引号",string.Join("\n",profile.Arguments),true),("cwd","覆盖工作目录（留空继承项目）",slot.Directory,false),("note","分工备注（不会自动发给 Agent）",slot.Note,true)],"切换 Agent 会保留各自的程序和参数。取消不修改配置。");
        if(dialog.ShowDialog()==true){if(revision!=config.Draft.Revision){ErrorText.Text="配置已改变，请重新打开编辑。";return;}Edit(d=>{var target=d.Slots.Single(s=>s.Id==id);target.Profiles[target.AgentId]=new(){Executable=dialog.Values["exe"].Trim(),Arguments=dialog.Values["args"].Length==0?[]:dialog.Values["args"].Replace("\r","").Split('\n')};target.Directory=dialog.Values["cwd"].Trim();target.Note=dialog.Values["note"];});}
    }
    private async void ResourceClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>{await RefreshAsync();if(status?.Memory is not {} m)return;MessageBox.Show(this,$"采样时间：{m.At.LocalDateTime:HH:mm:ss}\n可用物理内存：{GiB(m.Available)} GiB / {GiB(m.Total)} GiB\n提交余量：{GiB(m.CommitAvailable)} GiB\n整机 CPU：{m.CpuPercent:F1}%\n受管总 CPU 硬上限：35%\n每 Agent 内存预算：2 GiB；Git 准备：1 GiB\n活动预约：{status.Entries?.Count(x=>!Store.IsTerminal(x))??0}\n\n内存超额分配可能让工具失败。保护包含受管子进程；不控制外部程序。重新检查不会自动启动。","资源详情");});
    private FormWindow CreateSettingsDialog()=>new(this,"设置",[("terminal","Windows Terminal 路径（留空查找执行别名）",config.TerminalPath,false),("theme","主题",config.Theme,false),("minimize","启动全部确认后最小化窗口",config.MinimizeAfterLaunch.ToString().ToLowerInvariant(),false)],$"配置位置：{configPath}\n备份位于同路径 .bak。当前构建是 .NET 8 技术预览，完整性能门禁尚待验证。",new(){["theme"]=[("system","跟随系统"),("light","浅色"),("dark","深色")]},["minimize"]);
    private void SettingsClick(object sender,RoutedEventArgs e)
    {
        var dialog=CreateSettingsDialog();
        if(dialog.ShowDialog()==true){if(!new[]{"system","light","dark"}.Contains(dialog.Values["theme"])||!bool.TryParse(dialog.Values["minimize"],out var minimize)){ErrorText.Text="设置值无效，未应用。";return;}config.TerminalPath=dialog.Values["terminal"].Trim();config.Theme=dialog.Values["theme"];config.MinimizeAfterLaunch=minimize;QueueSave();ApplyTheme();Render();}
    }
    private async void RestoreBackupClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        var recovered=await Task.Run(()=>Store.Read<Configuration>(configPath+".bak"))??throw new IOException("没有可恢复备份。");
        if(recovered.SchemaVersion!=1||recovered.Draft==null||recovered.Templates==null||recovered.RecentProjects==null||recovered.TerminalPath==null)throw new IOException("备份版本或结构不支持，未恢复。");recovered.Draft.Validate();foreach(var t in recovered.Templates.Values)t.Validate();
        if(MessageBox.Show(this,"将用上一个有效备份恢复配置。当前损坏文件会另存保留。", "恢复配置",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
        await Task.Run(()=>{if(File.Exists(configPath))File.Copy(configPath,configPath+".corrupt-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));Store.Save(configPath,recovered);});
        config=recovered;readOnly=false;RestoreBackupButton.Visibility=Visibility.Collapsed;ErrorText.Text="";SaveStatus.Text="已从备份恢复";ApplyTheme();Render();
    });
    private async void WorkspacesClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        var records=await Task.Run(()=>{
            var directory=Store.FilePath("workspaces");var rows=new List<(string Root,string Target,string Branch,string Oid,string State)>();
            if(!Directory.Exists(directory))return rows;
            foreach(var file in Directory.EnumerateFiles(directory,"*.json").Take(200))
            {
                using var json=Store.Read<JsonDocument>(file);if(json==null)continue;var r=json.RootElement;
                rows.Add((r.GetProperty("Root").GetString()!,r.GetProperty("Target").GetString()!,r.GetProperty("Branch").GetString()!,r.GetProperty("Oid").GetString()!,r.GetProperty("State").GetString()!));
            }
            return rows;
        });
        var window=new Window{Owner=this,Title="独立工作目录与人工整合",Width=Math.Min(720,SystemParameters.WorkArea.Width-32),Height=Math.Min(650,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new StackPanel{Margin=new(24)};
        panel.Children.Add(new TextBlock{Text="检查各目录修改 → 分别提交需要保留的修改 → 在集成分支逐个合并 → 运行项目测试。启动器不会自动合并或删除目录。",Margin=new(0,0,0,16)});
        if(records.Count==0)panel.Children.Add(new TextBlock{Text="尚无自动创建的工作目录。手工指定的目录显示在每项配置和启动记录中。"});
        foreach(var record in records)
        {
            panel.Children.Add(new TextBlock{Text=$"分支：{record.Branch}\n基线：{record.Oid}\n状态：{(record.State=="Prepared"?"准备完成":"准备中断或待核对，不会自动重做")}",Margin=new(0,8,0,0)});
            panel.Children.Add(new TextBox{Text=record.Target,IsReadOnly=true});var buttons=new WrapPanel();
            var open=new Button{Content="打开工作目录"};open.Click+=(_,_)=>{try{if(!Directory.Exists(record.Target))throw new IOException("目录尚未创建或已移动。");Process.Start(new ProcessStartInfo(record.Target){UseShellExecute=true})?.Dispose();}catch(Exception ex)when(ex is IOException or Win32Exception){MessageBox.Show(window,ex.Message);}};buttons.Children.Add(open);
            var branch=new Button{Content="复制分支"};branch.Click+=(_,_)=>{try{Clipboard.SetText(record.Branch);}catch(System.Runtime.InteropServices.COMException){MessageBox.Show(window,"剪贴板正被占用，请稍后再试。");}};buttons.Children.Add(branch);
            var origin=new Button{Content="打开主项目"};origin.Click+=(_,_)=>{try{if(Directory.Exists(record.Root))Process.Start(new ProcessStartInfo(record.Root){UseShellExecute=true})?.Dispose();}catch(Win32Exception ex){MessageBox.Show(window,ex.Message);}};buttons.Children.Add(origin);panel.Children.Add(buttons);
        }
        window.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};window.ShowDialog();
    });
    private void ApplyTheme()
    {
        var dark=config.Theme=="dark";
        if(config.Theme=="system"){try{using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");dark=key?.GetValue("AppsUseLightTheme") is int value&&value==0;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){dark=false;}}
        var colors=dark?new[]{"#2B2B2B","#202020","#F5F5F5","#C5C5C5","#8A8A8A","#313438","#1F3B66","#565B63","#3D3120","#E8B45C"}:new[]{"#FFFFFF","#F5F5F5","#202020","#5F6368","#8A8A8A","#F1F2F4","#EDF2FC","#969BA4","#FFF5E5","#8A4B08"};
        var keys=new[]{"Surface","Page","Ink","Muted","Line","Raised","Selected","InputLine","WarningSurface","WarningInk"};for(var i=0;i<keys.Length;i++)Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if(SystemParameters.HighContrast){Application.Current.Resources["Surface"]=SystemColors.WindowBrush;Application.Current.Resources["Page"]=SystemColors.WindowBrush;Application.Current.Resources["Ink"]=SystemColors.WindowTextBrush;Application.Current.Resources["Muted"]=SystemColors.WindowTextBrush;Application.Current.Resources["Raised"]=SystemColors.ControlBrush;Application.Current.Resources["Selected"]=SystemColors.HighlightBrush;Application.Current.Resources["InputLine"]=SystemColors.WindowTextBrush;Application.Current.Resources["WarningSurface"]=SystemColors.WindowBrush;Application.Current.Resources["WarningInk"]=SystemColors.WindowTextBrush;}
        Background=(Brush)Application.Current.Resources["Page"];Foreground=(Brush)Application.Current.Resources["Ink"];
        ErrorText.Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#FFB4AB":"#B42318"));
    }
    private void OnDragOver(object sender,DragEventArgs e){e.Effects=!busy&&!readOnly&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private async void OnDrop(object sender,DragEventArgs e)=>await GuardAsync(async()=>
    {
        if(busy||readOnly||e.Data.GetData(DataFormats.FileDrop) is not string[] paths)return;
        if(paths.Length is <1 or >4)throw new IOException("一次最多拖入 4 个文件夹，未更改当前配置。");
        var verified=await Task.Run(()=>paths.Select(p=>PhysicalDirectory.Resolve(p).Path).ToArray());
        if(verified.Length==1)SelectProject(verified[0]);else Edit(d=>{while(d.Slots.Count<verified.Length)d.Slots.Add(new());for(var i=0;i<verified.Length;i++)d.Slots[i].Directory=verified[i];});
        ProgressText.Text=$"已填入 {verified.Length} 个目录，本次启用 {config.Draft.Slots.Count(s=>s.Enabled)} 项。";
    });
    private async void ClosingWindow(object? sender,CancelEventArgs e)
    {
        if(closing)return;e.Cancel=true;
        if(busy&&MessageBox.Show(this,"当前正在处理启动。关闭只停止后续准备，不结束已运行任务。关闭窗口？","关闭配置窗口",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        launchCancellation?.Cancel();await saveTask;
        if(saveError!=null&&MessageBox.Show(this,"配置尚未保存："+saveError+"\n仍要关闭？","保存失败",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        timer.Stop();closing=true;await Dispatcher.InvokeAsync(Close,DispatcherPriority.Background);
    }
}
