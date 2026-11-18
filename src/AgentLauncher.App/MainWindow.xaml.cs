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
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;
public partial class MainWindow : Window
{
    private Configuration config=new();
    private bool rendering,busy,loading=true,readOnly,refreshing,closing;
    private bool launchFinalizing,launchStopping,closeRequested;
    private readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromSeconds(30)};
    private byte[]? pendingSave;
    private Task saveTask=Task.CompletedTask;
    private Task commandCheckTask=Task.CompletedTask;
    private Task launchTask=Task.CompletedTask;
    private Task cancelTask=Task.CompletedTask;
    private string? saveError;
    private bool saving;
    private readonly List<(Slot Slot,int Index)> undoRemovals=[];
    private Response? status;
    private CancellationTokenSource? launchCancellation;
    private Guid[] submittedAttempts=[];
    private long submittedRevision=-1;
    private long submissionGeneration;
    private readonly string[] arguments;
    private string? sessionSignature;
    private readonly Dictionary<Guid,TextBlock> commandLabels=[];
    private readonly Dictionary<Guid,TextBlock> railLabels=[];
    private readonly Dictionary<Guid,TextBlock> railNames=[];
    private readonly Dictionary<Guid,Ellipse> railDots=[];
    private readonly Dictionary<Guid,TextBlock> railSources=[];
    private Guid? selectedSlotId;
    private Draft? renderedDraft;
    private bool draftEditedByApp;
    private readonly string configPath=Store.FilePath("configuration.json");
    private bool commandsChecking,commandsRunning;
    private bool editingSlot;
#if INTEGRATION_TESTS
    private bool uiChecksStarted;
    private Task? commandCheckPause;
    internal static Func<string,string,MessageBoxButton,MessageBoxResult>? TestConfirm;
#endif
    private Draft? pendingCommandCheck;
    private string validationError="";
    private Dictionary<string,string[]> discoveredInstalls=[];
    private string[] discoveredTerminals=[];
    private bool discovering;
    private bool applyingSettings;
    private long projectInputRevision;
    private sealed record AgentChoice(string Id,string Name,string Label,Guid? HostId=null)
    {
        public string Key=>HostId==null?"local:"+Id:HostId+":"+Id;
    }
    private sealed record PlaceChoice(string Key,string Label,Guid? HostId);
    private enum TemplateAction { LastEdited }
    public MainWindow(string[] args)
    {
        arguments=args;InitializeComponent();
        ProjectPath.TextChanged+=(_,_)=>{if(!rendering){projectInputRevision++;if(!loading)UpdateLaunch();}};
        ProjectPath.KeyDown+=ProjectKeyDown;
        var work=SystemParameters.WorkArea;Width=Math.Min(920,work.Width-32);Height=Math.Min(760,work.Height-32);MinWidth=Math.Min(560,work.Width-32);MinHeight=Math.Min(460,work.Height-32);
        SizeChanged+=(_,_)=>ArrangePanels();
        Loaded+=async (_,_)=>await GuardAsync(LoadAsync);
        timer.Tick+=async (_,_)=>await GuardAsync(PollResourcesAsync);
        taskTimer.Tick+=async (_,_)=>await GuardAsync(PumpQueueAsync);
        PreviewKeyDown+=MainKeyDown;
        LaunchButton.ToolTip="Ctrl+Enter · 启动已勾选项";RefreshLaunchButton.ToolTip="F5 · 重新检查启动条件";
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
            if(config.Draft==null||config.Templates==null||config.Templates.Values.Any(t=>t==null)||config.RecentProjects==null||config.RecentAgents==null||config.TerminalPath==null)throw new InvalidDataException("配置版本或结构不支持，禁止覆盖。");config=ConfigurationCompatibility.Accept(config);
        }
        catch(Exception e)when(e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or InvalidDataException)
        {readOnly=true;config=new Configuration();ErrorText.Text="配置无法读取，已禁止覆盖和启动。请在设置中检查配置文件及 .bak 备份。\n"+e.Message;}
        if(configMissing){readOnly=true;ErrorText.Text="配置文件丢失，检测到 .bak 备份。请核对后使用「恢复备份」；当前不会写入配置。";}
        loading=false;RestoreBackupButton.Visibility=readOnly?Visibility.Visible:Visibility.Collapsed;ApplyTheme();Render();_=GuardAsync(DiscoverCatalogAsync);await LoadResourceViewAsync();await LoadTasksAsync();timer.Start();StartMemoryBoard();
#if INTEGRATION_TESTS
        if(arguments is ["--idle-check",var reportPath])
        {
            using var process=Process.GetCurrentProcess();var samples=new List<object>();var started=DateTime.UtcNow;var cpu=process.TotalProcessorTime;
            for(var i=0;i<30;i++){await Task.Delay(2000);process.Refresh();samples.Add(new{At=DateTimeOffset.UtcNow,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64});}
            var percent=(process.TotalProcessorTime-cpu).TotalSeconds/(DateTime.UtcNow-started).TotalSeconds/Environment.ProcessorCount*100;
            Store.Save(reportPath,new{Samples=samples,CpuPercent=percent,LogicalProcessors=Environment.ProcessorCount});Close();return;
        }
        if(arguments is ["--usability-check",var usabilityFolder]&&!uiChecksStarted)
        {
            uiChecksStarted=true;
            try{await RunTaskUsabilityOnlyAsync(usabilityFolder);}
            catch(Exception e)when(e is not OutOfMemoryException){Store.Save(System.IO.Path.Combine(usabilityFolder,"ui-exception.json"),new{Error=e.ToString()});Application.Current.Shutdown(1);}
            return;
        }
        if(arguments is ["--ui-check",var folder]&&!uiChecksStarted)
        {
            uiChecksStarted=true;
            try{await RunUiChecksAsync(folder);}
            catch(Exception e)when(e is not OutOfMemoryException){Store.Save(System.IO.Path.Combine(folder,"ui-exception.json"),new{Error=e.ToString()});Application.Current.Shutdown(1);}
            return;
        }
        if(arguments is ["--screenshot",var path])
        {
            await Task.Delay(500);UpdateLayout();var dpi=VisualTreeHelper.GetDpi(this);var bitmap=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(this);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);
            using(var process=Process.GetCurrentProcess()){process.Refresh();Store.Save(path+".json",new{Width=ActualWidth,Height=ActualHeight,Dpi=dpi.PixelsPerInchX,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,LaunchEnabled=LaunchButton.IsEnabled,Error=ErrorText.Text});}
            Close();
        }
        if(arguments is ["--screenshot-demo",var demoPath])
        {
            // 诊断截图：内存注入示例 VPS（文档 IP，无密钥）+ 本机/VPS 两槽位，展示来源徽章与远程 agent 候选；不写真实配置。
            var host=new RemoteHost(Guid.NewGuid(),"示例 VPS","203.0.113.10",22,"root","agent",new[]{"pi","kimi","qodercli"},null);
            config.RemoteHosts=new List<RemoteHost>{host};
            config.Theme="light";
            config.Draft.Project=Store.Root;
            var vps=new Slot{Enabled=true,AgentId="kimi",RemoteHostId=host.Id,RemoteDirectory="/home/agent/work/demo"};
            config.Draft.Slots=new List<Slot>{new(){Enabled=true,AgentId="pi"},vps};
            readOnly=false;ApplyTheme();Render();NavigateToSlot(vps.Id);
            await Task.Delay(700);UpdateLayout();
            var dpi=VisualTreeHelper.GetDpi(this);var bitmap=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);bitmap.Render(this);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(demoPath))encoder.Save(file);
            Close();
        }
