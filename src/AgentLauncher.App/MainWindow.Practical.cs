using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

// 实用三件套（参考 crystal / claude-squad 的多 Agent 管理实践，全部只读、additive）：
// F1 查看改动：按槽位核对本机工作目录的 git status（VPS 槽位即同步落回本机的文件，证明远端 agent 改了本机文件）；
//    B1 逐文件审阅：文件列表选中后显示只读 diff（单文件 400 行、本次查看累计 2000 行截断并提示）。
// F2 内存看板：页脚实时显示本进程工作集/专用内存与系统可用内存，提示 VPS 会话降低本机内存占用。
// F3 同步状态：VPS 槽位的 Mutagen 健康徽标（就绪/同步中/有问题/未同步），判据与 WorkspaceSync.WaitReadyAsync 一致；
//    菜单「刷新同步状态」按需刷新，内存定时器顺带每 ~15 秒节流探测一次（单次 mutagen sync list 覆盖全部槽位）。
//    A1 终态回退：会话终止后按 attempts/<id>/sync-final.json 显示「已同步」/「同步有问题」；
//    B3 名字持久化：无活 spec 匹配时按 attempts/<id>/sync-session.json 的会话名匹配 mutagen list（重启后恢复）。
public partial class MainWindow
{
    private readonly DispatcherTimer memoryTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    private bool memoryBoardStarted;
    private Process? memoryProcess;
    private DateTimeOffset lastSyncProbe=DateTimeOffset.MinValue;
    private bool syncProbing;
    private readonly Dictionary<Guid,TextBlock> syncBadges=[];
    private readonly Dictionary<Guid,SyncHealth> syncHealthCache=[];

    // ---- F2 本机内存看板 ----
    internal void StartMemoryBoard()
    {
        if(memoryBoardStarted)return;memoryBoardStarted=true;
        memoryTimer.Tick+=(_,_)=>{UpdateMemoryBoard();_=ProbeSyncHealthAsync();};
        Closed+=(_,_)=>{memoryTimer.Stop();memoryProcess?.Dispose();memoryProcess=null;};
        MemoryBoardText.ToolTip="VPS 会话在远端运行，降低本机内存占用。每 5 秒刷新：本程序=启动器进程工作集 / 专用内存；系统可用=本机空闲物理内存。";
        UpdateMemoryBoard();memoryTimer.Start();
    }
    private void UpdateMemoryBoard()
    {
        try
        {
            memoryProcess??=Process.GetCurrentProcess();memoryProcess.Refresh();
            var memory=new Native.MemoryStatus{Length=(uint)Marshal.SizeOf<Native.MemoryStatus>()};
            var available=Native.GlobalMemoryStatusEx(ref memory)?(long)(memory.AvailablePhysical/1024/1024):-1;
            MemoryBoardText.Text=$"内存：本程序 {memoryProcess.WorkingSet64/1024/1024} MB · 专用 {memoryProcess.PrivateMemorySize64/1024/1024} MB · 系统可用 {(available<0?"未知":available+" MB")}";
        }
        catch(Exception e)when(e is not OutOfMemoryException){MemoryBoardText.Text="内存：读取失败";}
    }

