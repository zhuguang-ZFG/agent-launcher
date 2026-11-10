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
    private Task commandCheckTask=Task.CompletedTask;
    private string? saveError;
    private bool saving;
    private readonly List<(Slot Slot,int Index)> undoRemovals=[];
    private Response? status;
    private CancellationTokenSource? launchCancellation;
    private Guid[] submittedAttempts=[];
    private long submittedRevision=-1;
    private readonly string[] arguments;
    private string? sessionSignature;
    private readonly Dictionary<Guid,TextBlock> commandLabels=[];
    private readonly string configPath=Store.FilePath("configuration.json");
    private bool commandsChecking,commandsRunning;
#if INTEGRATION_TESTS
    private bool uiChecksStarted;
#endif
    private Draft? pendingCommandCheck;
    private string validationError="";
    private Dictionary<string,string[]> discoveredInstalls=[];
    private string[] discoveredTerminals=[];
    private bool discovering;
    private bool applyingSettings;
    private long projectInputRevision;
    private sealed record AgentChoice(string Id,string Name,string Label);
    private enum TemplateAction { LastEdited }
    public MainWindow(string[] args)
    {
        arguments=args;InitializeComponent();
        ProjectPath.TextChanged+=(_,_)=>{if(!rendering){projectInputRevision++;if(!loading)UpdateLaunch();}};
        var work=SystemParameters.WorkArea;Width=Math.Min(1120,work.Width-32);Height=Math.Min(800,work.Height-32);MinWidth=Math.Min(560,work.Width-32);MinHeight=Math.Min(460,work.Height-32);
        SizeChanged+=(_,_)=>ArrangePanels();
        Loaded+=async (_,_)=>await GuardAsync(LoadAsync);
        timer.Tick+=async (_,_)=>await GuardAsync(RefreshAsync);
        PreviewKeyDown+=(_,e)=>{if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.O){ChooseClick(this,new());e.Handled=true;}else if(Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.Enter&&LaunchButton.IsEnabled){LaunchClick(this,new());e.Handled=true;}};
        Closing+=ClosingWindow;
        SystemParameters.StaticPropertyChanged+=SystemAppearanceChanged;Closed+=(_,_)=>SystemParameters.StaticPropertyChanged-=SystemAppearanceChanged;
    }
    private void SystemAppearanceChanged(object? sender,PropertyChangedEventArgs e){if(!loading)Dispatcher.BeginInvoke(new Action(ApplyTheme));}
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
            if(config.SchemaVersion!=1||config.Draft==null||config.Templates==null||config.Templates.Values.Any(t=>t==null)||config.RecentProjects==null||config.RecentAgents==null||config.TerminalPath==null)throw new InvalidDataException("配置版本或结构不支持，禁止覆盖。");config.Draft.Validate();foreach(var template in config.Templates.Values)template.Validate();config.LastEdited?.Validate();
        }
        catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {readOnly=true;config=new Configuration();ErrorText.Text="配置无法读取，已禁止覆盖和启动。请在设置中检查配置文件及 .bak 备份。\n"+e.Message;}
        if(configMissing){readOnly=true;ErrorText.Text="配置文件丢失，检测到 .bak 备份。请核对后使用「恢复备份」；当前不会写入配置。";}
        loading=false;RestoreBackupButton.Visibility=readOnly?Visibility.Visible:Visibility.Collapsed;ApplyTheme();Render();_=GuardAsync(DiscoverCatalogAsync);await RefreshAsync();timer.Start();
#if INTEGRATION_TESTS
        if(arguments is ["--idle-check",var reportPath])
        {
            using var process=Process.GetCurrentProcess();var samples=new List<object>();var started=DateTime.UtcNow;var cpu=process.TotalProcessorTime;
            for(var i=0;i<30;i++){await Task.Delay(2000);process.Refresh();samples.Add(new{At=DateTimeOffset.UtcNow,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64});}
            var percent=(process.TotalProcessorTime-cpu).TotalSeconds/(DateTime.UtcNow-started).TotalSeconds/Environment.ProcessorCount*100;
            Store.Save(reportPath,new{Samples=samples,CpuPercent=percent,LogicalProcessors=Environment.ProcessorCount});Close();return;
        }
        if(arguments is ["--ui-check",var folder]&&!uiChecksStarted){uiChecksStarted=true;await RunUiChecksAsync(folder);return;}
        if(arguments is ["--screenshot",var path])
        {
            await Task.Delay(500);UpdateLayout();var dpi=VisualTreeHelper.GetDpi(this);var bitmap=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(this);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
            using(var process=Process.GetCurrentProcess()){process.Refresh();Store.Save(path+".json",new{Width=ActualWidth,Height=ActualHeight,Dpi=dpi.PixelsPerInchX,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,LaunchEnabled=LaunchButton.IsEnabled,Resource=ResourceText.Text,Error=ErrorText.Text});}
            Close();
        }