#endif
    }
    private bool Edit(Action<Draft> change)
    {
        if(rendering||loading||busy||closeRequested)return false;
        if(readOnly){Render();return false;}
        var focusName=Keyboard.FocusedElement is DependencyObject focused&&(Within(focused,SlotList)||Within(focused,RailSlots))?AutomationProperties.GetName(focused):null;
        var before=config.Draft;config.Edit(change);TrackMultipaneSlotEdits(before,config.Draft);validationError="";ErrorText.Text="";QueueSave();
        // Keeps the rail selection on the edited slot: Render falls back to the first item only for a wholesale draft replacement (template/backup/reload).
        draftEditedByApp=true;Render();
        if(focusName is {Length:>0}&&FindLogical(SlotList).Concat(FindLogical(RailSlots)).FirstOrDefault(el=>AutomationProperties.GetName(el)==focusName) is IInputElement target)Keyboard.Focus(target);
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
    // Implemented by MainWindow.Rail.cs: the rail session/queue/resource summaries are rebuilt inside this existing Render cycle.
    partial void RenderRail();
    private void Render()
    {
        commandsChecking=true;
        rendering=true;
        try
        {
            ProjectPath.Text=config.Draft.Project;var noProject=string.IsNullOrWhiteSpace(config.Draft.Project);ProjectTitle.Text=MiddleEllipsis(noProject?"还没有选择项目":System.IO.Path.GetFileName(config.Draft.Project.TrimEnd('\\','/')) is {Length:>0} title?title:config.Draft.Project);ProjectTitle.ToolTip=noProject?null:config.Draft.Project;
            ProjectSwitchText.Text=noProject?"选择项目…":ProjectName(config.Draft.Project);ProjectSwitchButton.ToolTip=noProject?"选择项目或最近项目":config.Draft.Project+"\n点击切换最近项目";
            var ownDirectories=config.Draft.Slots.Count(s=>s.Enabled&&!string.IsNullOrWhiteSpace(s.Directory));
            ProjectGuide.Text=ownDirectories>0?$"已勾选的 {ownDirectories} 项使用下方各自的工作目录；修改此处不会改变它们。":noProject?"从上方选择项目，或把文件夹拖进窗口。":"未单独指定目录的 Agent 使用此项目";
            ProjectGuide.Visibility=Visibility.Visible;
            RenderRecentProjects();
            Recent.Visibility=Visibility.Collapsed;ProjectPath.Visibility=Visibility.Collapsed;ChooseProjectButton.Visibility=Visibility.Collapsed;
            DirectoryMode.SelectedIndex=config.Draft.DirectoryMode switch{"prepared"=>1,"shared"=>2,_=>0};Layout.SelectedIndex=config.Draft.Layout=="horizontal"?1:0;
            DirectoryHelp.Text=DirectoryModeHelp();
            SharedDirectoryRiskText.Text=LaunchSurface.SharedDirectoryRisk;
            SharedDirectoryRisk.Visibility=config.Draft.DirectoryMode=="shared"?Visibility.Visible:Visibility.Collapsed;
            Templates.Items.Clear();Templates.Items.Add(new ComboBoxItem{Content="套用组合方案…",IsEnabled=false});
            Templates.Items.Add(new ComboBoxItem{Content="上次用的组合",Tag=TemplateAction.LastEdited,IsEnabled=config.LastEdited!=null,ToolTip="最近一次切换组合前的配置"});
            foreach(var name in config.Templates.Keys)Templates.Items.Add(new ComboBoxItem{Content=name,Tag=name});Templates.SelectedIndex=0;
            var slots=config.Draft.Slots;
            // A wholesale draft replacement (template, backup, reload) retires the previously selected row; the selection then
            // falls back to the first item, exactly as a deletion does. App-initiated edits and rail clicks keep the selection.
            if(!ReferenceEquals(renderedDraft,config.Draft)&&!draftEditedByApp)
                selectedSlotId=renderedDraft is null
                    ?slots.FirstOrDefault(s=>s.Enabled)?.Id??slots.FirstOrDefault()?.Id
                    :slots.FirstOrDefault()?.Id;
            renderedDraft=config.Draft;draftEditedByApp=false;
            var selectedSlot=selectedSlotId.HasValue?slots.FirstOrDefault(s=>s.Id==selectedSlotId.Value):null;
            selectedSlot??=slots.FirstOrDefault(s=>s.Enabled)??slots.FirstOrDefault();
            selectedSlotId=selectedSlot?.Id;
            SlotList.Children.Clear();commandLabels.Clear();
            if(selectedSlot is {} detailSlot)AddSlot(detailSlot);
            if(selectedSlot is {} shown)DetailTitle.Text=$"{slots.IndexOf(shown)+1:00} · {shown.DisplayName}";else DetailTitle.Text="要一起启动的 Agent";
            RailSlots.Children.Clear();railLabels.Clear();railNames.Clear();railDots.Clear();railSources.Clear();syncBadges.Clear();
            for(var i=0;i<slots.Count;i++)AddRailSlot(slots[i],i+1);
            AddButton.IsEnabled=CanAddSlot;
            var active=config.Draft.Slots.Where(s=>s.Enabled).ToArray();
            RenderMultipanePreview(active);Layout.IsEnabled=active.Length==2;
            PaneCount.Text=$"{active.Length} 个窗格";ArrangePanels();
            UpdateLaunch();
            RenderRail();
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
        var detail=new TextBlock{Text="启动时自动加载 · 无需再输入命令",FontSize=12,Margin=new(0,4,0,0)};detail.SetResourceReference(TextBlock.ForegroundProperty,"PreviewMuted");
        var source=CreateSourceBadge(slot);source.Margin=new(0,4,0,0);
        shell.Child=new StackPanel{Children={text,detail,source}};return shell;
    }
    private void ArrangePanels()
    {
        var logicalWidth=ActualWidth/UiScale.Value;var compact=logicalWidth<650;var narrow=logicalWidth<900;
        // Below 900 logical pixels the rail keeps the launch checkbox and a separate numbered navigation target.
        Rail.Width=narrow?72:232;
        Rail.Padding=narrow?new Thickness(4,8,4,8):new Thickness(10,12,10,12);
        foreach(var row in FindLogical(RailSlots).OfType<Button>())
        {
            row.Padding=narrow?new Thickness(2,7,2,7):new Thickness(6,7,6,7);
            if(row.Content is Grid grid&&grid.Children.OfType<TextBlock>().FirstOrDefault() is {} number)number.Margin=narrow?new Thickness(0):new Thickness(6,0,6,0);
        }
        foreach(var dot in railDots.Values)dot.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        Brand.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        BrandText.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        ProjectCard.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        AgentHeader.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        SelectionSummary.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        RailSessionsSection.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        RailResource.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        RailOverflow.Visibility=narrow?Visibility.Visible:Visibility.Collapsed;
        AddButton.Content=narrow?"＋":"＋ 添加 Agent";
        foreach(var name in railNames.Values)name.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        foreach(var status in railLabels.Values)status.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        foreach(var source in railSources.Values)source.Visibility=narrow?Visibility.Collapsed:Visibility.Visible;
        // Compact: actions take the first footer row and the message wraps underneath, so a long hint cannot squeeze the launch button.
        Grid.SetRow(FooterActions,0);Grid.SetColumn(FooterActions,compact?0:1);Grid.SetColumnSpan(FooterActions,compact?2:1);
        Grid.SetRow(FooterMessage,compact?1:0);Grid.SetColumn(FooterMessage,0);Grid.SetColumnSpan(FooterMessage,compact?2:1);
        FooterActions.Margin=compact?new(0,0,0,8):new(0);FooterMessage.Margin=compact?new(0):new(0,0,16,0);
        Grid.SetColumn(ConfigurationPanel,0);Grid.SetRow(ConfigurationPanel,0);
        ConfigColumn.Width=new GridLength(1,GridUnitType.Star);SummaryColumn.Width=new GridLength(0);
        ConfigurationPanel.Margin=new(0);
    }
    private AgentChoice[] AgentChoices()=>Agents.All.OrderBy(a=>a.Id=="custom"?3:config.RecentAgents.Contains(a.Id)?0:discoveredInstalls.GetValueOrDefault(a.Id)?.Length>0?1:2).ThenBy(a=>config.RecentAgents.Contains(a.Id)?config.RecentAgents.IndexOf(a.Id):int.MaxValue).Select(a=>new AgentChoice(a.Id,a.Name,a.Name+(a.Id=="custom"?"":discoveredInstalls.TryGetValue(a.Id,out var paths)?paths.Length>0?" · 找到命令":" · 未找到":""))).ToArray();
    private AgentChoice[] ChoicesForSlot(Slot slot)
    {
        // 运行位置单独选择。Agent 下拉只列当前位置的程序，避免把云端主机混进 CLI 名称。
        if(slot.RemoteHostId is Guid hostId&&config.RemoteHosts.FirstOrDefault(h=>h.Id==hostId) is {} host)
        {
            var ids=host.AvailableAgents.Any()?host.AvailableAgents.ToList():new List<string>{"pi"};
            if(!ids.Contains(slot.AgentId))ids.Insert(0,slot.AgentId);
            return ids.Select(id=>{var name=Agents.All.FirstOrDefault(a=>a.Id==id)?.Name??id;return new AgentChoice(id,name,name,host.Id);}).ToArray();
        }
        return AgentChoices();
    }
    private static string SelectedKey(Slot slot)=>slot.RemoteHostId is Guid host?host+":"+slot.AgentId:"local:"+slot.AgentId;
    private AgentChoice[] FilterChoices(IEnumerable<AgentChoice> choices,string? selected)=>choices.Where(a=>a.Key==selected||a.Name.Contains(AgentSearch.Text.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
    private void RefreshAgentChoices()
    {
        if(SlotList==null)return;var previous=rendering;rendering=true;
        try{foreach(var combo in FindLogical(SlotList).OfType<ComboBox>()){if(AutomationProperties.GetName(combo) is string comboName&&comboName.Contains("运行位置"))continue;var selected=combo.SelectedValue as string;var slot=combo.Tag is Guid id?config.Draft.Slots.FirstOrDefault(s=>s.Id==id):null;var choices=slot==null?AgentChoices():ChoicesForSlot(slot);combo.ItemsSource=FilterChoices(choices,selected);combo.SelectedValue=selected;}}
        finally{rendering=previous;}
    }
    private void AgentSearchChanged(object sender,TextChangedEventArgs e){if(!loading)RefreshAgentChoices();}
    private void AddSlot(Slot slot)
    {
        var position=config.Draft.Slots.IndexOf(slot)+1;
        var row=new Grid{Margin=new(0,8,0,8)};row.RowDefinitions.Add(new(){Height=GridLength.Auto});row.RowDefinitions.Add(new(){Height=GridLength.Auto});
        row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var enabled=CreateSlotToggle(slot,position,false);enabled.Margin=new(16,0,8,0);enabled.MinWidth=28;row.Children.Add(enabled);
        var number=new TextBlock{Text=$"{position:00}",Foreground=(Brush)FindResource("Muted"),FontFamily=new FontFamily("Consolas"),VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,12,0)};Grid.SetColumn(number,1);row.Children.Add(number);
        var choices=FilterChoices(ChoicesForSlot(slot),SelectedKey(slot));
        var choice=new ComboBox{ItemsSource=choices,Tag=slot.Id,DisplayMemberPath="Label",SelectedValuePath="Key",SelectedValue=SelectedKey(slot),VerticalAlignment=VerticalAlignment.Center,MinWidth=140,MaxWidth=220,MinHeight=36};AutomationProperties.SetName(choice,$"第 {position} 项 Agent");TextSearch.SetTextPath(choice,"Name");
        choice.SelectionChanged+=(_,_)=>{if(rendering||choice.SelectedItem is not AgentChoice picked)return;var before=SelectedKey(slot);if(Edit(d=>{var target=d.Slots.Single(s=>s.Id==slot.Id);target.AgentId=picked.Id;})){config.RecentAgents.Remove(picked.Id);config.RecentAgents.Insert(0,picked.Id);config.RecentAgents=config.RecentAgents.Take(9).ToList();QueueSave();RefreshAgentChoices();}else{rendering=true;choice.SelectedValue=before;rendering=false;}};
        var places=new List<PlaceChoice>{new("local","本机",null)};
        foreach(var host in config.RemoteHosts)places.Add(new(host.Id.ToString(),"☁ "+host.Name,host.Id));
        if(slot.RemoteHostId is Guid bound&&places.All(p=>p.HostId!=bound))places.Add(new(bound.ToString(),"云端",bound));
        var place=new ComboBox{ItemsSource=places,DisplayMemberPath="Label",SelectedValuePath="Key",SelectedValue=slot.RemoteHostId?.ToString()??"local",VerticalAlignment=VerticalAlignment.Center,MinWidth=88,MaxWidth=150,MinHeight=36,Margin=new(0,0,8,0)};
        AutomationProperties.SetName(place,$"第 {position} 项运行位置");
        place.SelectionChanged+=(_,_)=>{if(rendering||place.SelectedItem is not PlaceChoice picked)return;var before=slot.RemoteHostId?.ToString()??"local";if(!Edit(d=>{var target=d.Slots.Single(s=>s.Id==slot.Id);target.RemoteHostId=picked.HostId;if(picked.HostId is Guid hostId){if(string.IsNullOrEmpty(target.RemoteDirectory))target.RemoteDirectory=RemoteWorkspaceMappingHelper.DeriveRemoteDirectory(target.Directory is {Length:>0} local?local:d.Project);var known=config.RemoteHosts.FirstOrDefault(h=>h.Id==hostId);var ids=known?.AvailableAgents is {Length:>0} list?list.ToList():new List<string>{"pi"};if(!ids.Contains(target.AgentId))target.AgentId=ids[0];}else target.RemoteDirectory=null;})){rendering=true;place.SelectedValue=before;rendering=false;}};
        var placeRow=new DockPanel{LastChildFill=true};DockPanel.SetDock(place,Dock.Left);placeRow.Children.Add(place);placeRow.Children.Add(choice);Grid.SetColumn(placeRow,2);row.Children.Add(placeRow);
        var command=new TextBlock{Text=slot.Enabled?"正在检查…":"未勾选 · 不启动、不预约",Foreground=(Brush)FindResource("Muted"),FontSize=12,Margin=new(16,4,12,0),TextWrapping=TextWrapping.Wrap};commandLabels[slot.Id]=command;Grid.SetColumnSpan(command,6);Grid.SetRow(command,1);row.Children.Add(command);
        command.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        var detail=new Button{Content="详情",ToolTip="程序路径、启动参数、工作目录与分工备注",Style=(Style)FindResource("QuietButton"),Padding=new(8,4,8,4),VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(detail,$"第 {position} 项详情");
        detail.Click+=(_,_)=>EditSlot(slot.Id);Grid.SetColumn(detail,4);row.Children.Add(detail);
        var actions=new MenuButton(slot,position);Grid.SetColumn(actions,5);row.Children.Add(actions);
        row.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var directory=new TextBlock{Text=slot.RequiresDirectory&&slot.Directory.Length==0?"选择工作目录…":slot.Directory.Length>0?slot.Directory:string.IsNullOrWhiteSpace(config.Draft.Project)?"选择工作目录…":"默认项目 · "+config.Draft.Project,Style=(Style)FindResource("Caption"),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
        var directoryButton=new Button{Content=directory,ToolTip="选择此 Agent 的工作目录；右键可改回默认项目。\n"+(slot.Directory.Length>0?slot.Directory:config.Draft.Project),Style=(Style)FindResource("QuietButton"),HorizontalContentAlignment=HorizontalAlignment.Stretch,Margin=new(8,0,4,0),MinWidth=60,Padding=new(6,4,6,4)};
        AutomationProperties.SetName(directoryButton,$"第 {position} 项工作目录");
        directoryButton.Click+=(_,_)=>ChooseSlotDirectory(slot.Id);
        var inherit=new MenuItem{Header="使用上方默认项目"};inherit.Click+=(_,_)=>SetSlotDirectory(slot.Id,"");directoryButton.ContextMenu=new ContextMenu{Items={inherit}};
        Grid.SetColumn(directoryButton,3);row.Children.Add(directoryButton);
        row.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var source=CreateSourceBadge(slot);source.Margin=new(16,2,0,0);var sourceRow=WrapSlotSource(slot,source);Grid.SetRow(sourceRow,3);Grid.SetColumnSpan(sourceRow,6);row.Children.Add(sourceRow);
        // Base the breakpoint on the window, not the row that this handler rearranges: long folder labels in
        // Auto columns can otherwise push the row across its own breakpoint repeatedly at larger text scales.
        // Compact stacks the directory button on its own span including the star column: two Auto-only spans
        // (directory + agent combo) otherwise inflate the Auto columns past the row width at 150% text scale.
        row.SizeChanged+=(_,_)=>{var compact=ActualWidth/UiScale.Value<650;directoryButton.MaxWidth=compact?160:double.PositiveInfinity;choice.MinWidth=compact?96:140;place.MinWidth=compact?72:88;Grid.SetColumnSpan(placeRow,compact?2:1);Grid.SetRow(detail,compact?1:0);Grid.SetRow(actions,compact?1:0);Grid.SetRow(command,compact?2:1);Grid.SetRow(directoryButton,compact?1:0);Grid.SetColumn(directoryButton,compact?0:3);Grid.SetColumnSpan(directoryButton,compact?4:1);};
        // v0.8 renders exactly the selected row, so a "separator before the next row" would draw a stray line.
        var container=new Border{BorderBrush=(Brush)FindResource("Line"),BorderThickness=new(0),Child=row};AttachSlotDrag(container,number,directory,slot,position);SlotList.Children.Add(container);
    }
    // Separate sibling controls keep checkbox toggles and navigation independent for mouse, keyboard and UIA.
    private void AddRailSlot(Slot slot,int position)
    {
        var row=new Grid{Margin=new(0,1,0,1)};
        row.RowDefinitions.Add(new(){Height=GridLength.Auto});row.RowDefinitions.Add(new(){Height=GridLength.Auto});
        row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var dot=new Ellipse{Width=12,Height=12,VerticalAlignment=VerticalAlignment.Center,Margin=new(2,0,0,0)};ApplyRailDot(dot,slot);Grid.SetRowSpan(dot,2);row.Children.Add(dot);
        var number=new TextBlock{Text=$"{position:00}",FontFamily=new FontFamily("Consolas"),FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new(6,0,6,0),TextWrapping=TextWrapping.NoWrap};number.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Grid.SetColumn(number,1);Grid.SetRowSpan(number,2);row.Children.Add(number);
        var name=new TextBlock{Text=slot.DisplayName,FontSize=13,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};name.SetResourceReference(TextBlock.ForegroundProperty,"Ink");Grid.SetColumn(name,2);row.Children.Add(name);
        var status=new TextBlock{Text=slot.Enabled?"正在检查…":"未勾选",FontSize=12,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,2,0,0),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=slot.DisplayName};status.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Grid.SetColumn(status,2);Grid.SetRow(status,1);row.Children.Add(status);
        railLabels[slot.Id]=status;railNames[slot.Id]=name;railDots[slot.Id]=dot;
        var source=CreateSourceBadge(slot);source.Margin=new(6,0,2,0);Grid.SetColumn(source,3);Grid.SetRowSpan(source,2);row.Children.Add(source);railSources[slot.Id]=source;
        var button=new Button{Content=row,Tag=slot.Id,Padding=new(6,7,6,7),HorizontalContentAlignment=HorizontalAlignment.Stretch,Background=Brushes.Transparent,BorderThickness=new(0),ToolTip=slot.DisplayName+" · "+Agents.Get(slot.AgentId).Name+(position<=9?$" · Alt+{position}":"")};
        AutomationProperties.SetName(button,$"第 {position} 项 Agent 行");
        if(selectedSlotId==slot.Id)button.SetResourceReference(Button.BackgroundProperty,"Selected");
        button.Click+=(_,_)=>NavigateToSlot(slot.Id);
        var container=new Grid();container.ColumnDefinitions.Add(new(){Width=GridLength.Auto});container.ColumnDefinitions.Add(new());
        var toggle=CreateSlotToggle(slot,position,true);toggle.Width=24;container.Children.Add(toggle);
        Grid.SetColumn(button,1);container.Children.Add(button);RailSlots.Children.Add(container);
    }
    private void ApplyRailDot(Ellipse dot,Slot slot)
    {
        var missing=railLabels.TryGetValue(slot.Id,out var text)&&text.Text.Contains("未找到");
        dot.SetResourceReference(Ellipse.FillProperty,!slot.Enabled?"StatusOff":missing?"StatusBad":"StatusOk");
    }
    // 来源徽标：本机（中性 Muted）vs VPS（云形 + 主机名，按 agent 颜色着色）。
    private static string AgentColorKey(string agentId)=>agentId switch{"pi"=>"AgentPi","kimi"=>"AgentKimi","qoder" or "qodercli"=>"AgentQoder","codex"=>"AgentCodex","omp"=>"AgentOmp",_=>"Accent"};
    private void SetAgentColorReference(FrameworkElement element,DependencyProperty property,string agentId){var key=AgentColorKey(agentId);element.SetResourceReference(property,TryFindResource(key)!=null?key:"Accent");}
    private TextBlock CreateSourceBadge(Slot slot)
    {
        var badge=new TextBlock{FontSize=11,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};
        var host=RemoteHostForSlot(slot);
        if(host==null){badge.Text="🖥 本机";badge.SetResourceReference(TextBlock.ForegroundProperty,"Muted");badge.ToolTip="在本机启动此 Agent";AutomationProperties.SetName(badge,"来源：本机");}
        else{badge.Text="☁ VPS · "+host.Name;SetAgentColorReference(badge,TextBlock.ForegroundProperty,slot.AgentId);badge.ToolTip=$"通过免密 SSH 在远程主机 {host.Name}（{host.Address}）上启动";AutomationProperties.SetName(badge,"来源：VPS "+host.Name);}
        return badge;
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
            Menu("将此项 Agent 应用到后续窗格",()=>Owner.ApplyAgentToSlots(slot.Id,false),()=>position<Owner.config.Draft.Slots.Count);
            Menu("将此项 Agent 应用到全部窗格",()=>Owner.ApplyAgentToSlots(slot.Id,true),()=>Owner.config.Draft.Slots.Count>1);
            menu.Items.Add(new Separator());
            Menu("复制此项配置",()=>Owner.DuplicateSlot(slot.Id),()=>Owner.CanAddSlot&&Owner.CanEditSlot(slot.Id));
            Menu("重命名…",()=>Owner.RenameSlot(slot.Id),()=>Owner.CanEditSlot(slot.Id));
            // 远程维护入口只对绑定了 VPS 主机的槽位启用；追加在末尾，不影响既有菜单项索引。
            bool Remote()=>Owner.config.Draft.Slots.FirstOrDefault(s=>s.Id==slot.Id) is {} current&&Owner.RemoteHostForSlot(current)!=null;
            menu.Items.Add(new Separator());
            Menu("测试远程免密连接",()=>_=Owner.GuardAsync(()=>Owner.RunSlotRemoteMaintenanceAsync(slot.Id,"test")),Remote);
            Menu("检查远程版本",()=>_=Owner.GuardAsync(()=>Owner.RunSlotRemoteMaintenanceAsync(slot.Id,"version")),Remote);
            Menu("升级远程 Agent",()=>_=Owner.GuardAsync(()=>Owner.RunSlotRemoteMaintenanceAsync(slot.Id,"upgrade")),Remote);
            Menu("续接上次远程会话…",()=>_=Owner.GuardAsync(()=>Owner.ResumeRemoteSessionAsync(slot.Id)),()=>Owner.config.Draft.Slots.FirstOrDefault(s=>s.Id==slot.Id) is {} current&&Owner.CanResumeRemoteSlot(current));
            // 实用追加（末尾追加，既有菜单项索引不变）：工作区改动查看 + VPS 同步健康刷新。
            menu.Items.Add(new Separator());
            Menu("查看改动",()=>_=Owner.GuardAsync(()=>Owner.ShowSlotChangesAsync(slot.Id)),()=>true);
            Menu("刷新同步状态",()=>_=Owner.GuardAsync(()=>Owner.RefreshSlotSyncHealthAsync(slot.Id)),Remote);
            button.Click+=(_,_)=>{foreach(var (item,enabled) in items)item.IsEnabled=Owner.CanEditSlot(slot.Id)&&enabled();menu.PlacementTarget=button;menu.IsOpen=true;};
            Content=button;
            void Menu(string text,Action action,Func<bool> enabled){var item=new MenuItem{Header=text};item.Click+=(_,_)=>action();menu.Items.Add(item);items.Add((item,enabled));}
        }
        private MainWindow Owner=>Window.GetWindow(this) as MainWindow ?? throw new InvalidOperationException();
    }
    private async void DiscoverClick(object sender,RoutedEventArgs e)=>await GuardAsync(DiscoverCatalogAsync);
    private async Task DiscoverCatalogAsync()
    {
        if(discovering)return;discovering=true;DiscoverButton.IsEnabled=false;DiscoveryStatus.Text="正在查找 PATH 与常见安装目录，不会启动 CLI…";
        commandLabelCache.Clear();
        try
        {
            discoveredInstalls=await Task.Run(()=>{var directories=Commands.InstallSearchDirs();return Agents.All.Where(a=>a.Command.Length>0).ToDictionary(a=>a.Id,a=>Commands.DiscoverInstalls(a.Command,directories).ToArray());});
            var found=Agents.All.Where(a=>discoveredInstalls.GetValueOrDefault(a.Id)?.Length>0).Select(a=>a.Name).ToArray();
            DiscoveryStatus.Text=found.Length==0?"没有找到本机 CLI；可在详情中选择已安装的程序":"已找到 "+string.Join("、",found)+" · 选中后随终端自动加载，版本与登录状态未验证";RefreshAgentChoices();
            try{discoveredTerminals=await Task.Run(()=>TerminalDiscovery.Installs().ToArray());}
            catch(Exception ex)when(ex is not OutOfMemoryException){DiscoveryStatus.Text+="；Terminal 查询未完成："+ex.Message;}
            await CheckCommandsAsync();
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){DiscoveryStatus.Text="查找未完成："+ex.Message;}
        finally{discovering=false;DiscoverButton.IsEnabled=!busy;}
    }
    private Task CheckCommandsAsync()
    {
        // Single consumer: a request only queues the latest draft and returns the running task. The loop's last pending check and
        // the finally that clears commandsRunning have no await between them, so no caller can observe "running but not consuming".
        // Adding an await there would reintroduce the starvation that the previous waiter loop caused.
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
#if INTEGRATION_TESTS
            // Deterministic in-flight edit fixture; no pause exists in the production build.
            if(commandCheckPause is {} pause){commandCheckPause=null;await pause;}
#endif
            foreach(var slot in draft.Slots.Where(s=>s.Enabled))
            {
                var position=draft.Slots.IndexOf(slot)+1;
                string detail;string? resolvedPath=null;
                try{resolvedPath=await ResolveCommandLabelAsync(slot);detail="程序与目录就绪";}
                catch(Exception e)when(e is not OutOfMemoryException){detail=e.Message;errors.Add($"第 {position} 项：{detail}");}
                var directory=string.IsNullOrWhiteSpace(slot.Directory)?draft.Project:slot.Directory;
                if(string.IsNullOrWhiteSpace(directory))detail+=" · 未设置工作目录";
                else
                {
                    try{await Task.Run(()=>PhysicalDirectory.Resolve(directory));}
                    catch(Exception e)when(e is not OutOfMemoryException){detail+=" · 目录不可用";errors.Add($"第 {position} 项目录不可用：{e.Message}");}
                }
                labels[slot.Id]=(detail,(resolvedPath??detail)+"\n"+SlotCostText(slot));
            }
            if(draft.Revision!=config.Draft.Revision){pendingCommandCheck=config.Draft.Copy();continue;}
            if(!string.Equals(ProjectPath.Text.Trim(),config.Draft.Project,StringComparison.Ordinal)){commandsChecking=false;validationError="项目路径输入尚未应用；请按 Enter 或移开焦点完成校验。";UpdateLaunch();break;}
            foreach(var (id,label) in labels)
            {
                if(commandLabels.TryGetValue(id,out var text)){text.Text=label.Text;text.ToolTip=label.Tooltip;}
                // The rail row carries the same status text; its dot turns red only for a command that is not found on this machine.
                if(railLabels.TryGetValue(id,out var railText)){railText.Text=label.Text;railText.ToolTip=label.Tooltip;}
                if(railDots.TryGetValue(id,out var dot))dot.SetResourceReference(Ellipse.FillProperty,label.Text.Contains("未找到")?"StatusBad":"StatusOk");
            }
            commandsChecking=false;validationError=string.Join("\n",errors.Distinct());UpdateLaunch();
        }
        }
        catch(Exception e)when(e is not OutOfMemoryException){commandsChecking=false;validationError="命令/目录检查中断："+e.Message;}
        finally{commandsRunning=false;UpdateLaunch();}
    }
    private void UpdateLaunch()
    {
        var count=config.Draft.Slots.Count(s=>s.Enabled);
        var places=config.Draft.Slots.Where(s=>s.Enabled).Select(s=>s.RemoteHostId is Guid id?config.RemoteHosts.FirstOrDefault(h=>h.Id==id)?.Name??"云端":null).ToArray();
        var admission=DecideAdmission(config.Draft,status?.Memory,status?.Entries,budgets:status?.Budgets);
        var admissionBlockedReason=status?.AdmissionHardBlockedReason;var admissionBlocked=!string.IsNullOrWhiteSpace(admissionBlockedReason);
        var admissionPressureBlocked=status?.AdmissionPressure==true&&LaunchesConservatively;
        var explicitDirectoryMissing=config.Draft.Slots.Any(s=>s.Enabled&&s.RequiresDirectory&&string.IsNullOrWhiteSpace(s.Directory));
        var missing=explicitDirectoryMissing||config.Draft.Slots.Where(s=>s.Enabled).Any(s=>string.IsNullOrWhiteSpace(s.Directory)&&string.IsNullOrWhiteSpace(config.Draft.Project));
        var pendingProject=!string.Equals(ProjectPath.Text.Trim(),config.Draft.Project,StringComparison.Ordinal);
        var concurrencyBlocked=ConcurrencyBlocked(count);
        LaunchButton.Content=LaunchSurface.LaunchButtonText(count,places,busy,previewOnly);
        var targetTip=LaunchSurface.TargetTooltip(places);if(targetTip.Length>0)LaunchButton.ToolTip=targetTip;
        LaunchButton.IsEnabled=!closeRequested&&!busy&&!launchFinalizing&&launchCancellation==null&&cancelTask.IsCompleted&&!readOnly&&!commandsChecking&&count>0&&!missing&&!pendingProject&&(admission.Launchable||IsPreviewStale)&&!admissionBlocked&&!admissionPressureBlocked&&!concurrencyBlocked&&status?.Ok==true&&validationError.Length==0&&submittedRevision!=config.Draft.Revision&&submittedAttempts.Length==0&&SaveHealthy;
        var hint=LaunchHintState(admission);LaunchHint.Text=hint.Text;LaunchHint.SetResourceReference(TextBlock.ForegroundProperty,hint.Key);
        ReconcileButton.Visibility=status?.ReconcileOffered==true&&!busy?Visibility.Visible:Visibility.Collapsed;
        UpdateQuickQueueButton();
        UpdateMultipaneSummary();UpdateSimpleStatus();UpdatePollingInterval();
    }
    // Text and foreground resource key for LaunchHint. Blocking states get WarningInk, running states Muted, a launchable
    // configuration Ink, and a practical-mode advisory stays WarningInk because the monitor may still refuse the plan.
    // The branch order is the same as the previous single-expression chain.
    private (string Text,string Key) LaunchHintState(AdmissionDecision? admission=null)
    {
        admission??=DecideAdmission(config.Draft,status?.Memory,status?.Entries,budgets:status?.Budgets);
        var admissionBlockedReason=status?.AdmissionHardBlockedReason;var admissionBlocked=!string.IsNullOrWhiteSpace(admissionBlockedReason);
        var admissionPressureBlocked=status?.AdmissionPressure==true&&LaunchesConservatively;
        var explicitDirectoryMissing=config.Draft.Slots.Any(s=>s.Enabled&&s.RequiresDirectory&&string.IsNullOrWhiteSpace(s.Directory));
        var missing=explicitDirectoryMissing||config.Draft.Slots.Where(s=>s.Enabled).Any(s=>string.IsNullOrWhiteSpace(s.Directory)&&string.IsNullOrWhiteSpace(config.Draft.Project));
        var pendingProject=!string.Equals(ProjectPath.Text.Trim(),config.Draft.Project,StringComparison.Ordinal);
        var count=config.Draft.Slots.Count(s=>s.Enabled);
        var concurrencyBlocked=ConcurrencyBlocked(count);
        return closeRequested?("正在关闭窗口，等待本次操作与配置保存完成…","Muted"):
            launchFinalizing&&launchStopping||launchCancellation?.IsCancellationRequested==true||!cancelTask.IsCompleted?("正在停止后续步骤并核对撤销结果，暂不能再次启动","Muted"):
            launchFinalizing?("正在完成本次启动收尾；可以编辑配置，暂不能再次启动","Muted"):
            busy?("正在准备文件夹并打开终端","Muted"):
            launchCancellation!=null?("正在核对预约失败后的撤销结果；可以编辑配置，暂不能再次启动","Muted"):
            readOnly?("当前配置只读，已禁止启动与保存","WarningInk"):
            submittedAttempts.Length>0?("已提交，正在等每个窗口确认；还没启动的可以在运行记录里撤销","Muted"):
            pendingProject?("项目路径输入尚未应用；请按 Enter 或移开焦点完成校验","Muted"):
            explicitDirectoryMissing?("通用组合需要重新指定独立目录，请打开对应 Agent 的详情","Muted"):
            missing?("先选择项目文件夹","Muted"):count==0?("先勾选至少一个 Agent","Muted"):
            concurrencyBlocked?(ConcurrencyLimitMessage,"WarningInk"):
            admissionBlocked?(admissionBlockedReason!,"WarningInk"):
            admissionPressureBlocked?(PressureMessage,"WarningInk"):
            admission.Kind==AdmissionKind.Deny&&!IsPreviewStale?(admission.Reason,"WarningInk"):
            !SaveHealthy?saveError!=null?("配置尚未保存："+saveError,"WarningInk"):("正在保存配置…","Muted"):
            validationError.Length>0?(validationError,"WarningInk"):
            status?.Ok!=true?("正在等待后台检查就绪，暂时不能启动","Muted"):
            commandsChecking?("正在检查命令和目录","Muted"):
            LaunchButton.IsEnabled?(previewOnly?"本机预览不是启动授权；点击后由监控器重新核验":admission.Advisory?admission.Reason:"检查全部通过，可以启动",previewOnly?"Ink":admission.Advisory?"WarningInk":"Ink"):("本次配置已提交，暂不能再次启动","Muted");
    }
    private static string GiB(ulong value)=>(value/(double)ResourcePolicy.GiB).ToString("F1");
    // Keeps the leading and trailing parts of a long project title so the parent folder hint and the leaf name both stay visible;
    // ProjectTitle still trims with CharacterEllipsis if the shortened text does not fit.
    private static string MiddleEllipsis(string text,int max=48)
    {
        if(text.Length<=max||max<8)return text;
        var head=(max-1)/2;var tail=max-1-head;
        return text[..head]+"…"+text[^tail..];
    }
    private static AdmissionDecision DecideAdmission(Draft draft,MemorySnapshot? memory,IEnumerable<ControlEntry>? entries,bool includeGitPreparation=false,IReadOnlyList<BudgetItem>? budgets=null)
    {
        if(memory is null)return new(AdmissionKind.Deny,"等待资源检测",0,0,0,0,0,0,0,false);
        var settings=draft.Resource??new ResourceSettings();
        var policy=ResourcePolicy.SnapshotFor(settings,memory.Total,memory.CommitLimit);
        var reserved=budgets?.ToList()??(entries??[]).Where(e=>!Store.IsTerminal(e)&&e.Plan.RemoteExecution is null).Select(e=>ResourcePolicy.BudgetOf(e.Plan,e.PoolMemoryLimit)).ToList();
        // One prepared batch shares one pool, exactly as WorkspacePreparer builds it; charging every slot on its own would bill a
        // shared pool once per slot and disable the launch button the monitor would allow.
        var poolId=ResourcePolicy.PoolModeOf(settings)==ResourcePolicy.PoolShared?Guid.NewGuid():(Guid?)null;
        var poolLimit=poolId is null?(ulong?)null:ResourcePolicy.PoolLimitOf(settings);
        // 远程槽位不参与本地 admission（远程进程不受本地内存限制）
        var localSlots=draft.Slots.Where(s=>s.Enabled&&s.RemoteHostId is null).ToList();
        var requested=localSlots.Select(s=>new BudgetItem(s.Id,"agent",settings.PlanningBytes,settings.JobMemoryLimit,poolId,poolLimit)).ToList();
        if(includeGitPreparation)
        {
            var enabled=localSlots.Select(s=>PhysicalDirectory.Resolve(string.IsNullOrWhiteSpace(s.Directory)?draft.Project:s.Directory).Key).ToList();
            if(draft.DirectoryMode=="isolated"&&enabled.GroupBy(key=>key,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))
                requested.Add(new BudgetItem(Guid.Empty,"git",ResourcePolicy.DefaultGitPlanningBytes,ResourcePolicy.GitJobMemoryLimit));
        }
        return ResourcePolicy.Evaluate(memory,policy,reserved,requested);
    }
    private string? commError;
    private async Task RefreshAsync()
    {
        if(resourceRefreshTask is {} pending){await pending;return;}
        var task=RefreshMonitorAsync();resourceRefreshTask=task;
        try{await task;}finally{if(ReferenceEquals(resourceRefreshTask,task))resourceRefreshTask=null;}
    }
    // Monitor-status fan-out: the polling seam refreshes sessions, the rail summary and the launch gate together.
    private void RenderMonitorStatus(){RenderSessions();RenderRail();UpdateLaunch();SessionNotifier.For(this).Observe(status?.Entries,config.NotifyOnSessionEventsEnabled);}
    private void RenderSessions()
    {
        var entries=status?.Entries??[];var signature=string.Join("|",entries.Select(e=>$"{e.Plan.Id}:{e.State}:{e.Detail}:{(e.State=="Pending"&&DateTimeOffset.UtcNow-e.Created>TimeSpan.FromSeconds(10))}:{(e.State=="Settled"&&e.Plan.Kind=="agent"&&FindLinkedRun(e.Plan.Id)!=null)}"));
        var blockedTools=entries.Count(ToolRecovery.CanOffer);
        SessionHeading.Text=blockedTools>0?$"运行记录 · {blockedTools} 项工具曾被内存拦截":"运行记录";
        var retryable=taskBook is null?[]:entries.Where(e=>e.State is "FailedBeforeStart" or "Revoked")
            .Where(e=>AttemptIdentity.CanRetryOutsideQueue(e.Plan,entries.Select(entry=>entry.Plan),taskBook)).Select(e=>e.Plan.Id).ToHashSet();
        signature+="|retry:"+string.Join(",",retryable);
        if(signature==sessionSignature&&(Sessions.Children.Count>0||ActiveSessions.Children.Count>0))return;sessionSignature=signature;Sessions.Children.Clear();ActiveSessions.Children.Clear();
        var agents=entries.Where(e=>e.Plan.Kind=="agent").OrderByDescending(e=>e.Created).ToArray();
        var history=agents.Where(Store.IsTerminal).Take(12).ToHashSet();
        var visible=agents.Where(e=>!Store.IsTerminal(e)||history.Contains(e)).ToArray();
        var live=visible.Where(e=>!Store.IsTerminal(e)).ToArray();
        ActiveNow.Visibility=live.Length>0?Visibility.Visible:Visibility.Collapsed;
        ActiveNowHint.Text=live.Any(e=>e.Plan.RemoteExecution!=null)
            ?"尚未结束的条目都在这里。进程出现后，Agent 是否就绪看终端。"+LaunchSurface.RemoteConnection+" "+LaunchSurface.ReattachIsNewPane
            :"尚未结束的条目都在这里。进程出现后，Agent 是否就绪看终端。关闭本窗口不会结束已运行的 Agent。";
        if(visible.Length==0&&undoRemovals.Count==0)Sessions.Children.Add(new TextBlock{Text="尚无启动记录。关闭配置窗口不会结束已运行的 Agent。",Foreground=(Brush)FindResource("Muted")});
        else if(live.Length>0&&live.Length==visible.Length&&undoRemovals.Count==0)Sessions.Children.Add(new TextBlock{Text="更早的记录会显示在这里。关闭配置窗口不会结束已运行的 Agent。",Foreground=(Brush)FindResource("Muted")});
        foreach(var e in visible)
        {
            var row=new StackPanel{Tag=e.Plan.Id,Focusable=true,Margin=new(0,0,0,8)};
            AutomationProperties.SetName(row,"运行记录 "+e.Plan.Name);
            var detail=e.State=="Pending"&&DateTimeOffset.UtcNow-e.Created>TimeSpan.FromSeconds(10)?"结果未确认；可撤销尚未领取的请求":e.Detail;
            row.Children.Add(new TextBlock{Text=$"{e.Plan.Position} {e.Plan.Name} · {detail}",FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
            var path=new TextBox{Text=e.Plan.Cwd,IsReadOnly=true,FontSize=12,MinHeight=28};row.Children.Add(path);
            if(ToolRecovery.CanOffer(e))
            {
                var copy=new Button{Content="复制继续提示",HorizontalAlignment=HorizontalAlignment.Left,ToolTip="等待已有工具结束或释放内存后，粘贴到原 Agent 终端。每次工具调用都会重新检查内存。"};
                AutomationProperties.SetName(copy,$"复制继续提示 {e.Plan.Name}");
                copy.Click+=(_,_)=>{try{Clipboard.SetText(ToolRecovery.ContinuePrompt);ProgressText.Text="已复制。等待内存释放后，粘贴到该任务原终端继续；不会新建会话。";}catch(System.Runtime.InteropServices.COMException){ProgressText.Text="剪贴板正忙，请稍后再点「复制继续提示」。";}};row.Children.Add(copy);
            }
            if(e.State=="Started"&&e.Plan.Kind=="agent")
            {
                var focus=new Button{Content="聚焦终端",HorizontalAlignment=HorizontalAlignment.Left,Style=(Style)FindResource("QuietButton"),Margin=new(0,4,0,0),ToolTip="把本次启动所在的分屏终端窗口切到前台；找不到说明窗口已关闭。"};
                AutomationProperties.SetName(focus,$"聚焦终端 {e.Plan.Name}");
                focus.Click+=(_,_)=>FocusTerminalWindow(e.Plan.BatchId,e.Plan.Id);
                row.Children.Add(focus);
            }
            AddChangeActions(row,e);
            if(e.State=="Pending"){var revoke=new Button{Content="撤销未启动项",HorizontalAlignment=HorizontalAlignment.Left};AutomationProperties.SetName(revoke,$"撤销未启动项 {e.Plan.Name}");revoke.Click+=async(_,_)=>await GuardAsync(async()=>{var result=await Ipc.SendAsync(new("revoke",Guid.NewGuid(),Attempt:e.Plan.Id));ProgressText.Text=result.Message;await RefreshAsync();});row.Children.Add(revoke);}
            if(e.State is "Disconnected" or "Reattaching")
            {
                // 断开与重新挂接都保持非终态。两块分开：释放不挂接，挂接不释放。不会自动重连。
                var warning=new TextBlock{Text=e.State=="Reattaching"?"正在重新挂接，占用仍保留。若窗格没有出现，可再次确认挂接；若远程进程已不在，请核对并确认释放。不会自动重连。":"SSH 连接已断开，远程进程可能仍在运行。名额仍占用。不会自动重连。",Foreground=(Brush)FindResource("WarningInk"),TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0)};
                row.Children.Add(warning);
                var reattachNote=new TextBlock{Text=LaunchSurface.ReattachEffect+" "+LaunchSurface.ReattachIsNewPane,TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)};
                AutomationProperties.SetName(reattachNote,"重新挂接的效果 "+e.Plan.Name);row.Children.Add(reattachNote);
                var reattach=new Button{Content="核对并确认重新挂接",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,4,0,0),ToolTip=LaunchSurface.ReattachEffect};
                AutomationProperties.SetName(reattach,$"核对并确认重新挂接 {e.Plan.Name}");
                reattach.Click+=async(_,_)=>await GuardAsync(()=>ConfirmRemoteReattachAsync(e));
                row.Children.Add(reattach);
                var releaseNote=new TextBlock{Text=LaunchSurface.ReleaseEffect,TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0),Foreground=(Brush)FindResource("WarningInk")};
                AutomationProperties.SetName(releaseNote,"释放名额的效果 "+e.Plan.Name);row.Children.Add(releaseNote);
                var release=new Button{Content="核对并确认释放",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,4,0,0),ToolTip=LaunchSurface.ReleaseEffect};
                AutomationProperties.SetName(release,$"核对并确认释放 {e.Plan.Name}");
                release.Click+=async(_,_)=>await GuardAsync(()=>ConfirmRemoteReleaseAsync(e));
                row.Children.Add(release);
            }
            if(e.State is "FailedBeforeStart" or "Revoked"&&!entries.Any(next=>next.Plan.RetryOf==e.Plan.Id))
            {
                if(retryable.Contains(e.Plan.Id))
                {
                    var retry=new Button{Content="仅重试此项",HorizontalAlignment=HorizontalAlignment.Left};AutomationProperties.SetName(retry,$"仅重试此项 {e.Plan.Name}");retry.Click+=async(_,_)=>await GuardAsync(()=>RetrySessionAsync(e.Plan.Id));row.Children.Add(retry);
                }
                else row.Children.Add(new TextBlock{Text="请从任务卡重新入队；来源无法核对时先检查任务记录。",TextWrapping=TextWrapping.Wrap});
            }
            if(!Store.IsTerminal(e))ActiveSessions.Children.Add(row);else Sessions.Children.Add(row);
        }
        var clipped=agents.Count(Store.IsTerminal)-history.Count;if(clipped>0)Sessions.Children.Add(new TextBlock{Text=$"仅显示最近 12 条历史记录；另有 {clipped} 条较旧记录未展示。活动项始终显示。",Foreground=(Brush)FindResource("Muted")});
        foreach(var state in undoRemovals){var undo=new Button{Content="撤销移除",Style=(Style)FindResource("QuietButton"),HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,8)};AutomationProperties.SetName(undo,"撤销移除");undo.Click+=(_,_)=>UndoRemoval(state);Sessions.Children.Add(undo);}
    }
    private void RenderSessionsNow(){sessionSignature=null;RenderSessions();}
    private void RememberRemoval(Slot slot,int index)
    {
        undoRemovals.Insert(0,(slot.Copy(),index));while(undoRemovals.Count>ResourcePolicy.MaxSlots)undoRemovals.RemoveAt(undoRemovals.Count-1);RenderSessionsNow();
    }
    private void UndoRemoval((Slot Slot,int Index) state)
    {
        if(!undoRemovals.Contains(state))return;
        if(config.Draft.Slots.Count>=ResourcePolicy.MaxSlots){ProgressText.Text="已有 16 项，无法撤销移除；请先移除其他项。";return;}
        if(config.Draft.Slots.Any(s=>s.Id==state.Slot.Id)){undoRemovals.Remove(state);RenderSessionsNow();return;}
        if(Edit(d=>d.Slots.Insert(Math.Min(state.Index,d.Slots.Count),state.Slot.Copy()))){undoRemovals.Remove(state);ProgressText.Text="已撤销移除，配置已恢复。";}
        RenderSessionsNow();
    }
    private void SetConfigLocked(bool locked)
    {
        ProjectPath.IsEnabled=!locked;Recent.IsEnabled=!locked;Templates.IsEnabled=!locked;AgentSearch.IsEnabled=!locked;SlotList.IsEnabled=!locked;DirectoryMode.IsEnabled=!locked;
        foreach(var toggle in FindLogical(RailSlots).OfType<CheckBox>())toggle.IsEnabled=!locked&&CanEditSlots;
        ChooseProjectButton.IsEnabled=!locked;SaveTemplateButton.IsEnabled=!locked;DiscoverButton.IsEnabled=!locked&&!discovering;AddButton.IsEnabled=!locked&&CanAddSlot;Layout.IsEnabled=!locked&&config.Draft.Slots.Count(s=>s.Enabled)==2;
    }
    private void SetBusy(bool value){busy=value;CancelButton.IsEnabled=value&&launchCancellation!=null;SetConfigLocked(value);UpdateLaunch();}
    private void UnlockForCleanup()
    {
        // The launch operation still owns its token or finalization gate. Editing is safe; starting another launch is not.
        SetBusy(false);CancelButton.IsEnabled=false;
    }
    private async Task FinishLaunchAsync()
    {
        var token=launchCancellation;launchFinalizing=true;launchStopping=token?.IsCancellationRequested==true;launchCancellation=null;UnlockForCleanup();
        try
        {
            if(token?.IsCancellationRequested==true)await RevokeOnCancelAsync(token.Token);
            sessionSignature=null;await RefreshAsync();
        }
        finally{token?.Dispose();launchFinalizing=false;UpdateLaunch();}
    }
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
    private Task CancelAsync()
    {
        launchCancellation?.Cancel();
        if(!cancelTask.IsCompleted)return cancelTask;
        return cancelTask=RevokeSubmittedAsync();
    }
    private async Task RevokeSubmittedAsync()
    {
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
        UnlockForCleanup();
        ProgressText.Text="已停止后续步骤，正在撤销尚未领取的启动请求…";
        var report=await RevokeAttemptsAsync(submittedAttempts);
        await RefreshAsync();
        ProgressText.Text=RevokeSummary(report);
        if(report.Unknown>0)ErrorText.Text=$"撤销结果未确认：{report.Unknown} 项可能仍在等待领取；请在运行记录中核对。";
    }
    private (ResourcePolicySnapshot Policy,string Hash,string Digest,long Revision,Guid[] Slots) ReservationBindings(LaunchItem[] items)
    {
        var memory=status?.Memory??throw new IOException("资源不可用");
        var settings=config.Draft.Resource??new ResourceSettings();
        var policy=ResourcePolicy.SnapshotFor(settings,memory.Total,memory.CommitLimit);
        ResourcePolicy.ValidateSettings(settings,memory.Total,memory.CommitLimit);
        var slots=items.All(i=>i.Kind=="git")||items.Any(i=>i.RetryOf!=null)?config.Draft.Slots.Where(s=>s.Enabled).Select(s=>s.Id).ToArray():items.Select(i=>i.SourceSlotId??throw new IOException("旧启动记录缺少稳定槽身份，请从配置重新启动。")).ToArray();
        return (policy,ResourcePolicy.PolicyHash(policy),ResourcePolicy.PlanDigest(items),config.Draft.Revision,slots);
    }
    private Task RetrySessionAsync(Guid id)
    {
        if(closeRequested||busy||launchFinalizing||launchCancellation!=null||!cancelTask.IsCompleted||readOnly||submittedAttempts.Length>0)return Task.CompletedTask;
        return launchTask=RetrySessionCoreAsync(id);
    }
    private async Task RetrySessionCoreAsync(Guid id)
    {
        launchCancellation=new();SetBusy(true);
        try
        {
            await saveTask;if(!SaveHealthy)throw new IOException("请先解决配置保存失败或等待保存完成。");
            var current=await Ipc.SendAsync(new("status",Guid.NewGuid()));if(!current.Ok)throw new IOException(current.Message);
            var previous=current.Entries?.SingleOrDefault(e=>e.Plan.Id==id)??throw new IOException("记录已变化，请重新核对。");
            if(previous.State is not ("FailedBeforeStart" or "Revoked"))throw new IOException("不能证明此项未运行，已阻止重复提交。");
            var retryBook=await Task.Run(taskStore.Load);
            if(!AttemptIdentity.CanRetryOutsideQueue(previous.Plan,current.Entries!.Select(entry=>entry.Plan),retryBook))
                throw new IOException("该记录来自任务队列或来源尚待核对，请从任务卡重新入队，保留并发限制与尝试记录。");
            var terminal=await Task.Run(()=>Commands.Terminal(config.TerminalPath));
            var retry=previous.Plan with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid(),RetryOf=previous.Plan.Id};
            await AuthorizePlansAsync([retry]);
            var retryBindings=ReservationBindings([retry]);
            var reserve=await Ipc.ReserveWithCleanupAsync([retry],launchCancellation.Token,UnlockForCleanup,retryBindings.Policy,retryBindings.Revision,retryBindings.Hash,retryBindings.Digest,retryBindings.Slots);if(!reserve.Ok)throw new IOException(reserve.Message);
            submittedRevision=config.Draft.Revision;submittedAttempts=[retry.Id];submissionGeneration++;
            if(launchCancellation.Token.IsCancellationRequested)return;
            try{await Task.Run(()=>Commands.SubmitTerminal(terminal,[retry],"vertical"));}
            catch{ProgressText.Text="Terminal 提交结果未确认。请核对记录或撤销未领取项，不要重复提交。";throw;}
            if(launchCancellation.Token.IsCancellationRequested)return;
            ProgressText.Text="只提交了这一项的新尝试；已有会话保持不变，正在等待回执。";
        }
        finally{await FinishLaunchAsync();}
    }
    private async void LaunchClick(object sender,RoutedEventArgs e)
    {
        if(closeRequested||busy||launchFinalizing||launchCancellation!=null||!cancelTask.IsCompleted||readOnly||submittedAttempts.Length>0)return;
        launchTask=LaunchAsync();
        await GuardAsync(()=>launchTask);
    }
    private async Task LaunchAsync()
    {
        if(ProjectPath.Text.Trim()!=config.Draft.Project)SelectProject(ProjectPath.Text.Trim());
        launchCancellation=new();SetBusy(true);ErrorText.Text="";
        try
        {
            await CheckFreshCommandsAsync();if(validationError.Length>0)throw new IOException(validationError);
            await saveTask;if(!SaveHealthy)throw new IOException(saveError!=null?"先解决配置保存失败，再启动。":"正在保存配置，请稍候。");
            var draft=config.Draft.Copy();
            var terminal=await Task.Run(()=>Commands.Terminal(config.TerminalPath));await RefreshAsync();
            if(status?.Ok!=true)throw new IOException(status?.Message??"监控状态不可用");
            var admissionBlocked=status?.AdmissionHardBlockedReason;if(!string.IsNullOrWhiteSpace(admissionBlocked))throw new IOException(admissionBlocked);
            // A pressure latch only stops a conservative launch; practical/custom launches stay advisory and are re-authorized by the monitor.
            if(status?.AdmissionPressure==true&&LaunchesConservatively)throw new IOException(PressureMessage);
            var admission=DecideAdmission(draft,status?.Memory??throw new IOException("资源不可用"),status?.Entries,includeGitPreparation:true,budgets:status?.Budgets);if(admission.Kind==AdmissionKind.Deny)throw new IOException(admission.Reason);
            ProgressText.Text="正在核对命令和工作目录…";
            // Active agent workspaces are the monitor's own reserve precondition; checking them here keeps an occupied destination from leaving a worktree and branch behind.
            var activeWorkspaces=status?.Entries?.Where(x=>!Store.IsTerminal(x)&&x.Plan.Kind=="agent").Select(x=>x.Plan.Workspace).ToArray()??[];
            var plans=await Task.Run(()=>new WorkspacePreparer(config.WorktreeDirectory).PrepareAsync(draft,text=>Dispatcher.Invoke(()=>ProgressText.Text=text),text=>Dispatcher.InvokeAsync(()=>Task.FromResult(MessageBox.Show(this,text,"独立目录基线",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)).Task.Unwrap(),launchCancellation.Token,activeWorkspaces,configuration:config));
            launchCancellation.Token.ThrowIfCancellationRequested();
            var planned=plans.ToArray();
            await AuthorizePlansAsync(planned);
            var launchBindings=ReservationBindings(planned);
            var reserve=await Ipc.ReserveWithCleanupAsync(planned,launchCancellation.Token,UnlockForCleanup,launchBindings.Policy,launchBindings.Revision,launchBindings.Hash,launchBindings.Digest,launchBindings.Slots);if(!reserve.Ok)throw new IOException(reserve.Message);
            submittedRevision=draft.Revision;submittedAttempts=plans.Select(p=>p.Id).ToArray();submissionGeneration++;
            if(launchCancellation.Token.IsCancellationRequested)return;
            var profileHint="";
            try{var paneProfile=TerminalProfile.TryPrepare(out var profileDeferred);await Task.Run(()=>Commands.SubmitTerminal(terminal,plans,draft.Layout,launchCancellation.Token,paneProfile));if(profileDeferred)profileHint="（窗格美化将在 Windows Terminal 重启后生效）";}
            catch{ProgressText.Text="Terminal 提交结果未确认。请核对记录或撤销未领取项，不要重复提交。";throw;}
            if(launchCancellation.Token.IsCancellationRequested)return;
            ProgressText.Text="已交给 Windows Terminal 打开，正在等每个窗口确认。进程出现不代表 Agent 已就绪。"+profileHint;
            for(var i=0;i<10;i++)
            {
                if(launchCancellation.Token.IsCancellationRequested)break;
                await Task.Delay(1000);await RefreshAsync();if(plans.All(p=>status?.Entries?.Any(x=>x.Plan.Id==p.Id&&x.Detail.StartsWith("已启动进程"))==true)){if(config.MinimizeAfterLaunch)WindowState=WindowState.Minimized;break;}
            }
        }
        finally{await FinishLaunchAsync();}
    }
    private async void CancelClick(object sender,RoutedEventArgs e){await GuardAsync(CancelAsync);UpdateLaunch();}
    private void SelectProject(string path){var changed=!StringComparer.OrdinalIgnoreCase.Equals(config.Draft.Project,path);if(!Edit(d=>d.Project=path))return;if(changed&&undoRemovals.Count>0){undoRemovals.Clear();RenderSessionsNow();}if(path.Length>0){config.RecentProjects.RemoveAll(p=>StringComparer.OrdinalIgnoreCase.Equals(p,path));config.RecentProjects.Insert(0,path);config.RecentProjects=config.RecentProjects.Take(8).ToList();RenderRecentProjects();QueueSave();}}
    private async void ProjectLostFocus(object sender,RoutedEventArgs e)=>await GuardAsync(ApplyProjectInputAsync);
    private async void ProjectKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter||Keyboard.Modifiers!=ModifierKeys.None)return;
        e.Handled=true;await GuardAsync(ApplyProjectInputAsync);
    }
    private async Task ApplyProjectInputAsync()
    {
        if(loading||rendering||busy||readOnly||closeRequested)return;
        if(ProjectPath.Text.Trim()!=config.Draft.Project)SelectProject(ProjectPath.Text.Trim());
        await CheckCommandsAsync();
    }
    private void ChooseClick(object sender,RoutedEventArgs e){if(busy||readOnly)return;var picker=new OpenFolderDialog{Title="选择项目文件夹"};if(picker.ShowDialog(this)==true)SelectProject(picker.FolderName);}
    private async void RecentChanged(object sender,SelectionChangedEventArgs e){if(!rendering&&Recent.SelectedItem is string path)await (recentProjectTask=GuardAsync(()=>SwitchRecentProjectAsync(path)));}
    private void AddClick(object sender,RoutedEventArgs e)=>AddAgentSlot();
    private void LayoutChanged(object sender,SelectionChangedEventArgs e){if(Layout.SelectedItem is ComboBoxItem {Tag:string value})Edit(d=>d.Layout=value);}
    private void DirectoryChanged(object sender,SelectionChangedEventArgs e){if(DirectoryMode.SelectedItem is ComboBoxItem {Tag:string value})Edit(d=>d.DirectoryMode=value);}
    private void TemplateChanged(object sender,SelectionChangedEventArgs e)
    {
        if(rendering||busy||readOnly||Templates.SelectedItem is not ComboBoxItem{Tag:{} selection})return;
        try{config.Restore(selection is TemplateAction.LastEdited?config.LastEdited??throw new InvalidOperationException("尚无上次组合"):config.Templates[(string)selection]);QueueSave();validationError="";ErrorText.Text="";undoRemovals.Clear();Render();RenderSessionsNow();}catch(Exception ex)when(ex is not OutOfMemoryException){ErrorText.Text=ex.Message;}
    }
    private void SaveTemplateClick(object sender,RoutedEventArgs e)
    {
        var dialog=new FormWindow(this,"保存完整组合",[("name","组合名称","",false),("binding","项目目录","bound",false)],"保存勾选项、程序/参数、目录策略与布局，不覆盖同名组合。通用组合不保存路径；原本单独指定的目录需要在新项目下重新选择。",choices:new(){["binding"]=[("bound","绑定当前项目与目录"),("portable","通用组合：使用时继承新项目")]});
        if(dialog.ShowDialog()==true){try{config.SaveTemplate(dialog.Values["name"],portable:dialog.Values["binding"]=="portable");QueueSave();Render();}catch(ArgumentException ex){ErrorText.Text=ex.Message;}}
    }
    private async void EditSlot(Guid id)=>await GuardAsync(()=>EditSlotAsync(id));