    // ---- F1 工作区改动查看 ----
    internal async Task ShowSlotChangesAsync(Guid slotId)
    {
        var slot=config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId);
        if(slot==null){ErrorText.Text="查看改动失败：该配置项已不存在，请重新打开菜单。";return;}
        var directory=string.IsNullOrWhiteSpace(slot.Directory)?config.Draft.Project:slot.Directory;
        if(string.IsNullOrWhiteSpace(directory)){ErrorText.Text="查看改动失败：此项尚未设置工作目录，请先选择项目或为此项指定工作目录。";return;}
        if(!Directory.Exists(directory)){ErrorText.Text="查看改动失败：工作目录不存在或尚未创建："+directory;return;}
        var host=RemoteHostForSlot(slot);
        var changes=await Task.Run(()=>WorkspaceInsights.ReadChanges(directory));
        if(!changes.Ok){ErrorText.Text="查看改动失败："+changes.Error;return;}
        const int maxRows=400;
        var note=host!=null
            ?$"此项绑定 VPS「{host.Name}」。{LaunchSurface.ChangesAreLocalGit}只读核对，不修改任何内容。"
            :LaunchSurface.ChangesAreLocalGit+"只读核对，不修改任何内容。";
        // B1 逐文件审阅：左侧文件列表，右侧只读等宽 diff。单文件 400 行（ReadDiffAsync）、本次查看累计 2000 行截断并提示。
        var window=new Window{Owner=this,Title="查看改动 · "+slot.DisplayName,Width=Math.Min(980,SystemParameters.WorkArea.Width-32),Height=Math.Min(700,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)Application.Current.Resources[typeof(Window)]};
        var root=new DockPanel{Margin=new(24)};
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var close=new Button{Content="关闭",IsCancel=true,IsDefault=true,MinWidth=88};footer.Children.Add(close);root.Children.Add(footer);
        var header=new StackPanel{Margin=new(0,0,0,12)};DockPanel.SetDock(header,Dock.Top);
        header.Children.Add(new TextBlock{Text=note,TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted")});
        header.Children.Add(new TextBlock{Text="本机工作目录："+directory+" · 分支（git status -b）："+changes.Branch,TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,0)});
        root.Children.Add(header);
        var grid=new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(300)});
        grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        var list=new ListBox{Margin=new(0,0,12,0)};AutomationProperties.SetName(list,"改动文件列表");
        var diffBox=new TextBox{IsReadOnly=true,FontFamily=new FontFamily("Consolas"),TextWrapping=TextWrapping.NoWrap,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Text=changes.Entries.Length==0?"工作区干净，没有未提交改动。":"选择左侧文件查看只读 diff（单文件 400 行、本次查看累计 2000 行，超出截断并提示）。"};
        AutomationProperties.SetName(diffBox,"文件 diff");
        foreach(var line in changes.Entries.Take(maxRows))list.Items.Add(new ListBoxItem{Content=DescribeChange(line),Tag=WorkspaceInsights.ChangePath(line)});
        if(changes.Entries.Length>maxRows)list.Items.Add(new ListBoxItem{Content=$"…（其余 {changes.Entries.Length-maxRows} 项未显示，请在终端运行 git status 查看）",IsEnabled=false});
        Grid.SetColumn(list,0);Grid.SetColumn(diffBox,1);grid.Children.Add(list);grid.Children.Add(diffBox);
        root.Children.Add(grid);window.Content=root;UiScale.Apply(window);
        var shownLines=0;const int totalCap=2000;var diffGeneration=0;
        list.SelectionChanged+=async(_,_)=>{
            if(list.SelectedItem is not ListBoxItem{Tag:string file}||file.Length==0)return;
            if(shownLines>=totalCap){diffBox.Text="本次查看累计已达 2000 行上限，不再加载更多 diff；请重新打开对话框或在终端运行 git diff 查看。";return;}
            var generation=++diffGeneration;diffBox.Text="正在读取 diff…";
            try
            {
                var diff=await WorkspaceInsights.ReadDiffAsync(directory,file);
                if(generation!=diffGeneration)return;  // 选择已切换，丢弃过期结果
                var text=diff.Ok?diff.Text:"读取 diff 失败："+diff.Error;
                var (bounded,capped)=WorkspaceInsights.TruncateLines(text,totalCap-shownLines);
                diffBox.Text=capped?bounded+"\n…（本次查看累计已达 2000 行上限，其余内容未显示；请在终端运行 git diff 查看）":bounded;
                shownLines+=CountLines(diffBox.Text);
            }
            catch(Exception e)when(e is not OutOfMemoryException){if(generation==diffGeneration)diffBox.Text="读取 diff 失败："+e.Message;}
        };
        window.ShowDialog();
    }
    private static int CountLines(string text){var lines=text.Replace("\r\n","\n").Split('\n');return lines.Length>0&&lines[^1].Length==0?lines.Length-1:lines.Length;}
    // porcelain v1 行：XY<空格>路径；X=暂存区状态，Y=工作区状态。
    private static string DescribeChange(string line)
    {
        if(line.Length<4)return line;
        var code=line[..2];var path=line[3..];
        if(code=="??")return "[新增·未跟踪] "+path;
        if(code is "AA" or "DD" or "UU"||code.Contains('U'))return "[冲突] "+path;
        if(code.Contains('D'))return "[删除] "+path;
        if(code.Contains('R'))return "[重命名] "+path;
        if(code.Contains('C'))return "[复制] "+path;
        if(code.Contains('A'))return "[新增] "+path;
        if(code.Contains('M'))return "[修改] "+path;
        return "[变更] "+path;
    }

    // ---- F3 远程同步状态 ----
    // AddSlot 的来源徽标挂点：本机槽位原样返回（结构不变）；VPS 槽位追加同步健康徽标。
    internal UIElement WrapSlotSource(Slot slot,TextBlock badge)
    {
        var host=RemoteHostForSlot(slot);
        if(host==null)return badge;
        var sync=new TextBlock{FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new(10,0,0,0),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};
        AutomationProperties.SetName(sync,$"同步状态 {slot.DisplayName}");
        syncBadges[slot.Id]=sync;
        if(host.WorkspaceSyncMode==WorkspaceSyncMode.None)ApplySyncBadge(slot.Id,sync,new(SyncHealthState.NotSynced,"",LaunchSurface.SyncHonesty(WorkspaceSyncMode.None)),cache:false);
        else if(host.WorkspaceSyncMode==WorkspaceSyncMode.SshfsMount)ApplySyncBadge(slot.Id,sync,new(SyncHealthState.Syncing,"",LaunchSurface.SyncHonesty(WorkspaceSyncMode.SshfsMount)),cache:false);
        else ApplySyncBadge(slot.Id,sync,syncHealthCache.GetValueOrDefault(slot.Id)??new(SyncHealthState.NotSynced,"",LaunchSurface.SyncHonesty(WorkspaceSyncMode.Mutagen)),cache:false);
        var honesty=new TextBlock{Text=LaunchSurface.SyncHonesty(host.WorkspaceSyncMode),FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0)};
        AutomationProperties.SetName(honesty,"同步说明 "+slot.DisplayName);
        var badges=new StackPanel{Orientation=Orientation.Horizontal};badges.Children.Add(badge);badges.Children.Add(sync);
        var panel=new StackPanel();panel.Children.Add(badges);panel.Children.Add(honesty);
        return panel;
    }
    private void ApplySyncBadge(Guid slotId,TextBlock badge,SyncHealth health,bool cache=true)
    {
        badge.Text="同步："+health.Label;
        badge.SetResourceReference(TextBlock.ForegroundProperty,health.State switch{SyncHealthState.Ready or SyncHealthState.Synced=>"StatusOk",SyncHealthState.Problems or SyncHealthState.SyncProblem=>"StatusBad",SyncHealthState.Syncing=>"Muted",_=>"StatusOff"});
        badge.ToolTip=health.Detail+(health.SessionName.Length>0?"\n会话："+health.SessionName:"");
        if(cache)syncHealthCache[slotId]=health;
    }
    internal async Task RefreshSlotSyncHealthAsync(Guid slotId)
    {
        var slot=config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId);
        var host=slot is null?null:RemoteHostForSlot(slot);
        if(slot==null||host==null){ErrorText.Text="此项未绑定远程主机（VPS），没有远程同步可刷新。";return;}
        if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen){ProgressText.Text=LaunchSurface.SyncHonesty(host.WorkspaceSyncMode);return;}
        await ProbeSyncHealthAsync(force:true);
        ProgressText.Text="同步状态已刷新（mutagen sync list，只读）。";
    }
    // 单次 mutagen sync list 覆盖全部 Mutagen VPS 槽位；force=true 跳过节流。失败只更新徽标，不弹错误横幅。
    private async Task ProbeSyncHealthAsync(bool force=false)
    {
        if(syncProbing||loading||closeRequested||closing)return;
        if(!force&&DateTimeOffset.UtcNow-lastSyncProbe<TimeSpan.FromSeconds(15))return;
        var targets=config.Draft.Slots.Where(s=>s.RemoteHostId!=null&&RemoteHostForSlot(s) is {WorkspaceSyncMode:WorkspaceSyncMode.Mutagen}).ToArray();
        lastSyncProbe=DateTimeOffset.UtcNow;
        if(targets.Length==0)return;
        syncProbing=true;
        try
        {
            var (ok,output,error)=await WorkspaceInsights.ListMutagenSessionsAsync(CancellationToken.None);
            foreach(var slot in targets)
            {
                if(!syncBadges.TryGetValue(slot.Id,out var badge))continue;
                var host=RemoteHostForSlot(slot)!;
                var local=string.IsNullOrWhiteSpace(slot.Directory)?config.Draft.Project:slot.Directory;
                var health=ok?WorkspaceInsights.EvaluateSync(output,local,host.Address,slot.RemoteDirectory??""):new SyncHealth(SyncHealthState.NotSynced,"",error+"；视为未同步。");
                // A1/B3：无活会话时先按持久化会话名匹配（重启后恢复），仍无匹配且条目终态则回退 sync-final；文件读取离开 UI 线程。
                if(health.State==SyncHealthState.NotSynced)health=await Task.Run(()=>ResolveTerminalSync(health,slot,host,local,ok?output:null));
                ApplySyncBadge(slot.Id,badge,health);
            }
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            foreach(var slot in targets)if(syncBadges.TryGetValue(slot.Id,out var badge))ApplySyncBadge(slot.Id,badge,new(SyncHealthState.NotSynced,"","同步状态探测失败："+e.Message));
        }
        finally{syncProbing=false;}
    }
    // A1/B3 回退链（只读）：持久化会话名匹配 mutagen list → 条目终态时回退 sync-final → 维持原「未同步」判定。
    // Disconnected 非终态不传 final，仍按活会话评估；mutagen list 失败（listOutput=null）时跳过名字匹配。
    private SyncHealth ResolveTerminalSync(SyncHealth live,Slot slot,RemoteHost host,string local,string? listOutput)
    {
        var entry=LatestRemoteEntryForSlot(slot.Id);
        if(entry is null)return live;
        if(listOutput is not null&&WorkspaceInsights.ReadSyncEvidence<SyncSessionRecord>(Store.SyncSessionPath(entry.Plan.Id)) is {SchemaVersion:1} session)
        {
            var named=WorkspaceInsights.EvaluateSync(listOutput,local,host.Address,slot.RemoteDirectory??"",session.Name);
            if(named.State!=SyncHealthState.NotSynced)return named;
        }
        var final=Store.IsTerminal(entry)?WorkspaceInsights.ReadSyncEvidence<SyncFinalRecord>(Store.SyncFinalPath(entry.Plan.Id)):null;
        return WorkspaceInsights.EvaluateSyncBadge(live,final is {SchemaVersion:1}?final:null);
    }
    // 槽位最近一次远程尝试：优先监视器快照；重启或账本归档后回退磁盘控制账本（只读，失败按无记录处理）。
    private ControlEntry? LatestRemoteEntryForSlot(Guid slotId)
    {
        bool Remote(ControlEntry e)=>e.Plan.SourceSlotId==slotId&&e.Plan.RemoteExecution is not null;
        var entries=status?.Entries;
        if(entries is null||!entries.Any(Remote))
        {
            try{entries=TaskRecoveryReader.ReadLedger()?.Entries.ToArray();}
            catch(Exception e)when(e is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException){}
        }
        return entries?.Where(Remote).OrderByDescending(e=>e.Created).FirstOrDefault();
    }
}