#endif
    }
    private bool Edit(Action<Draft> change)
    {
        if(rendering||loading||busy)return false;
        if(readOnly){Render();return false;}
        var focusName=Keyboard.FocusedElement is DependencyObject focused&&Within(focused,SlotList)?AutomationProperties.GetName(focused):null;
        config.Edit(change);validationError="";ErrorText.Text="";QueueSave();Render();
        if(focusName is {Length:>0}&&FindLogical(SlotList).FirstOrDefault(el=>AutomationProperties.GetName(el)==focusName) is IInputElement target)Keyboard.Focus(target);
        return true;
    }
    private static bool Within(DependencyObject? node,DependencyObject root){while(node!=null){if(node==root)return true;node=LogicalTreeHelper.GetParent(node)??(node is Visual v?VisualTreeHelper.GetParent(v):null);}return false;}
    private static IEnumerable<DependencyObject> FindLogical(DependencyObject root){foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()){yield return child;foreach(var nested in FindLogical(child))yield return nested;}}
    private bool SaveHealthy=>saveError==null&&pendingSave==null&&!saving;
    private void QueueSave()
    {
        if(readOnly||loading)return;
        pendingSave=JsonSerializer.SerializeToUtf8Bytes(config,Store.Json);
        if(saveError!=null){SaveStatus.Text="尚未保存 · "+saveError+"（可点「重试保存」）";RetrySaveButton.Visibility=Visibility.Visible;UpdateLaunch();return;}
        SaveStatus.Text="正在保存…";
        if(!saving)StartSaveLoop();
    }
    private void StartSaveLoop(){saving=true;saveTask=SaveAsync();}
    private async Task SaveAsync()
    {
        await Task.Delay(100);
        try
        {
            while(pendingSave is {} bytes)
            {
                pendingSave=null;
                try
                {
                    await Task.Run(()=>{var snapshot=JsonSerializer.Deserialize<Configuration>(bytes,Store.Json)!;Store.Save(configPath,snapshot);});
                    saveError=null;RetrySaveButton.Visibility=Visibility.Collapsed;SaveStatus.Text=pendingSave==null?"配置已保存":"正在保存…";UpdateLaunch();
                }
                catch(Exception e)when(e is not OutOfMemoryException)
                {
                    saveError=e.Message;pendingSave??=bytes;RetrySaveButton.Visibility=Visibility.Visible;SaveStatus.Text="尚未保存 · "+e.Message+"（可点「重试保存」）";UpdateLaunch();return;
                }
            }
        }
        finally{saving=false;UpdateLaunch();}
    }
    private async Task RetrySaveAsync()
    {
        if(readOnly||loading)throw new IOException("当前配置只读，不能保存；请先核对配置和备份。");
        pendingSave=JsonSerializer.SerializeToUtf8Bytes(config,Store.Json);
        RetrySaveButton.Visibility=Visibility.Visible;SaveStatus.Text="正在重试保存…";UpdateLaunch();
        while(true)
        {
            if(!saving)StartSaveLoop();
            await saveTask;
            if(saveError!=null)throw new IOException("保存仍未成功："+saveError);
            if(pendingSave is null)break;
        }
        SaveStatus.Text="配置已保存";RetrySaveButton.Visibility=Visibility.Collapsed;UpdateLaunch();
    }
    private async void RetrySaveClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>{await RetrySaveAsync();ErrorText.Text="";});
    private void Render()
    {
        commandsChecking=true;
        rendering=true;
        try
        {
            ProjectPath.Text=config.Draft.Project;var noProject=string.IsNullOrWhiteSpace(config.Draft.Project);ProjectTitle.Text=noProject?"还没有选择项目":Path.GetFileName(config.Draft.Project.TrimEnd('\\','/')) is {Length:>0} title?title:config.Draft.Project;
            ProjectGuide.Text=noProject?"三步开始：① 选择或拖入项目文件夹　② 勾选要启动的 Agent　③ 点右下角「启动」":"把别的文件夹拖进来也可以切换项目";
            Recent.ItemsSource=config.RecentProjects;Recent.SelectedIndex=-1;Recent.Visibility=config.RecentProjects.Count>0?Visibility.Visible:Visibility.Collapsed;
            DirectoryMode.SelectedIndex=config.Draft.DirectoryMode switch{"prepared"=>1,"shared"=>2,_=>0};Layout.SelectedIndex=config.Draft.Layout=="horizontal"?1:0;
            DirectoryHelp.Text=DirectoryModeHelp();
            Templates.Items.Clear();Templates.Items.Add(new ComboBoxItem{Content="套用组合方案…",IsEnabled=false});
            Templates.Items.Add(new ComboBoxItem{Content="上次用的组合",Tag=TemplateAction.LastEdited,IsEnabled=config.LastEdited!=null,ToolTip="最近一次切换组合前的配置"});
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
            if(active.Length==0)Preview.Children.Add(new TextBlock{Text="勾选 Agent 后，这里实时预览窗格布局",Foreground=new SolidColorBrush(Color.FromRgb(165,171,182)),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center});Layout.IsEnabled=active.Length==2;
            SelectionSummary.Text=$"已选 {active.Length} 个，共 {config.Draft.Slots.Count} 个";PaneCount.Text=$"{active.Length} 个窗格";ArrangePanels();
            UpdateLaunch();
        }
        finally{rendering=false;}
        _=GuardAsync(CheckCommandsAsync);
    }
    private string DirectoryModeHelp()
    {
        var active=config.Draft.Slots.Where(s=>s.Enabled).ToArray();
        return config.Draft.DirectoryMode switch
        {
            "shared"=>"大家在同一个文件夹里修改，能看到彼此的改动，也可能互相覆盖。已在运行的独立会话不受影响。",
            "prepared"=>"用上次准备好的目录；每个 Agent 的目录可在「详情」里单独改。",
            _ when active.Length<=1=>active.Any(s=>!string.IsNullOrWhiteSpace(s.Directory))?"这一项按「详情」里指定的目录启动，不会复制项目。":"单项使用原项目目录，不会创建副本；同一项目里有多个 Agent 时才自动准备独立副本。完成后需要你自己合并改动。",
            _=>"同一项目、同一目录的多个 Agent 会各自准备独立副本；已在「详情」中指定不同目录的项按各自目录启动，不再复制。完成后需要你自己合并改动。"
        };
    }
    private Border CreatePreviewPane(Slot slot)
    {
        var position=config.Draft.Slots.IndexOf(slot)+1;
        var shell=new Border{BorderThickness=new(1),CornerRadius=new(6),Padding=new(10),Margin=new(3)};shell.SetResourceReference(Border.BackgroundProperty,"PreviewSurface");shell.SetResourceReference(Border.BorderBrushProperty,"PreviewLine");
        var text=new TextBlock{Text=$"{position}  {slot.DisplayName}",FontSize=14,FontWeight=FontWeights.SemiBold};text.SetResourceReference(TextBlock.ForegroundProperty,"PreviewInk");
        var detail=new TextBlock{Text="启动后是一个终端窗格",FontSize=12,Margin=new(0,4,0,0)};detail.SetResourceReference(TextBlock.ForegroundProperty,"PreviewMuted");
        shell.Child=new StackPanel{Children={text,detail}};return shell;
    }
    private void ArrangePanels()
    {
        var logicalWidth=ActualWidth/UiScale.Value;var wide=logicalWidth>=900;var compact=logicalWidth<650;
        Brand.Visibility=compact?Visibility.Collapsed:Visibility.Visible;DockPanel.SetDock(HeaderActions,compact?Dock.Left:Dock.Right);
        Grid.SetRow(FooterActions,compact?1:0);Grid.SetColumn(FooterActions,compact?0:1);Grid.SetColumnSpan(FooterActions,compact?2:1);Grid.SetColumnSpan(FooterMessage,compact?2:1);FooterActions.Margin=compact?new(0,8,0,0):new(0);
        Grid.SetColumn(ConfigurationPanel,0);Grid.SetRow(ConfigurationPanel,0);Grid.SetColumn(SummaryPanel,wide?1:0);Grid.SetRow(SummaryPanel,wide?0:1);
        SummaryPanel.Margin=wide?new(0):new(0,20,0,0);ConfigColumn.Width=new GridLength(1,GridUnitType.Star);SummaryColumn.Width=wide?new GridLength(320):new GridLength(0);
        ConfigurationPanel.Margin=wide?new(0,0,20,0):new(0);
    }
    private AgentChoice[] AgentChoices()=>Agents.All.OrderBy(a=>a.Id=="custom"?3:config.RecentAgents.Contains(a.Id)?0:discoveredInstalls.GetValueOrDefault(a.Id)?.Length>0?1:2).ThenBy(a=>config.RecentAgents.Contains(a.Id)?config.RecentAgents.IndexOf(a.Id):int.MaxValue).Select(a=>new AgentChoice(a.Id,a.Name,a.Name+(a.Id=="custom"?"":discoveredInstalls.TryGetValue(a.Id,out var paths)?paths.Length>0?" · 找到命令":" · 未找到":""))).ToArray();
    private AgentChoice[] FilterChoices(IEnumerable<AgentChoice> choices,string? selected)=>choices.Where(a=>a.Id==selected||a.Name.Contains(AgentSearch.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
    private void RefreshAgentChoices()
    {
        if(SlotList==null)return;var choices=AgentChoices();var previous=rendering;rendering=true;
        try{foreach(var combo in FindLogical(SlotList).OfType<ComboBox>()){var selected=combo.SelectedValue as string;combo.ItemsSource=FilterChoices(choices,selected);combo.SelectedValue=selected;}}
        finally{rendering=previous;}
    }
    private void AgentSearchChanged(object sender,TextChangedEventArgs e){if(!loading)RefreshAgentChoices();}
    private void AddSlot(Slot slot)
    {
        var position=config.Draft.Slots.IndexOf(slot)+1;
        var row=new Grid{Margin=new(0,8,0,8)};row.RowDefinitions.Add(new(){Height=GridLength.Auto});row.RowDefinitions.Add(new(){Height=GridLength.Auto});
        row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var enabled=new CheckBox{IsChecked=slot.Enabled,VerticalAlignment=VerticalAlignment.Center,Margin=new(16,0,8,0),MinWidth=22};
        AutomationProperties.SetName(enabled,$"第 {position} 项本次启动");
        // Checked/Unchecked (not Click) so UIA TogglePattern and assistive tech take the same path as mouse/keyboard.
        void OnToggled(object _,RoutedEventArgs __){if(rendering)return;var value=enabled.IsChecked==true;if(!Edit(d=>d.Slots.Single(s=>s.Id==slot.Id).Enabled=value)){rendering=true;enabled.IsChecked=!value;rendering=false;}}
        enabled.Checked+=OnToggled;enabled.Unchecked+=OnToggled;row.Children.Add(enabled);
        var number=new TextBlock{Text=$"{position:00}",Foreground=(Brush)FindResource("Muted"),FontFamily=new FontFamily("Consolas"),VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,12,0)};Grid.SetColumn(number,1);row.Children.Add(number);
        var choices=FilterChoices(AgentChoices(),slot.AgentId);
        var choice=new ComboBox{ItemsSource=choices,DisplayMemberPath="Label",SelectedValuePath="Id",SelectedValue=slot.AgentId,VerticalAlignment=VerticalAlignment.Center,MinWidth=180,MaxWidth=240,MinHeight=36};AutomationProperties.SetName(choice,$"第 {position} 项 Agent");TextSearch.SetTextPath(choice,"Name");
        choice.SelectionChanged+=(_,_)=>{if(rendering||choice.SelectedValue is not string id)return;if(Edit(d=>d.Slots.Single(s=>s.Id==slot.Id).AgentId=id)){config.RecentAgents.Remove(id);config.RecentAgents.Insert(0,id);config.RecentAgents=config.RecentAgents.Take(9).ToList();QueueSave();RefreshAgentChoices();}else{rendering=true;choice.SelectedValue=slot.AgentId;rendering=false;}};Grid.SetColumn(choice,2);row.Children.Add(choice);
        var command=new TextBlock{Text=slot.Enabled?"正在检查…":"未勾选",Foreground=(Brush)FindResource("Muted"),FontSize=12,Margin=new(0,4,12,0),TextWrapping=TextWrapping.Wrap};commandLabels[slot.Id]=command;Grid.SetColumn(command,2);Grid.SetColumnSpan(command,4);Grid.SetRow(command,1);row.Children.Add(command);
        command.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        var detail=new Button{Content="详情",Style=(Style)FindResource("QuietButton"),Padding=new(8,4,8,4),VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(detail,$"第 {position} 项详情");
        detail.Click+=(_,_)=>EditSlot(slot.Id);Grid.SetColumn(detail,4);row.Children.Add(detail);
        var actions=new MenuButton(slot,position);Grid.SetColumn(actions,5);row.Children.Add(actions);
        row.RowDefinitions.Add(new(){Height=GridLength.Auto});
        row.SizeChanged+=(_,_)=>{var compact=row.ActualWidth<480;Grid.SetColumnSpan(choice,compact?4:1);Grid.SetRow(detail,compact?1:0);Grid.SetColumn(detail,compact?2:4);Grid.SetRow(actions,compact?1:0);Grid.SetRow(command,compact?2:1);Grid.SetColumn(command,compact?0:2);Grid.SetColumnSpan(command,compact?6:4);command.Margin=compact?new(16,4,12,0):new(0,4,12,0);};
        var directory=new TextBlock{Text=slot.RequiresDirectory&&slot.Directory.Length==0?"需要指定独立目录":slot.Directory.Length>0?"独立指定目录 · "+slot.Directory:"使用项目目录 · 可拖入文件夹单独指定",Style=(Style)FindResource("Caption"),Margin=new(16,4,12,0)};
        row.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(directory,3);Grid.SetColumnSpan(directory,6);row.Children.Add(directory);
        var container=new Border{BorderBrush=(Brush)FindResource("Line"),BorderThickness=new(0,0,0,position<config.Draft.Slots.Count?1:0),Child=row};AttachSlotDrag(container,number,directory,slot,position);SlotList.Children.Add(container);
    }
    private sealed class MenuButton : ContentControl
    {
        internal readonly ContextMenu menu=new();
        internal string[] MenuLabels=>menu.Items.OfType<MenuItem>().Select(i=>(string)i.Header).ToArray();
        public MenuButton(Slot slot,int position)
        {
            var button=new Button{Content="⋯",Style=(Style)Application.Current.Resources["QuietButton"],Padding=new Thickness(10,4,10,4),MinWidth=40,MinHeight=36};
            AutomationProperties.SetName(button,$"第 {position} 项更多操作");
            var items=new List<(MenuItem Item,Func<bool> Enabled)>();
            Menu("上移",()=>Owner.Edit(d=>{var i=d.Slots.FindIndex(s=>s.Id==slot.Id);if(i>0)(d.Slots[i-1],d.Slots[i])=(d.Slots[i],d.Slots[i-1]);}),()=>position>1);Menu("下移",()=>Owner.Edit(d=>{var i=d.Slots.FindIndex(s=>s.Id==slot.Id);if(i>=0&&i<d.Slots.Count-1)(d.Slots[i+1],d.Slots[i])=(d.Slots[i],d.Slots[i+1]);}),()=>position<Owner.config.Draft.Slots.Count);Menu("移除",()=>{var owner=Owner;var index=owner.config.Draft.Slots.FindIndex(s=>s.Id==slot.Id);if(index<0)return;var removed=owner.config.Draft.Slots[index];if(!owner.Edit(d=>d.Slots.RemoveAll(s=>s.Id==removed.Id)))return;owner.ProgressText.Text="已移除配置，不影响现有会话。";owner.SessionExpander.IsExpanded=true;owner.RememberRemoval(removed,index);},()=>Owner.config.Draft.Slots.Count>1);
            button.Click+=(_,_)=>{foreach(var (item,enabled) in items)item.IsEnabled=enabled();menu.PlacementTarget=button;menu.IsOpen=true;};
            Content=button;
            void Menu(string text,Action action,Func<bool> enabled){var item=new MenuItem{Header=text};item.Click+=(_,_)=>action();menu.Items.Add(item);items.Add((item,enabled));}
        }
        private MainWindow Owner=>Window.GetWindow(this) as MainWindow ?? throw new InvalidOperationException();
    }
    private async void DiscoverClick(object sender,RoutedEventArgs e)=>await GuardAsync(DiscoverCatalogAsync);
    private async Task DiscoverCatalogAsync()
    {
        if(discovering)return;discovering=true;DiscoverButton.IsEnabled=false;DiscoveryStatus.Text="正在查找 PATH 与常见安装目录，不会启动 CLI…";
        try
        {
            discoveredInstalls=await Task.Run(()=>{var directories=Commands.InstallSearchDirs();return Agents.All.Where(a=>a.Command.Length>0).ToDictionary(a=>a.Id,a=>Commands.DiscoverInstalls(a.Command,directories).ToArray());});
            DiscoveryStatus.Text=$"找到 {discoveredInstalls.Count(p=>p.Value.Length>0)} 种 CLI · 只确认命令位置，版本与登录状态未验证";RefreshAgentChoices();
            try{discoveredTerminals=await Task.Run(()=>TerminalDiscovery.Installs().ToArray());}
            catch(Exception ex)when(ex is not OutOfMemoryException){DiscoveryStatus.Text+="；Terminal 查询未完成："+ex.Message;}
            await CheckCommandsAsync();
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){DiscoveryStatus.Text="查找未完成："+ex.Message;}
        finally{discovering=false;DiscoverButton.IsEnabled=true;}
    }
    private Task CheckCommandsAsync()
    {
        pendingCommandCheck=config.Draft.Copy();commandsChecking=true;UpdateLaunch();
        if(!commandsRunning){commandsRunning=true;commandCheckTask=RunCommandChecksAsync();}
        return commandCheckTask;
    }
    private async Task RunCommandChecksAsync()
    {
        try
        {
        while(pendingCommandCheck is {} draft)
        {
            pendingCommandCheck=null;var errors=new List<string>();var labels=new Dictionary<Guid,(string Text,string Tooltip)>();
            foreach(var slot in draft.Slots.Where(s=>s.Enabled))
            {
                var position=draft.Slots.IndexOf(slot)+1;
                string detail;string? resolvedPath=null;
                try{var cmd=await Task.Run(()=>Commands.Resolve(slot.AgentId,slot.Current));resolvedPath=cmd.Executable;detail="找到命令 · 版本未验证";}
                catch(Exception e)when(e is not OutOfMemoryException){detail=e.Message;errors.Add($"第 {position} 项：{detail}");}
                var directory=string.IsNullOrWhiteSpace(slot.Directory)?draft.Project:slot.Directory;
                if(string.IsNullOrWhiteSpace(directory))detail+=" · 未设置工作目录";
                else
                {
                    try{await Task.Run(()=>PhysicalDirectory.Resolve(directory));detail+=" · 目录已核对";}
                    catch(Exception e)when(e is not OutOfMemoryException){detail+=" · 目录不可用";errors.Add($"第 {position} 项目录不可用：{e.Message}");}
                }
                labels[slot.Id]=(detail,resolvedPath??detail);
            }
            if(draft.Revision!=config.Draft.Revision){pendingCommandCheck=config.Draft.Copy();continue;}
            if(!string.Equals(ProjectPath.Text.Trim(),config.Draft.Project,StringComparison.Ordinal)){commandsChecking=false;validationError="项目路径输入尚未应用；请按 Enter 或移开焦点完成校验。";UpdateLaunch();break;}
            foreach(var (id,label) in labels)if(commandLabels.TryGetValue(id,out var text)){text.Text=label.Text;text.ToolTip=label.Tooltip;}
            commandsChecking=false;validationError=string.Join("\n",errors.Distinct());UpdateLaunch();
        }
        }
        catch(Exception e)when(e is not OutOfMemoryException){commandsChecking=false;validationError="命令/目录检查中断："+e.Message;}
        finally{commandsRunning=false;UpdateLaunch();}
    }
    private void UpdateLaunch()
    {
        var count=config.Draft.Slots.Count(s=>s.Enabled);var reserved=status?.Entries?.Where(e=>!Store.IsTerminal(e)).Aggregate(0UL,(sum,e)=>sum+e.Plan.Budget)??0;
        var admission=status?.Memory is {} memory?ResourcePolicy.Check(memory,reserved,(ulong)count*ResourcePolicy.AgentBudget):new Admission(false,"等待资源检测");
        var admissionBlockedReason=status?.AdmissionBlockedReason;var admissionBlocked=!string.IsNullOrWhiteSpace(admissionBlockedReason);
        var explicitDirectoryMissing=config.Draft.Slots.Any(s=>s.Enabled&&s.RequiresDirectory&&string.IsNullOrWhiteSpace(s.Directory));
        var missing=explicitDirectoryMissing||config.Draft.Slots.Where(s=>s.Enabled).Any(s=>string.IsNullOrWhiteSpace(s.Directory)&&string.IsNullOrWhiteSpace(config.Draft.Project));
        var pendingProject=!string.Equals(ProjectPath.Text.Trim(),config.Draft.Project,StringComparison.Ordinal);
        LaunchButton.Content=busy?"正在处理…":$"启动 {count} 个 Agent";
        LaunchButton.IsEnabled=!busy&&!readOnly&&!commandsChecking&&count>0&&!missing&&!pendingProject&&admission.Allowed&&!admissionBlocked&&status?.Ok==true&&validationError.Length==0&&submittedRevision!=config.Draft.Revision&&submittedAttempts.Length==0&&SaveHealthy;
        LaunchHint.Text=busy?"正在准备文件夹并打开终端":readOnly?"当前配置只读，已禁止启动与保存":submittedAttempts.Length>0?"已提交，正在等每个窗口确认；还没启动的可以在运行记录里撤销":pendingProject?"项目路径输入尚未应用；请移开焦点完成校验":explicitDirectoryMissing?"通用组合需要重新指定独立目录，请打开对应 Agent 的详情":missing?"先选择项目文件夹":count==0?"先勾选至少一个 Agent":admissionBlocked?admissionBlockedReason!:!admission.Allowed?admission.Reason:!SaveHealthy?saveError!=null?"配置尚未保存："+saveError:"正在保存配置…":validationError.Length>0?validationError:status?.Ok!=true?"正在等待后台检查就绪，暂时不能启动":commandsChecking?"正在检查命令和目录":"检查全部通过，可以启动";
        LaunchHint.Foreground=(Brush)FindResource(LaunchButton.IsEnabled?"Ink":"Muted");
        if(status?.Memory is {} m){AvailableValue.Text=$"{GiB(m.Available)} GiB";BudgetValue.Text=$"{count*2} GiB";ResourceText.Text=(admissionBlocked?admissionBlockedReason!:admission.Reason)+$" · {m.At.LocalDateTime:HH:mm:ss} 采样";}
        else{AvailableValue.Text="—";BudgetValue.Text=$"{count*2} GiB";ResourceText.Text="资源检测不可用，暂缓启动";}
        if(status?.Entries?.Any(e=>!Store.IsTerminal(e))==true)SessionExpander.IsExpanded=true;
    }
    private static string GiB(ulong value)=>(value/(double)ResourcePolicy.GiB).ToString("F1");
    private string? commError;
    private async Task RefreshAsync()
    {
        if(refreshing)return;refreshing=true;
        try{status=await Ipc.EnsureMonitorAsync();if(submittedAttempts.Length>0){submittedAttempts=submittedAttempts.Where(id=>status.Entries?.Any(e=>e.Plan.Id==id&&e.State=="Pending")==true).ToArray();if(submittedAttempts.Length==0)submittedRevision=-1;}RenderSessions();UpdateLaunch();
            var err=status.Ok?null:status.Message;if(commError!=null&&ErrorText.Text==commError)ErrorText.Text="";commError=err;if(err!=null)ErrorText.Text=err;}
        catch(Exception e)when(e is IOException or OperationCanceledException or TimeoutException){status=null;commError="监控器不可用 · "+e.Message;ErrorText.Text=commError;UpdateLaunch();}
        finally{refreshing=false;}
    }
    private void RenderSessions()
    {
        var entries=status?.Entries??[];var signature=string.Join("|",entries.Select(e=>$"{e.Plan.Id}:{e.State}:{e.Detail}:{(e.State=="Pending"&&DateTimeOffset.UtcNow-e.Created>TimeSpan.FromSeconds(10))}"));
        if(signature==sessionSignature&&Sessions.Children.Count>0)return;sessionSignature=signature;Sessions.Children.Clear();
        var agents=entries.Where(e=>e.Plan.Kind=="agent").OrderByDescending(e=>e.Created).ToArray();
        var history=agents.Where(Store.IsTerminal).Take(12).ToHashSet();
        var visible=agents.Where(e=>!Store.IsTerminal(e)||history.Contains(e)).ToArray();
        if(visible.Length==0&&undoRemovals.Count==0){Sessions.Children.Add(new TextBlock{Text="尚无启动记录。关闭配置窗口不会结束已运行的 Agent。",Foreground=(Brush)FindResource("Muted")});}
        foreach(var e in visible)
        {
            var row=new StackPanel{Margin=new(0,0,0,8)};
            var detail=e.State=="Pending"&&DateTimeOffset.UtcNow-e.Created>TimeSpan.FromSeconds(10)?"结果未确认；可撤销尚未领取的请求":e.Detail;
            row.Children.Add(new TextBlock{Text=$"{e.Plan.Position} {e.Plan.Name} · {detail}",FontWeight=FontWeights.SemiBold});
            var path=new TextBox{Text=e.Plan.Cwd,IsReadOnly=true,FontSize=12,MinHeight=28};row.Children.Add(path);
            if(e.State=="Pending"){var revoke=new Button{Content="撤销未启动项",HorizontalAlignment=HorizontalAlignment.Left};AutomationProperties.SetName(revoke,$"撤销未启动项 {e.Plan.Name}");revoke.Click+=async(_,_)=>await GuardAsync(async()=>{var result=await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:e.Plan.Id));ProgressText.Text=result.Message;await RefreshAsync();});row.Children.Add(revoke);}
            if(e.State is "FailedBeforeStart" or "Revoked"&&!entries.Any(next=>next.Plan.RetryOf==e.Plan.Id)){var retry=new Button{Content="仅重试此项",HorizontalAlignment=HorizontalAlignment.Left};retry.Click+=async(_,_)=>await GuardAsync(()=>RetrySessionAsync(e.Plan.Id));row.Children.Add(retry);}
            Sessions.Children.Add(row);
        }
        var clipped=agents.Count(Store.IsTerminal)-history.Count;if(clipped>0)Sessions.Children.Add(new TextBlock{Text=$"仅显示最近 12 条历史记录；另有 {clipped} 条较旧记录未展示。活动项始终显示。",Foreground=(Brush)FindResource("Muted")});
        foreach(var state in undoRemovals){var undo=new Button{Content="撤销移除",Style=(Style)FindResource("QuietButton"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,8)};AutomationProperties.SetName(undo,"撤销移除");undo.Click+=(_,_)=>UndoRemoval(state);Sessions.Children.Add(undo);}
    }
    private void RenderSessionsNow(){sessionSignature=null;RenderSessions();}
    private void RememberRemoval(Slot slot,int index)
    {
        undoRemovals.Insert(0,(slot.Copy(),index));while(undoRemovals.Count>4)undoRemovals.RemoveAt(undoRemovals.Count-1);RenderSessionsNow();
    }
    private void UndoRemoval((Slot Slot,int Index) state)
    {
        if(!undoRemovals.Contains(state))return;
        if(config.Draft.Slots.Count>=4){ProgressText.Text="已有 4 项，无法撤销移除；请先移除其他项。";return;}
        if(config.Draft.Slots.Any(s=>s.Id==state.Slot.Id)){undoRemovals.Remove(state);RenderSessionsNow();return;}
        if(Edit(d=>d.Slots.Insert(Math.Min(state.Index,d.Slots.Count),state.Slot.Copy()))){undoRemovals.Remove(state);ProgressText.Text="已撤销移除，配置已恢复。";}
        RenderSessionsNow();
    }
    private void SetConfigLocked(bool locked)
    {
        ProjectPath.IsEnabled=!locked;Recent.IsEnabled=!locked;Templates.IsEnabled=!locked;AgentSearch.IsEnabled=!locked;SlotList.IsEnabled=!locked;DirectoryMode.IsEnabled=!locked;
        ChooseProjectButton.IsEnabled=!locked;SaveTemplateButton.IsEnabled=!locked;DiscoverButton.IsEnabled=!locked&&!discovering;AddButton.IsEnabled=!locked&&config.Draft.Slots.Count<4;Layout.IsEnabled=!locked&&config.Draft.Slots.Count(s=>s.Enabled)==2;
    }
    private void SetBusy(bool value){busy=value;CancelButton.IsEnabled=value&&launchCancellation!=null;SetConfigLocked(value);UpdateLaunch();}
    private sealed record RevokeReport(int Revoked,int Retained,int Unknown);
    private async Task<RevokeReport> RevokeAttemptsAsync(IEnumerable<Guid> ids)
    {
        int revoked=0,retained=0,unknown=0;
        foreach(var id in ids.Distinct())
        {
            try
            {
                var result=await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:id));
                if(result.Ok)revoked++;else retained++;
            }
            catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException){unknown++;}
        }
        return new(revoked,retained,unknown);
    }
    private static string RevokeSummary(RevokeReport report)
    {
        var parts=new List<string>();
        if(report.Revoked>0)parts.Add($"已撤销 {report.Revoked} 项，迟到请求不会执行");
        if(report.Retained>0)parts.Add($"{report.Retained} 项已被领取或可能运行，仍需核对");
        if(report.Unknown>0)parts.Add($"{report.Unknown} 项撤销结果未确认");
        return parts.Count==0?"没有可撤销的待领取请求。":string.Join("；",parts)+"；已运行的任务不受影响。";
    }
    private async Task CancelAsync()
    {
        launchCancellation?.Cancel();
        var ids=submittedAttempts;
        if(ids.Length==0){ProgressText.Text="已请求停止后续步骤；正在进行的 Git 写操作会先完成。尚未提交可撤销的启动请求。";return;}
        ProgressText.Text="已请求停止后续步骤；正在通过监控器撤销尚未领取的启动请求…";
        var report=await RevokeAttemptsAsync(ids);
        await RefreshAsync();
        ProgressText.Text=RevokeSummary(report);
        if(report.Unknown>0)ErrorText.Text=$"撤销结果未确认：{report.Unknown} 项可能仍在等待领取；请稍后重试撤销，不要重复启动。";
    }
    private async Task RevokeOnCancelAsync(CancellationToken token)
    {
        if(!token.IsCancellationRequested||submittedAttempts.Length==0)return;
        ProgressText.Text="已停止后续步骤，正在撤销尚未领取的启动请求…";
        var report=await RevokeAttemptsAsync(submittedAttempts);
        await RefreshAsync();
        ProgressText.Text=RevokeSummary(report);
        if(report.Unknown>0)ErrorText.Text=$"撤销结果未确认：{report.Unknown} 项可能仍在等待领取；请在运行记录中核对。";
    }
    private async Task RetrySessionAsync(Guid id)
    {
        if(busy||readOnly||submittedAttempts.Length>0)return;
        launchCancellation=new();SetBusy(true);
        try
        {
            await saveTask;if(!SaveHealthy)throw new IOException("请先解决配置保存失败或等待保存完成。");
            var current=await Ipc.SendAsync(new("status",Guid.NewGuid()));if(!current.Ok)throw new IOException(current.Message);
            var previous=current.Entries?.SingleOrDefault(e=>e.Plan.Id==id)??throw new IOException("记录已变化，请重新核对。");
            if(previous.State is not ("FailedBeforeStart" or "Revoked"))throw new IOException("不能证明此项未运行，已阻止重复提交。");
            var terminal=await Task.Run(()=>Commands.Terminal(config.TerminalPath));
            var retry=previous.Plan with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid(),RetryOf=previous.Plan.Id};
            var reserve=await Ipc.ReserveWithCleanupAsync([retry],launchCancellation.Token);if(!reserve.Ok)throw new IOException(reserve.Message);
            submittedRevision=config.Draft.Revision;submittedAttempts=[retry.Id];
            if(launchCancellation.Token.IsCancellationRequested){await RevokeOnCancelAsync(launchCancellation.Token);return;}
            try{await Task.Run(()=>Commands.SubmitTerminal(terminal,[retry],"vertical"));}
            catch{ProgressText.Text="Terminal 提交结果未确认。请核对记录或撤销未领取项，不要重复提交。";throw;}
            if(launchCancellation.Token.IsCancellationRequested){await RevokeOnCancelAsync(launchCancellation.Token);return;}
            ProgressText.Text="只提交了这一项的新尝试；已有会话保持不变，正在等待回执。";
        }
        finally
        {
            var token=launchCancellation;launchCancellation=null;
            try{if(token?.IsCancellationRequested==true)await RevokeOnCancelAsync(token.Token);}finally{SetBusy(false);CancelButton.IsEnabled=false;token?.Dispose();}
            sessionSignature="";await RefreshAsync();
        }
    }
    private async void LaunchClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        if(busy||readOnly||submittedAttempts.Length>0)return;
        if(ProjectPath.Text.Trim()!=config.Draft.Project)SelectProject(ProjectPath.Text.Trim());
        launchCancellation=new();SetBusy(true);ErrorText.Text="";
        try
        {
            await CheckCommandsAsync();if(validationError.Length>0)throw new IOException(validationError);
            await saveTask;if(!SaveHealthy)throw new IOException(saveError!=null?"先解决配置保存失败，再启动。":"正在保存配置，请稍候。");
            var draft=config.Draft.Copy();
            var terminal=await Task.Run(()=>Commands.Terminal(config.TerminalPath));await RefreshAsync();
            var admissionBlocked=status?.AdmissionBlockedReason;if(!string.IsNullOrWhiteSpace(admissionBlocked))throw new IOException(admissionBlocked);
            var reserved=status?.Entries?.Where(x=>!Store.IsTerminal(x)).Aggregate(0UL,(sum,x)=>sum+x.Plan.Budget)??0;
            var admission=ResourcePolicy.Check(status?.Memory??throw new IOException("资源不可用"),reserved,(ulong)draft.Slots.Count(s=>s.Enabled)*ResourcePolicy.AgentBudget);if(!admission.Allowed)throw new IOException(admission.Reason);
            ProgressText.Text="正在核对命令和工作目录…";
            var plans=await Task.Run(()=>new WorkspacePreparer(config.WorktreeDirectory).PrepareAsync(draft,text=>Dispatcher.Invoke(()=>ProgressText.Text=text),text=>Dispatcher.InvokeAsync(()=>Task.FromResult(MessageBox.Show(this,text,"独立目录基线",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)).Task.Unwrap(),launchCancellation.Token));
            launchCancellation.Token.ThrowIfCancellationRequested();
            var reserve=await Ipc.ReserveWithCleanupAsync(plans.ToArray(),launchCancellation.Token);if(!reserve.Ok)throw new IOException(reserve.Message);
            submittedRevision=draft.Revision;submittedAttempts=plans.Select(p=>p.Id).ToArray();
            if(launchCancellation.Token.IsCancellationRequested){await RevokeOnCancelAsync(launchCancellation.Token);return;}
            try{await Task.Run(()=>Commands.SubmitTerminal(terminal,plans,draft.Layout));}
            catch{ProgressText.Text="Terminal 提交结果未确认。请核对记录或撤销未领取项，不要重复提交。";throw;}
            if(launchCancellation.Token.IsCancellationRequested){await RevokeOnCancelAsync(launchCancellation.Token);return;}
            ProgressText.Text="已交给 Windows Terminal 打开，正在等每个窗口确认。进程出现不代表 Agent 已就绪。";
            for(var i=0;i<10;i++)
            {
                if(launchCancellation.Token.IsCancellationRequested){await RevokeOnCancelAsync(launchCancellation.Token);break;}
                await Task.Delay(1000);await RefreshAsync();if(plans.All(p=>status?.Entries?.Any(x=>x.Plan.Id==p.Id&&x.Detail.StartsWith("已启动进程"))==true)){if(config.MinimizeAfterLaunch)WindowState=WindowState.Minimized;break;}
            }
        }
        finally
        {
            var token=launchCancellation;launchCancellation=null;
            try{if(token?.IsCancellationRequested==true)await RevokeOnCancelAsync(token.Token);}finally{SetBusy(false);CancelButton.IsEnabled=false;token?.Dispose();}
            await RefreshAsync();
        }
    });
    private void CancelClick(object sender,RoutedEventArgs e)=>_=GuardAsync(CancelAsync);
    private void SelectProject(string path){if(!Edit(d=>d.Project=path))return;if(path.Length>0){config.RecentProjects.Remove(path);config.RecentProjects.Insert(0,path);config.RecentProjects=config.RecentProjects.Take(8).ToList();QueueSave();}}
    private async void ProjectLostFocus(object sender,RoutedEventArgs e){if(ProjectPath.Text.Trim()!=config.Draft.Project)SelectProject(ProjectPath.Text.Trim());await GuardAsync(CheckCommandsAsync);}
    private void ChooseClick(object sender,RoutedEventArgs e){if(busy||readOnly)return;var picker=new OpenFolderDialog{Title="选择项目文件夹"};if(picker.ShowDialog(this)==true)SelectProject(picker.FolderName);}
    private void RecentChanged(object sender,SelectionChangedEventArgs e){if(!rendering&&Recent.SelectedItem is string path)Edit(d=>d.Project=path);}
    private void AddClick(object sender,RoutedEventArgs e){if(config.Draft.Slots.Count<4)Edit(d=>d.Slots.Add(new()));}
    private void LayoutChanged(object sender,SelectionChangedEventArgs e){if(Layout.SelectedItem is ComboBoxItem {Tag:string value})Edit(d=>d.Layout=value);}
    private void DirectoryChanged(object sender,SelectionChangedEventArgs e){if(DirectoryMode.SelectedItem is ComboBoxItem {Tag:string value})Edit(d=>d.DirectoryMode=value);}
    private void TemplateChanged(object sender,SelectionChangedEventArgs e)
    {
        if(rendering||Templates.SelectedItem is not ComboBoxItem{Tag:{} selection})return;
        try{config.Restore(selection is TemplateAction.LastEdited?config.LastEdited??throw new InvalidOperationException("尚无上次组合"):config.Templates[(string)selection]);QueueSave();validationError="";ErrorText.Text="";Render();}catch(Exception ex)when(ex is not OutOfMemoryException){ErrorText.Text=ex.Message;}
    }
    private void SaveTemplateClick(object sender,RoutedEventArgs e)
    {
        var dialog=new FormWindow(this,"保存完整组合",[("name","组合名称","",false),("binding","项目目录","bound",false)],"保存勾选项、程序/参数、目录策略与布局，不覆盖同名组合。通用组合不保存路径；原本单独指定的目录需要在新项目下重新选择。",choices:new(){["binding"]=[("bound","绑定当前项目与目录"),("portable","通用组合：使用时继承新项目")]});
        if(dialog.ShowDialog()==true){try{config.SaveTemplate(dialog.Values["name"],portable:dialog.Values["binding"]=="portable");QueueSave();Render();}catch(ArgumentException ex){ErrorText.Text=ex.Message;}}
    }
    private void EditSlot(Guid id)
    {
        var slot=config.Draft.Slots.Single(s=>s.Id==id);var revision=config.Draft.Revision;var profile=slot.Current;
        var discovered=(slot.AgentId=="custom"?discoveredInstalls.Values.SelectMany(paths=>paths):discoveredInstalls.GetValueOrDefault(slot.AgentId)??[]).Concat(config.Draft.Slots.Select(s=>s.Profiles.GetValueOrDefault(slot.AgentId)?.Executable??"")).Append(profile.Executable).Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fields=new List<(string Key,string Label,string Value,bool Multiline)>();
        if(slot.AgentId=="custom")fields.Add(("name","自定义 Agent 名称",slot.CustomName??"",false));
        fields.AddRange([("exe","程序路径（留空自动查找）",profile.Executable,false),("args","启动参数","",false),("cwd","覆盖工作目录（留空继承项目）",slot.Directory,false),("note","分工备注（不会自动发给 Agent）",slot.Note,true)]);
        var dialog=new FormWindow(this,$"{Agents.Get(slot.AgentId).Name} · 参数与目录",fields.ToArray(),"切换 Agent 会保留各自的程序和参数。取消不修改配置。",suggestions:new(){["exe"]=[..discovered]},argumentLists:new(){["args"]=profile.Arguments});
        if(dialog.ShowDialog()==true){if(revision!=config.Draft.Revision){ErrorText.Text="配置已改变，请重新打开编辑。";return;}Edit(d=>{var target=d.Slots.Single(s=>s.Id==id);target.Profiles[target.AgentId]=new(){Executable=dialog.Values["exe"].Trim(),Arguments=dialog.ArgumentValues["args"]};if(target.AgentId=="custom")target.CustomName=string.IsNullOrWhiteSpace(dialog.Values["name"])?null:dialog.Values["name"].Trim();target.Directory=dialog.Values["cwd"].Trim();if(dialog.Values.GetValueOrDefault("inheritDirectory")=="true")target.RequiresDirectory=false;target.Note=dialog.Values["note"];});}
    }
    private async void ResourceClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>{await RefreshAsync();if(status?.Memory is not {} m)return;MessageBox.Show(this,$"采样时间：{m.At.LocalDateTime:HH:mm:ss}\n可用物理内存：{GiB(m.Available)} GiB / 共 {GiB(m.Total)} GiB\n系统提交余量：{GiB(m.CommitAvailable)} GiB\n整机 CPU：{m.CpuPercent:F1}%（受管进程总和上限 35%）\n预算：每个 Agent 2 GiB；每次目录准备 1 GiB\n进行中的预约：{status.Entries?.Count(x=>!Store.IsTerminal(x))??0}\n\n内存超额分配可能让工具失败。保护覆盖受管子进程，不控制其他程序。资源恢复后会自动解除阻止，但不会替你自动启动。","资源详情");});
    private FormWindow CreateSettingsDialog()=>new(this,"设置",[("terminal","Windows Terminal 路径（留空自动查找）",config.TerminalPath,false),("worktree","独立目录存放位置（留空使用默认位置）",config.WorktreeDirectory,false),("theme","主题",config.Theme,false),("scale","文字与控件大小",config.TextScalePercent.ToString(),false),("minimize","启动全部确认后最小化窗口",config.MinimizeAfterLaunch.ToString().ToLowerInvariant(),false)],$"配置位置：{configPath}\n更改独立目录位置只影响新准备，不移动或删除已有目录。当前为技术预览版，性能门禁尚未全部验证。",new(){["theme"]=[("system","跟随系统"),("light","浅色"),("dark","深色")],["scale"]=[("100","100% · 标准"),("125","125% · 较大"),("150","150% · 最大")]},["minimize"],suggestions:new(){["terminal"]=[..discoveredTerminals.Append(config.TerminalPath).Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)]},actions:new(){["诊断与导出"]=ShowDiagnostics,["管理最近项目"]=ManageRecentProjects,["管理组合"]=ManageTemplates});
    private async void SettingsClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        if(applyingSettings)return;applyingSettings=true;
        try
        {
            var dialog=CreateSettingsDialog();if(dialog.ShowDialog()!=true)return;if(readOnly||busy){ErrorText.Text="当前配置不可编辑，设置未保存。";return;}
            if(!new[]{"system","light","dark"}.Contains(dialog.Values["theme"])||!bool.TryParse(dialog.Values["minimize"],out var minimize)||!int.TryParse(dialog.Values["scale"],out var scale)||scale is not (100 or 125 or 150)){ErrorText.Text="设置值无效，未应用。";return;}
            var destination=dialog.Values["worktree"].Trim();if(destination.Length>0)destination=await Task.Run(()=>PhysicalDirectory.Resolve(destination).Path);
            if(readOnly||busy)return;config.WorktreeDirectory=destination;config.TerminalPath=dialog.Values["terminal"].Trim();config.Theme=dialog.Values["theme"];config.TextScalePercent=scale;config.MinimizeAfterLaunch=minimize;QueueSave();ApplyTheme();Render();
        }
        finally{applyingSettings=false;}
    });
    private async void RestoreBackupClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        var recovered=await Task.Run(()=>Store.Read<Configuration>(configPath+".bak"))??throw new IOException("没有可恢复备份。");
        if(recovered.SchemaVersion!=1||recovered.Draft==null||recovered.Templates==null||recovered.Templates.Values.Any(t=>t==null)||recovered.RecentProjects==null||recovered.RecentAgents==null||recovered.TerminalPath==null)throw new IOException("备份版本或结构不支持，未恢复。");recovered.Draft.Validate();foreach(var t in recovered.Templates.Values)t.Validate();recovered.LastEdited?.Validate();
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
        window.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};UiScale.Apply(window);window.ShowDialog();
    });
    private void ApplyTheme()
    {
        var dark=config.Theme=="dark";
        if(config.Theme=="system"){try{using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");dark=key?.GetValue("AppsUseLightTheme") is int value&&value==0;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){dark=false;}}
        var colors=dark?new[]{"#2B2B2B","#202020","#F5F5F5","#C5C5C5","#4B4F55","#313438","#1F3B66","#8A8A8A","#3D3120","#E8B45C"}:new[]{"#FFFFFF","#F5F5F5","#202020","#5F6368","#DEE0E4","#F1F2F4","#EDF2FC","#7A7A7A","#FFF5E5","#8A4B08"};
        var resources=Application.Current.Resources;var keys=new[]{"Surface","Page","Ink","Muted","Line","Raised","Selected","InputLine","WarningSurface","WarningInk"};for(var i=0;i<keys.Length;i++)resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        resources["Accent"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#60CDFF":"#245BC4"));resources["AccentInk"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#002B40":"#FFFFFF"));resources["SelectionInk"]=resources["Ink"];resources["ErrorInk"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#FFB4AB":"#B42318"));
        foreach(var (key,color) in new[]{("PreviewPage","#191B20"),("PreviewSurface","#0F1116"),("PreviewLine","#34373F"),("PreviewInk","#D7DBE2"),("PreviewMuted","#A5ABB6")})resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        if(SystemParameters.HighContrast)
        {
            foreach(var key in new[]{"Surface","Page","PreviewPage","PreviewSurface","WarningSurface"})resources[key]=SystemColors.WindowBrush;
            foreach(var key in new[]{"Ink","Muted","Line","InputLine","WarningInk","ErrorInk","PreviewInk","PreviewMuted","PreviewLine"})resources[key]=SystemColors.WindowTextBrush;
            resources["Raised"]=SystemColors.ControlBrush;resources["Selected"]=SystemColors.HighlightBrush;resources["Accent"]=SystemColors.HighlightBrush;resources["AccentInk"]=SystemColors.HighlightTextBrush;resources["SelectionInk"]=SystemColors.HighlightTextBrush;
        }
        SetResourceReference(BackgroundProperty,"Page");SetResourceReference(ForegroundProperty,"Ink");ErrorText.SetResourceReference(TextBlock.ForegroundProperty,"ErrorInk");LaunchHint.SetResourceReference(TextBlock.ForegroundProperty,LaunchButton.IsEnabled?"Ink":"Muted");
        UiScale.Value=config.TextScalePercent is 100 or 125 or 150?config.TextScalePercent/100d:1;UiScale.Apply(this);ArrangePanels();
    }
    private void OnDragOver(object sender,DragEventArgs e){e.Effects=!busy&&!readOnly&&e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
    private async void OnDrop(object sender,DragEventArgs e)
    {
        if(e.Data.GetData(DataFormats.FileDrop) is not string[] paths)return;e.Handled=true;
        await GuardAsync(()=>ApplyDroppedDirectoriesAsync(paths,null,config.Draft.Revision));
    }
    private async void ClosingWindow(object? sender,CancelEventArgs e)
    {
        if(closing)return;e.Cancel=true;
        if(busy&&MessageBox.Show(this,"当前正在处理启动。关闭只停止后续准备，不结束已运行任务。关闭窗口？","关闭配置窗口",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        launchCancellation?.Cancel();await saveTask;
        if(saveError!=null&&MessageBox.Show(this,"配置尚未保存："+saveError+"\n仍要关闭？","保存失败",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        timer.Stop();closing=true;await Dispatcher.InvokeAsync(Close,DispatcherPriority.Background);
    }
}