#if INTEGRATION_TESTS
    private Func<CancellationToken,Task<AgentCapability>>? detailProbeForTest;
#endif
    private async Task EditSlotAsync(Guid id)
    {
        if(editingSlot||!CanEditSlot(id))return;
        editingSlot=true;
        try
        {
            var slot=config.Draft.Slots.Single(s=>s.Id==id);var revision=config.Draft.Revision;var profile=slot.Current;
            using var probeCancellation=new CancellationTokenSource();
            IReadOnlyList<ArgumentPreset> presets=[];var probedPath="";var probing=false;Task probeTask=Task.CompletedTask;
            var discovered=(slot.AgentId=="custom"?discoveredInstalls.Values.SelectMany(paths=>paths):discoveredInstalls.GetValueOrDefault(slot.AgentId)??[]).Concat(config.Draft.Slots.Select(s=>s.Profiles.GetValueOrDefault(slot.AgentId)?.Executable??"")).Append(profile.Executable).Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var fields=new List<(string Key,string Label,string Value,bool Multiline)>();
            fields.Add(("name","分工名称（留空使用 Agent 名称）",slot.CustomName??"",false));
            fields.AddRange([("exe","程序路径（留空自动查找）",profile.Executable,false),("args","启动参数","",false),("cwd","覆盖工作目录（留空继承项目）",slot.Directory,false),("note","分工备注（不会自动发给 Agent）",slot.Note,true)]);
            
            // 远程执行字段（可选）
            var remoteHosts=config.RemoteHosts;
            if(remoteHosts.Count>0)
            {
                fields.Add(("remoteHost","远程主机（留空本地执行）",slot.RemoteHostId?.ToString()??"",false));
                fields.Add(("remoteDir","远程工作目录（如 /home/agent/work）",slot.RemoteDirectory??"",false));
            }
            // B2 VPS 槽位详情也显示一行远端资源（只读展示，保存时忽略该字段；30 秒节流共享主机卡片缓存）。
            var detailHost=slot.RemoteHostId is {} detailHostId?remoteHosts.FirstOrDefault(h=>h.Id==detailHostId):null;
            if(detailHost!=null)fields.Add(("remoteResources","远端资源（只读，自动刷新）",remoteResourceCache.TryGetValue(detailHost.Id,out var cachedResources)&&cachedResources.Sample.IsCompletedSuccessfully?RemoteResourceText(cachedResources.Sample.Result):"远端资源：读取中…",false));
            
            var actions=new Dictionary<string,Action<Window>>();
            actions["重新检查版本"]=window=>probeTask=ProbeAsync((FormWindow)window);
            actions["套用预设…"]=window=>{var form=(FormWindow)window;if(form.ReadValue("exe").Trim()!=probedPath){form.SetNote("程序路径已改变，请先重新检查版本。");return;}if(presets.Count>0)ApplyPreset(window,presets);};
            var choices=new Dictionary<string,(string Id,string Label)[]>();
            if(remoteHosts.Count>0)
            {
                choices["remoteHost"]=[("", "本地执行"),..remoteHosts.Select(h=>(h.Id.ToString(),h.Name))];
            }
            var dialog=new FormWindow(this,$"{Agents.Get(slot.AgentId).Name} · 参数与目录",fields.ToArray(),"可以直接编辑和保存。正在后台检查版本，取消不修改配置。",suggestions:new(){["exe"]=[..discovered]},argumentLists:new(){["args"]=profile.Arguments},actions:actions,choices:choices);
            dialog.SetActionEnabled("套用预设…",false);
            dialog.Loaded+=(_,_)=>probeTask=ProbeAsync(dialog);
            dialog.Closed+=(_,_)=>probeCancellation.Cancel();
            if(detailHost!=null)_=FillRemoteResourcesAsync(dialog,detailHost);
            if(dialog.ShowDialog()==true){if(revision!=config.Draft.Revision){ErrorText.Text="配置已改变，请重新打开编辑。";return;}Edit(d=>{var target=d.Slots.Single(s=>s.Id==id);target.Profiles[target.AgentId]=new(){Executable=dialog.Values["exe"].Trim(),Arguments=dialog.ArgumentValues["args"]};target.CustomName=string.IsNullOrWhiteSpace(dialog.Values["name"])?null:dialog.Values["name"].Trim();target.Directory=dialog.Values["cwd"].Trim();if(dialog.Values.GetValueOrDefault("inheritDirectory")=="true")target.RequiresDirectory=false;target.Note=dialog.Values["note"];
                // 远程执行字段
                if(remoteHosts.Count>0)
                {
                    var remoteHostValue=dialog.Values.GetValueOrDefault("remoteHost")?.Trim();
                    target.RemoteHostId=string.IsNullOrWhiteSpace(remoteHostValue)?null:Guid.Parse(remoteHostValue);
                    target.RemoteDirectory=dialog.Values.GetValueOrDefault("remoteDir")?.Trim();
                }
            });}
            await probeTask;
            async Task ProbeAsync(FormWindow form)
            {
                if(probing||probeCancellation.IsCancellationRequested)return;probing=true;form.SetActionEnabled("重新检查版本",false);form.SetActionEnabled("套用预设…",false);
                var executable=form.ReadValue("exe").Trim();var directory=form.ReadValue("cwd").Trim();if(directory.Length==0)directory=string.IsNullOrWhiteSpace(config.Draft.Project)?Store.Root:config.Draft.Project;
                try
                {
                    var capability=await Probe();
                    Task<AgentCapability> Probe()
                    {
#if INTEGRATION_TESTS
                        if(detailProbeForTest is{} injected)return injected(probeCancellation.Token);
#endif
                        return Task.Run(()=>AgentProbe.ProbeCapabilityAsync(slot.AgentId,new AgentProfile{Executable=executable},directory,TimeSpan.FromSeconds(8),probeCancellation.Token));
                    }
                    if(probeCancellation.IsCancellationRequested)return;
                    if(form.ReadValue("exe").Trim()!=executable){form.SetNote("程序路径已改变，请重新检查版本；仍可直接保存。");return;}
                    probedPath=executable;presets=capability.Presets;form.SetNote($"{Agents.Get(slot.AgentId).Name} {capability.Version??"版本未知"} · {capability.Note}\n取消不修改配置。");form.SetActionEnabled("套用预设…",presets.Count>0);
                }
                catch(OperationCanceledException)when(probeCancellation.IsCancellationRequested){}
                catch(Exception ex)when(ex is not OutOfMemoryException){if(!probeCancellation.IsCancellationRequested)form.SetNote("版本检查未完成："+ex.Message+"。仍可编辑和保存。");}
                finally{probing=false;if(!probeCancellation.IsCancellationRequested)form.SetActionEnabled("重新检查版本",true);}
            }
            async Task FillRemoteResourcesAsync(FormWindow form,RemoteHost resourceHost)
            {
                var text=RemoteResourceText(await SampleRemoteResourcesThrottledAsync(resourceHost));
                if(!probeCancellation.IsCancellationRequested)form.SetValue("remoteResources",text);
            }
        }
        finally{editingSlot=false;}
    }
    private static void ApplyPreset(Window owner,IReadOnlyList<ArgumentPreset> presets)
    {
        var pick=new FormWindow(owner,"参数预设",[("preset","预设",presets[0].Id,false)],"预设是一组完整参数，仅对已验证版本提供；套用后仍可逐项修改或全部替换。取消不改变参数。",choices:new(){["preset"]=[..presets.Select(preset=>(preset.Id,$"{preset.Name} · {preset.Preview}"))]});
        if(pick.ShowDialog()!=true)return;
        var chosen=presets.FirstOrDefault(preset=>preset.Id==pick.Values["preset"]);if(chosen is null)return;
        string? value=null;
        if(chosen.NeedsValue)
        {
            var ask=new FormWindow(owner,"填写预设值",[("value",chosen.ValueLabel!,"",false)],"该值作为单个参数精确传递，不添加 shell 引号。");
            if(ask.ShowDialog()!=true)return;value=ask.Values["value"];
        }
        try{(owner as FormWindow)?.SetArguments("args",chosen.Fill(value).Arguments);}
        catch(InvalidDataException e){MessageBox.Show(owner,e.Message,"参数预设",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private async void ResourceClick(object sender,RoutedEventArgs e)=>await GuardAsync(ShowResourceSettingsAsync);
    private FormWindow CreateSettingsDialog()=>new(this,"设置",[("terminal","Windows Terminal 路径（留空自动查找）",config.TerminalPath,false),("worktree","独立目录存放位置（留空使用默认位置）",config.WorktreeDirectory,false),("theme","主题",config.Theme,false),("scale","文字与控件大小",config.TextScalePercent.ToString(),false),("minimize","启动全部确认后最小化窗口",config.MinimizeAfterLaunch.ToString().ToLowerInvariant(),false),("notify","会话状态变化时显示桌面通知",(config.NotifyOnSessionEvents??true).ToString().ToLowerInvariant(),false)],$"配置位置：{configPath}\n更改独立目录位置只影响新准备，不移动或删除已有目录。当前为技术预览版，性能门禁尚未全部验证。",new(){["theme"]=[("system","跟随系统"),("light","浅色"),("dark","深色")],["scale"]=[("100","100% · 标准"),("125","125% · 较大"),("150","150% · 最大")]},["minimize","notify"],suggestions:new(){["terminal"]=[..discoveredTerminals.Append(config.TerminalPath).Where(p=>!string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)]},actions:new(){["诊断与导出"]=ShowDiagnostics,["管理最近项目"]=ManageRecentProjects,["管理组合"]=ManageTemplates,["管理远程主机"]=ManageRemoteHosts});
    private async void SettingsClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        if(applyingSettings)return;applyingSettings=true;
        try
        {
            var dialog=CreateSettingsDialog();if(dialog.ShowDialog()!=true)return;if(readOnly||busy){ErrorText.Text="当前配置不可编辑，设置未保存。";return;}
            if(!new[]{"system","light","dark"}.Contains(dialog.Values["theme"])||!bool.TryParse(dialog.Values["minimize"],out var minimize)||!bool.TryParse(dialog.Values["notify"],out var notify)||!int.TryParse(dialog.Values["scale"],out var scale)||scale is not (100 or 125 or 150)){ErrorText.Text="设置值无效，未应用。";return;}
            var destination=dialog.Values["worktree"].Trim();if(destination.Length>0)destination=await Task.Run(()=>PhysicalDirectory.Resolve(destination).Path);
            if(readOnly||busy)return;config.WorktreeDirectory=destination;config.TerminalPath=dialog.Values["terminal"].Trim();config.Theme=dialog.Values["theme"];config.TextScalePercent=scale;config.MinimizeAfterLaunch=minimize;config.NotifyOnSessionEvents=notify;QueueSave();ApplyTheme();Render();
        }
        finally{applyingSettings=false;}
    });
    private void RailOverflowMenuOpened(object sender,RoutedEventArgs e)=>RailOverflowRestore.IsEnabled=readOnly;
    private async void RestoreBackupClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        var recovered=await Task.Run(()=>Store.Read<Configuration>(configPath+".bak"))??throw new IOException("没有可恢复备份。");
        if(recovered.Draft==null||recovered.Templates==null||recovered.Templates.Values.Any(t=>t==null)||recovered.RecentProjects==null||recovered.RecentAgents==null||recovered.TerminalPath==null)throw new IOException("备份版本或结构不支持，未恢复。");try{recovered=ConfigurationCompatibility.Accept(recovered);}catch(InvalidDataException ex){throw new IOException("备份版本或结构不支持，未恢复。",ex);}
#if INTEGRATION_TESTS
        var confirmed=TestConfirm!=null?TestConfirm("将用上一个有效备份恢复配置。当前损坏文件会另存保留。","恢复配置",MessageBoxButton.OKCancel):MessageBox.Show(this,"将用上一个有效备份恢复配置。当前损坏文件会另存保留。", "恢复配置",MessageBoxButton.OKCancel);
        if(confirmed!=MessageBoxResult.OK)return;
#else
        if(MessageBox.Show(this,"将用上一个有效备份恢复配置。当前损坏文件会另存保留。", "恢复配置",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
#endif
        await Task.Run(()=>{if(File.Exists(configPath))File.Copy(configPath,configPath+".corrupt-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));Store.Save(configPath,recovered);});
        config=recovered;readOnly=false;RestoreBackupButton.Visibility=Visibility.Collapsed;ErrorText.Text="";SaveStatus.Text="已从备份恢复";ApplyTheme();Render();
    });
    
    private void RemoteHostsClick(object sender,RoutedEventArgs e)
    {
        if(busy||readOnly){ErrorText.Text="当前配置不可编辑，未打开远程主机管理。";return;}
        ManageRemoteHosts(this);
    }
    
    // 远程主机管理：添加/编辑/删除 VPS 配置（immutable snapshot 模式，确认后才 commit）
    private void ManageRemoteHosts(Window owner)
    {
        if(busy||readOnly){ErrorText.Text="当前配置不可编辑，未打开远程主机管理。";return;}
        // 创建 immutable snapshot（取消不修改原配置）
        var snapshot=config.RemoteHosts.ToList();
        var window=new Window{Owner=owner,Title="管理远程主机（VPS）",Width=Math.Min(720,SystemParameters.WorkArea.Width-32),Height=Math.Min(650,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        window.Style=(Style)Application.Current.Resources[typeof(Window)];
        var panel=new StackPanel{Margin=new(24)};
        // 同步模式用下拉选择而不是自由文本，避免拼写错误静默回退成另一种同步策略。
        var syncChoices=new Dictionary<string,(string Value,string Label)[]>
        {
            ["syncMode"]=[("mutagen","mutagen（双向同步，不是实时一致，也不是已收敛）"),("sshfs","sshfs（挂载读写，不是已收敛）"),("none","none（不同步，这次不是同权）")]
        };
        
        void Build()
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock{Text="远程主机是通过 SSH 访问的 VPS，上面装有 agent CLI。添加后，可在槽位配置中选择远程主机启动 agent。",Margin=new(0,0,0,16),TextWrapping=TextWrapping.Wrap});
            
            var addButton=new Button{Content="添加远程主机",HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,0,0,16)};
            AutomationProperties.SetName(addButton,"添加远程主机");
            addButton.Click+=(_,_)=>EditHost(null);
            panel.Children.Add(addButton);
            
            if(snapshot.Count==0)
            {
                panel.Children.Add(new TextBlock{Text="尚无远程主机配置。点击「添加远程主机」开始。",Foreground=(Brush)FindResource("Muted")});
            }
            else
            {
                foreach(var host in snapshot)
                {
                    var card=new Border{Style=(Style)FindResource("Card"),Padding=new(12),Margin=new(0,0,0,8)};
                    var stack=new StackPanel();
                    stack.Children.Add(new TextBlock{Text=host.Name,FontWeight=FontWeights.SemiBold});
                    stack.Children.Add(new TextBlock{Text=$"{host.SshUser}@{host.Address}:{host.SshPort} · agent 用户：{host.AgentUser}",FontSize=12,Foreground=(Brush)FindResource("Muted"),Margin=new(0,4,0,0)});
                    stack.Children.Add(new TextBlock{Text=$"可用 agent：{string.Join(", ",host.AvailableAgents)} · 同步：{RemoteHostEditor.SyncModeText(host.WorkspaceSyncMode)}",FontSize=12,Foreground=(Brush)FindResource("Muted"),Margin=new(0,4,0,0)});
                    if(!string.IsNullOrEmpty(host.SshKeyPath))stack.Children.Add(new TextBlock{Text=$"SSH 密钥：{host.SshKeyPath}",FontSize=12,Foreground=(Brush)FindResource("Muted"),Margin=new(0,4,0,0)});
                    
                    var buttons=new WrapPanel{Margin=new(0,8,0,0)};
                    var editButton=new Button{Content="编辑",Margin=new(0,0,8,0)};
                    AutomationProperties.SetName(editButton,"编辑远程主机 "+host.Name);
                    editButton.Click+=(_,_)=>EditHost(host);
                    var deleteButton=new Button{Content="删除"};
                    AutomationProperties.SetName(deleteButton,"删除远程主机 "+host.Name);
                    deleteButton.Click+=(_,_)=>DeleteRemoteHost(host.Id);
                    buttons.Children.Add(editButton);buttons.Children.Add(deleteButton);
                    stack.Children.Add(buttons);
                    // 远程维护：免密自检 / 检查版本 / 升级 Agent / 远端资源（短命令、有界超时，结果显示在卡片内）。
                    var remoteActions=CreateRemoteHostActions(window,host);
                    stack.Children.Add(remoteActions.Buttons);stack.Children.Add(remoteActions.Status);stack.Children.Add(remoteActions.Resources);
                    card.Child=stack;
                    panel.Children.Add(card);
                }
            }
            
            var buttons2=new DockPanel{Margin=new(0,16,0,0)};
            var applyButton=new Button{Content="应用",Width=80,Margin=new(0,0,8,0)};
            AutomationProperties.SetName(applyButton,"应用远程主机更改");
            applyButton.Click+=(_,_)=>{if(Commit())window.DialogResult=true;};
            var cancelButton=new Button{Content="取消",Width=80};
            AutomationProperties.SetName(cancelButton,"取消远程主机更改");
            cancelButton.Click+=(_,_)=>window.DialogResult=false;
            DockPanel.SetDock(applyButton,Dock.Right);
            DockPanel.SetDock(cancelButton,Dock.Right);
            buttons2.Children.Add(applyButton);
            buttons2.Children.Add(cancelButton);
            panel.Children.Add(buttons2);
        }
        
        void EditHost(RemoteHost? host)
        {
            var isNew=host is null;
            var fields=new (string Key,string Label,string Value,bool Multiline)[]
            {
                ("name","显示名称（如：京东云 VPS）",host?.Name??"",false),
                ("address","IP 或域名（如：100.85.114.65）",host?.Address??"",false),
                ("port","SSH 端口（默认 22）",host?.SshPort.ToString()??"22",false),
                ("sshUser","SSH 登录用户（如：root）",host?.SshUser??"",false),
                ("agentUser","agent 运行用户（如：agent）",host?.AgentUser??"",false),
                ("agents","可用 agent 类型（逗号分隔，如：pi,kimi,qodercli）",isNew?"pi":string.Join(",",host!.AvailableAgents),false),
                ("keyPath","SSH 私钥路径（可选，留空用 ssh-agent/SSH config，全程免密）",host?.SshKeyPath??"",false),
                ("syncMode","工作区同步模式",RemoteHostEditor.SyncModeText(host?.WorkspaceSyncMode??WorkspaceSyncMode.Mutagen),false),
                ("upgradeOverrides","Agent 升级命令覆盖（每行 agent=命令，留空用默认）",RemoteHostEditor.UpgradeOverridesText(host?.AgentUpgradeCommands),true)
            };
            var caption=isNew?"添加远程主机":"编辑远程主机";
            var dialog=new FormWindow(window,caption,fields,"修改后，使用此主机的槽位会应用新配置。\n同步在远程启动时生效。mutagen 使用 "+MutagenCreateMode.SyncMode+"：冲突时本机一侧会赢得修改，这不是已收敛，文件也不是实时一致。none 这次不是同权，仍可启动。云端终端通过 SSH 可以实时输入和看到输出。",choices:syncChoices);
            if(dialog.ShowDialog()!=true)return;
            var name=dialog.Values["name"].Trim();
            var address=dialog.Values["address"].Trim();
            var error=RemoteHostEditor.Validate(name,address,dialog.Values["port"],dialog.Values["agents"]);
            error??=RemoteHostEditor.Duplicate(snapshot,name,address,host?.Id);
            var overrides=RemoteHostEditor.ParseUpgradeOverrides(dialog.Values["upgradeOverrides"],out var overrideError);
            error??=overrideError;
            if(error is not null){MessageBox.Show(window,error,caption,MessageBoxButton.OK,MessageBoxImage.Warning);return;}
            var updated=new RemoteHost(
                Id:host?.Id??Guid.NewGuid(),
                Name:name,
                Address:address,
                SshPort:int.Parse(dialog.Values["port"]),
                SshUser:dialog.Values["sshUser"].Trim(),
                AgentUser:dialog.Values["agentUser"].Trim(),
                AvailableAgents:RemoteHostEditor.ParseAgents(dialog.Values["agents"]),
                SshKeyPath:string.IsNullOrWhiteSpace(dialog.Values["keyPath"])?null:dialog.Values["keyPath"].Trim(),
                WorkspaceSyncMode:RemoteHostEditor.ParseSyncMode(dialog.Values["syncMode"],host?.WorkspaceSyncMode??WorkspaceSyncMode.Mutagen),
                AgentUpgradeCommands:overrides);
            if(isNew)snapshot.Add(updated);
            else snapshot[snapshot.FindIndex(h=>h.Id==host!.Id)]=updated;
            Build();
        }
        
        void DeleteRemoteHost(Guid id)
        {
            var host=snapshot.FirstOrDefault(h=>h.Id==id);
            if(host==null)return;
            
            // 检查是否有槽位引用此主机（包括当前草稿和所有模板）
            var referencedSlots=config.Draft.Slots.Where(s=>s.RemoteHostId==id).Select(s=>s.DisplayName).ToArray();
            var referencedTemplates=config.Templates.Where(t=>t.Value.Slots.Any(s=>s.RemoteHostId==id)).Select(t=>t.Key).ToArray();
            
            if(referencedSlots.Length>0||referencedTemplates.Length>0)
            {
                var message="无法删除远程主机「"+host.Name+"」，因为以下配置正在使用：\n\n";
                if(referencedSlots.Length>0)message+="当前草稿槽位："+string.Join("、",referencedSlots)+"\n";
                if(referencedTemplates.Length>0)message+="模板："+string.Join("、",referencedTemplates)+"\n";
                message+="\n请先将这些槽位改为本地执行或选择其他主机。";
                MessageBox.Show(window,message,"删除远程主机",MessageBoxButton.OK,MessageBoxImage.Warning);
                return;
            }
            
#if INTEGRATION_TESTS
            var confirm=TestConfirm?.Invoke($"确定删除远程主机「{host.Name}」吗？","删除远程主机",MessageBoxButton.YesNo)??MessageBox.Show(window,$"确定删除远程主机「{host.Name}」吗？","删除远程主机",MessageBoxButton.YesNo,MessageBoxImage.Question);
#else
            var confirm=MessageBox.Show(window,$"确定删除远程主机「{host.Name}」吗？","删除远程主机",MessageBoxButton.YesNo,MessageBoxImage.Question);
#endif
            if(confirm!=MessageBoxResult.Yes)return;
            snapshot.RemoveAll(h=>h.Id==id);
            Build();
        }
        
        bool Commit()
        {
            // readOnly/busy 保护（与普通 Settings 保存一致）
            if(busy||readOnly){MessageBox.Show(window,"当前配置不可编辑，远程主机更改未保存。","管理远程主机",MessageBoxButton.OK,MessageBoxImage.Warning);return false;}
            config.RemoteHosts=snapshot;
            QueueSave();
            Render();
            return true;
        }
        
        Build();
        window.Content=new ScrollViewer{Content=panel};
        UiScale.Apply(window);
        window.ShowDialog();
    }
    
    
    private async void WorkspacesClick(object sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        var records=await Task.Run(()=>{
            var directory=Store.FilePath("workspaces");var rows=new List<(string Id,string Root,string Target,string Branch,string Oid,string State)>();
            if(!Directory.Exists(directory))return rows;
            foreach(var file in Directory.EnumerateFiles(directory,"*.json").Take(200))
            {
                using var json=Store.Read<JsonDocument>(file);if(json==null)continue;var r=json.RootElement;
                rows.Add((System.IO.Path.GetFileNameWithoutExtension(file),r.GetProperty("Root").GetString()!,r.GetProperty("Target").GetString()!,r.GetProperty("Branch").GetString()!,r.GetProperty("Oid").GetString()!,r.GetProperty("State").GetString()!));
            }
            return rows;
        });
        var window=new Window{Owner=this,Title="独立工作目录与人工整合",Width=Math.Min(720,SystemParameters.WorkArea.Width-32),Height=Math.Min(650,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};var panel=new StackPanel{Margin=new(24)};
        void Build()
        {
            panel.Children.Clear();
            panel.Children.Add(new TextBlock{Text="检查各目录修改 → 分别提交需要保留的修改 → 在集成分支逐个合并 → 运行项目测试。启动器不会自动合并；目录只在 git 确认干净且没有会话占用时，由你明确点击清理。",Margin=new(0,0,0,16)});
            if(records.Count==0)panel.Children.Add(new TextBlock{Text="尚无自动创建的工作目录。手工指定的目录显示在每项配置和启动记录中。"});
            foreach(var record in records)
            {
                panel.Children.Add(new TextBlock{Text=$"分支：{record.Branch}\n基线：{record.Oid}\n状态：{(record.State=="Prepared"?"准备完成":"准备中断或待核对，不会自动重做")}",Margin=new(0,8,0,0)});
                panel.Children.Add(new TextBox{Text=record.Target,IsReadOnly=true});var buttons=new WrapPanel();
                var open=new Button{Content="打开工作目录"};open.Click+=(_,_)=>{try{if(!Directory.Exists(record.Target))throw new IOException("目录尚未创建或已移动。");Process.Start(new ProcessStartInfo(record.Target){UseShellExecute=true})?.Dispose();}catch(Exception ex)when(ex is IOException or Win32Exception){MessageBox.Show(window,ex.Message);}};buttons.Children.Add(open);
                var branch=new Button{Content="复制分支"};branch.Click+=(_,_)=>{try{Clipboard.SetText(record.Branch);}catch(System.Runtime.InteropServices.COMException){MessageBox.Show(window,"剪贴板正被占用，请稍后再试。");}};buttons.Children.Add(branch);
                var origin=new Button{Content="打开主项目"};origin.Click+=(_,_)=>{try{if(Directory.Exists(record.Root))Process.Start(new ProcessStartInfo(record.Root){UseShellExecute=true})?.Dispose();}catch(Win32Exception ex){MessageBox.Show(window,ex.Message);}};buttons.Children.Add(origin);
                var inspect=new Button{Content="检查清理内容…"};inspect.Click+=async(_,_)=>await GuardAsync(()=>ShowCleanupInventoryAsync(window,record.Target));buttons.Children.Add(inspect);
                // Explicit cleanup (2026-11-13, vibe-kanban pattern, conservative variant): only when no live session
                // occupies the directory and git reports it completely clean. Plain `git worktree remove` (no --force)
                // is the second guard; the branch and all committed work always survive.
                var cleanup=new Button{Content="清理目录",Style=(Style)FindResource("QuietButton"),ToolTip="仅当分支及文件状态可核对、没有未提交/未跟踪/忽略内容且没有会话占用时可用；移除工作目录，保留分支"};
                cleanup.Click+=async(_,_)=>{
                    cleanup.IsEnabled=false;
                    try
                    {
                        // Mutual exclusion (advisory 2026-11-13): refuse while any launch/queue dispatch is in flight;
                        // the pump itself is blocked by workspaceCleanupActive for the whole dialog session.
                        if(busy||launchFinalizing||launchCancellation!=null||!cancelTask.IsCompleted||submittedAttempts.Length>0||queuePumping)
                            throw new IOException("有启动或队列执行正在进行，暂不清理；请等待其结束后再试。");
                        // Fresh, complete monitor status is required — the polled display cache may be stale or a preview.
                        // Any IPC failure (unreachable/slow monitor, cancelled read) means occupancy is UNKNOWN → refuse.
                        bool? occupied;
                        try{var current=await Ipc.EnsureAsync(new("status",Guid.NewGuid()));occupied=WorkspaceCleanupShell.OccupiedByLiveSession(current.Ok?current.Entries:null,record.Target);}
                        catch(Exception ex)when(ex is IOException or TimeoutException or OperationCanceledException){occupied=null;}
                        var git=Commands.FindExe("git.exe")??throw new IOException("未找到 Git，无法核对目录状态。");
                        string? porcelain=null;
                        if(Directory.Exists(record.Target))porcelain=await Task.Run(()=>WorkspaceCleanupShell.ReadStatusPorcelainAsync(git,record.Target,CancellationToken.None));
                        var decision=WorkspaceCleanup.Evaluate(occupied,porcelain);
                        if(!decision.Allowed){await ShowCleanupInventoryAsync(window,record.Target,decision.Reason);return;}
                        if(MessageBox.Show(window,decision.Reason+"\n\n确定继续？","清理工作目录",MessageBoxButton.OKCancel,MessageBoxImage.Question)!=MessageBoxResult.OK)return;
                        // Post-confirm re-verification: occupancy or tree contents may have changed during the confirm box.
                        try{var current=await Ipc.EnsureAsync(new("status",Guid.NewGuid()));occupied=WorkspaceCleanupShell.OccupiedByLiveSession(current.Ok?current.Entries:null,record.Target);}
                        catch(Exception ex)when(ex is IOException or TimeoutException or OperationCanceledException){occupied=null;}
                        porcelain=null;
                        if(Directory.Exists(record.Target))porcelain=await Task.Run(()=>WorkspaceCleanupShell.ReadStatusPorcelainAsync(git,record.Target,CancellationToken.None));
                        var recheck=WorkspaceCleanup.Evaluate(occupied,porcelain);
                        if(!recheck.Allowed){MessageBox.Show(window,"确认期间状态已变化，已取消清理："+recheck.Reason,"清理工作目录",MessageBoxButton.OK,MessageBoxImage.Information);return;}
                        await Task.Run(()=>WorkspaceCleanupShell.RemoveAsync(git,record.Root,record.Target,CancellationToken.None));
                        // The directory is already gone at this point: never let a record-delete failure abort the refresh,
                        // or the dialog would keep showing a stale entry pointing at a missing directory.
                        string? recordError=null;
                        await Task.Run(()=>{var file=Store.FilePath($"workspaces/{record.Id}.json");try{if(File.Exists(file))File.Delete(file);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){recordError=e.Message;}});
                        records.Remove(record);
                        Build();
                        if(recordError!=null)MessageBox.Show(window,"目录已移除，但记录文件删除失败："+recordError+"\n可到 "+Store.FilePath("workspaces")+" 手动删除该记录。","清理工作目录",MessageBoxButton.OK,MessageBoxImage.Warning);
                    }
                    catch(Exception ex)when(ex is IOException or TimeoutException or UnauthorizedAccessException or Win32Exception){MessageBox.Show(window,ex.Message,"清理工作目录");}
                    finally{cleanup.IsEnabled=true;}
                };
                buttons.Children.Add(cleanup);panel.Children.Add(buttons);
            }
        }
        Build();
        window.Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};UiScale.Apply(window);
        // Queue launches are blocked for the whole dialog session (advisory): cleanup must be atomic against dispatch.
        workspaceCleanupActive=true;
        try{window.ShowDialog();}
        finally{workspaceCleanupActive=false;}
    });
    private void ApplyTheme()
    {
        var dark=config.Theme=="dark";
        if(config.Theme=="system"){try{using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");dark=key?.GetValue("AppsUseLightTheme") is int value&&value==0;}catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){dark=false;}}
        var colors=dark?new[]{"#1C1F27","#14161C","#ECEFF5","#9AA3B2","#2A2F3A","#232733","#1E2A3F","#3A4150","#3D3120","#E8B45C"}:new[]{"#FFFFFF","#F4F6FB","#1F2430","#6E7686","#E2E7F0","#F0F3FA","#E7F0FE","#C0C8D8","#FFF5E5","#8A4B08"};
        var resources=Application.Current.Resources;var keys=new[]{"Surface","Page","Ink","Muted","Line","Raised","Selected","InputLine","WarningSurface","WarningInk"};for(var i=0;i<keys.Length;i++)resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        resources["Accent"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#60A5FA":"#3B82F6"));resources["AccentInk"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#0B1220":"#FFFFFF"));resources["SelectionInk"]=resources["Ink"];resources["ErrorInk"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#F87171":"#EF4444"));
        resources["AccentSoft"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#7CB8FC":"#60A5FA"));resources["AccentPressed"]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?"#3B82F6":"#2563EB"));
        foreach(var (key,color) in new[]{("PreviewPage","#191B20"),("PreviewSurface","#0F1116"),("PreviewLine","#34373F"),("PreviewInk","#D7DBE2"),("PreviewMuted","#A5ABB6")})resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        // Rail status tokens: the three dots and the rail surface keep their own light/dark pair so the rail reads as one layer below Page.
        foreach(var (key,darkColor,lightColor) in new[]{("StatusOk","#4ADE80","#22C55E"),("StatusOff","#6B7280","#9AA3B2"),("StatusBad","#F87171","#EF4444"),("RailSurface","#171A21","#EDF1F8")})resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?darkColor:lightColor));
        // Per-agent identity colors (variant C); dark uses brightened tints so badges stay legible on dark surfaces.
        foreach(var (key,darkColor,lightColor) in new[]{("AgentPi","#60A5FA","#3B82F6"),("AgentKimi","#A78BFA","#8B5CF6"),("AgentQoder","#2DD4BF","#14B8A6"),("AgentCodex","#4ADE80","#22C55E"),("AgentOmp","#FBBF24","#F59E0B")})resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?darkColor:lightColor));
        var shadow=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=14,ShadowDepth=2,Opacity=dark?0.35:0.10,Color=(Color)ColorConverter.ConvertFromString(dark?"#000000":"#1F2937")};shadow.Freeze();resources["CardShadow"]=shadow;
        if(SystemParameters.HighContrast)
        {
            foreach(var key in new[]{"Surface","Page","PreviewPage","PreviewSurface","WarningSurface","RailSurface"})resources[key]=SystemColors.WindowBrush;
            foreach(var key in new[]{"Ink","Muted","Line","InputLine","WarningInk","ErrorInk","PreviewInk","PreviewMuted","PreviewLine","StatusOk","StatusOff","StatusBad"})resources[key]=SystemColors.WindowTextBrush;
            resources["Raised"]=SystemColors.ControlBrush;resources["Selected"]=SystemColors.HighlightBrush;resources["Accent"]=SystemColors.HighlightBrush;resources["AccentSoft"]=SystemColors.HighlightBrush;resources["AccentPressed"]=SystemColors.HighlightBrush;resources["AccentInk"]=SystemColors.HighlightTextBrush;resources["SelectionInk"]=SystemColors.HighlightTextBrush;
        }
        SetResourceReference(BackgroundProperty,"Page");SetResourceReference(ForegroundProperty,"Ink");ErrorText.SetResourceReference(TextBlock.ForegroundProperty,"ErrorInk");LaunchHint.SetResourceReference(TextBlock.ForegroundProperty,LaunchHintState().Key);
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
        if(closeRequested)return;
        var closeNote=TaskUsability.CloseQueueNotice(queueEnabled?taskBook?.Queue.Count??0:0,busy);
        if(closeNote.Length>0)
        {
#if INTEGRATION_TESTS
            var answer=TestConfirm?.Invoke(closeNote,"关闭并暂停队列",MessageBoxButton.YesNo)??MessageBox.Show(this,closeNote,"关闭并暂停队列",MessageBoxButton.YesNo,MessageBoxImage.Question);
#else
            var answer=MessageBox.Show(this,closeNote,"关闭并暂停队列",MessageBoxButton.YesNo,MessageBoxImage.Question);
#endif
            if(answer!=MessageBoxResult.Yes)return;
        }
        closeRequested=true;var restartTimer=timer.IsEnabled;timer.Stop();var restartMemory=memoryTimer.IsEnabled;memoryTimer.Stop();IsEnabled=false;UpdateLaunch();
        try
        {
            queueEnabled=false;taskTimer.Stop();queueCancellation?.Cancel();
            launchCancellation?.Cancel();
            // Keep the Dispatcher alive until both launch compensation and an explicit stop have settled.
            await GuardAsync(()=>launchTask);await GuardAsync(()=>cancelTask);
            await GuardAsync(()=>quickQueueTask);await GuardAsync(()=>queuePumpTask);
            await commandCheckTask;await saveTask;
            if(saveError!=null&&MessageBox.Show(this,"配置尚未保存："+saveError+"\n仍要关闭？","保存失败",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            closing=true;await Dispatcher.InvokeAsync(Close,DispatcherPriority.Background);
        }
        finally
        {
            if(!closing){closeRequested=false;IsEnabled=true;if(restartTimer)timer.Start();if(restartMemory)memoryTimer.Start();RenderTasks();UpdateLaunch();}
        }
    }
}
