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
// F3 同步状态：VPS 槽位的每链路七态徽章（安全暂停/冲突/未知/离线/未收敛/同步中/已收敛，规格 2026-11-18 §4）+ 上次观测时间；
//    30 秒新鲜度由 5s 内存 tick 重渲染缓存降级，list 仍 ~15 秒节流、单次只读 mutagen sync list --long 覆盖全部槽位；
//    菜单「刷新同步状态」按需刷新、「同步详情…」只读展示整体标签/逐链路/冲突清单（≤50 行 + 剩余计数）；
//    A1 终态回退：会话终止后按 attempts/<id>/sync-final.json 显示「已同步」/「同步有问题」（旧回退链保留）；
//    B3 名字持久化：按 attempts/<id>/sync-session.json 的会话名匹配 mutagen list（重启后恢复）；
//    None/SSHFS 徽章按最近尝试状态给拒收文案/挂载提示，永远不声称已收敛（R41）。
public partial class MainWindow
{
    private readonly DispatcherTimer memoryTimer=new(){Interval=TimeSpan.FromSeconds(5)};
    private bool memoryBoardStarted;
    private Process? memoryProcess;
    private DateTimeOffset lastSyncProbe=DateTimeOffset.MinValue;
    private bool syncProbing;
    private readonly Dictionary<Guid,TextBlock> syncBadges=[];
    private readonly Dictionary<Guid,SyncHealth> syncHealthCache=[];
    // 同步可靠性（规格 §4）：链路观测缓存。syncLinkObservedAt/syncLastState 在回退后仍保留，供「上次观测」与未知详情使用。
    private readonly Dictionary<Guid,SyncLinkObservation> syncLinkCache=[];
    private readonly Dictionary<Guid,ParsedMutagenSession> syncParsedCache=[];
    private readonly Dictionary<Guid,DateTimeOffset> syncLinkObservedAt=[];
    private readonly Dictionary<Guid,SyncLinkState> syncLastState=[];
    private sealed record SyncEndpoint(string Local,Guid HostId,string Address,string User,int Port,string Remote,WorkspaceSyncMode Mode);
    private readonly Dictionary<Guid,SyncEndpoint> syncEndpoints=[];

    private SyncEndpoint EndpointFor(Slot slot,RemoteHost host)=>new(SyncLocalDirectory(slot),host.Id,host.Address.Trim('[',']').ToUpperInvariant(),host.AgentUser,host.SshPort,(slot.RemoteDirectory??"").TrimEnd('/'),host.WorkspaceSyncMode);
    private bool IsCurrentEndpoint(Guid slotId,SyncEndpoint endpoint)=>config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId) is {} slot&&RemoteHostForSlot(slot) is {} host&&EndpointFor(slot,host)==endpoint;
    private SyncEndpoint BindSyncEndpoint(Slot slot,RemoteHost host)
    {
        var endpoint=EndpointFor(slot,host);
        if(syncEndpoints.GetValueOrDefault(slot.Id)!=endpoint)
        {
            syncHealthCache.Remove(slot.Id);syncLinkCache.Remove(slot.Id);syncParsedCache.Remove(slot.Id);
            syncLinkObservedAt.Remove(slot.Id);syncLastState.Remove(slot.Id);
            syncEndpoints[slot.Id]=endpoint;
        }
        return endpoint;
    }

    // ---- F2 本机内存看板 ----
    internal void StartMemoryBoard()
    {
        if(memoryBoardStarted)return;memoryBoardStarted=true;
        memoryTimer.Tick+=(_,_)=>{UpdateMemoryBoard();_=ProbeSyncHealthAsync();RefreshCachedSyncBadges();};
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
            ?$"此项绑定 VPS「{host.Name}」。以下是本机工作目录的改动：Mutagen 双向同步把远端 agent 的修改落回这里，看到改动即证明远端修改了本机文件。只读核对，不修改任何内容。"
            :"只读核对本机工作目录的改动（git status --porcelain=v1 -b），不修改任何内容。";
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
        BindSyncEndpoint(slot,host);
        var sync=new TextBlock{FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new(10,0,0,0),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis};
        AutomationProperties.SetName(sync,$"同步状态 {slot.DisplayName}");
        syncBadges[slot.Id]=sync;
        if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen)RefreshNonMutagenBadge(slot,host,sync);
        else if(syncLinkCache.TryGetValue(slot.Id,out var link))
        {
            var parsed=syncParsedCache.GetValueOrDefault(slot.Id);
            if(parsed!=null)link=syncLinkCache[slot.Id]=SyncReliability.EvaluateLink(parsed,syncLinkObservedAt[slot.Id],DateTimeOffset.UtcNow);
            ApplyLinkBadge(sync,link,parsed?.Name??"");
        }
        else ApplySyncBadge(slot.Id,sync,syncHealthCache.GetValueOrDefault(slot.Id)??new(SyncHealthState.NotSynced,"","尚未检查；点菜单「刷新同步状态」或等待自动刷新（约 15 秒一次）。"),cache:false);
        var panel=new StackPanel{Orientation=Orientation.Horizontal};
        panel.Children.Add(badge);panel.Children.Add(sync);
        return panel;
    }
    private void ApplySyncBadge(Guid slotId,TextBlock badge,SyncHealth health,bool cache=true)
    {
        badge.Text="同步："+health.Label;
        badge.SetResourceReference(TextBlock.ForegroundProperty,health.State switch{SyncHealthState.Ready or SyncHealthState.Synced=>"StatusOk",SyncHealthState.Problems or SyncHealthState.SyncProblem=>"StatusBad",SyncHealthState.Syncing=>"Muted",_=>"StatusOff"});
        badge.ToolTip=health.Detail+(health.SessionName.Length>0?"\n会话："+health.SessionName:"");
        if(cache)syncHealthCache[slotId]=health;
    }
    // None/SSHFS 徽章（§6/R41）：按最近尝试状态给挂载提示/拒收文案；永远不声称已收敛。
    private void RefreshNonMutagenBadge(Slot slot,RemoteHost host,TextBlock badge)
    {
        var entry=LatestMatchingSyncEntry(slot,host);
        var alive=entry?.State is "Started" or "Reattaching";
        var failed=entry?.State=="FailedBeforeStart";
        var (label,detail)=SyncReliability.NonMutagenSyncBadge(host.WorkspaceSyncMode,alive,failed);
        ApplyTextBadge(badge,label,detail,host.WorkspaceSyncMode==WorkspaceSyncMode.SshfsMount&&alive?"Muted":failed?"StatusBad":"StatusOff");
    }
    private static void ApplyTextBadge(TextBlock badge,string label,string detail,string brushKey)
    {
        badge.Text="同步："+label;
        badge.SetResourceReference(TextBlock.ForegroundProperty,brushKey);
        badge.ToolTip=detail;
    }
    private void ApplyLinkBadge(TextBlock badge,SyncLinkObservation observation,string sessionName)
    {
        var brush=observation.State switch{SyncLinkState.Converged=>"StatusOk",SyncLinkState.Conflict or SyncLinkState.SafetyPaused or SyncLinkState.Offline=>"StatusBad",SyncLinkState.Syncing=>"Muted",_=>"StatusOff"};
        var observed=observation.LastObservedAt is {} at?"\n上次观测："+at.ToLocalTime().ToString("HH:mm:ss"):"\n上次观测：尚无";
        ApplyTextBadge(badge,observation.Label,observation.Detail+observed+(sessionName.Length>0?"\n会话："+sessionName:""),brush);
    }
    // 每次内存 tick 用缓存重算新鲜度：超过 30 秒的观测自动降为未知，不发新 list、不占名额。
    private void RefreshCachedSyncBadges()
    {
        foreach(var slot in config.Draft.Slots)
        {
            var host=RemoteHostForSlot(slot);if(host==null)continue;
            BindSyncEndpoint(slot,host);
            if(!syncParsedCache.TryGetValue(slot.Id,out var parsed))continue;
            var observation=SyncReliability.EvaluateLink(parsed,syncLinkObservedAt[slot.Id],DateTimeOffset.UtcNow);
            syncLinkCache[slot.Id]=observation;
            if(syncBadges.TryGetValue(slot.Id,out var badge))ApplyLinkBadge(badge,observation,parsed.Name);
        }
    }
    internal async Task RefreshSlotSyncHealthAsync(Guid slotId)
    {
        var slot=config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId);
        var host=slot is null?null:RemoteHostForSlot(slot);
        if(slot==null||host==null){ErrorText.Text="此项未绑定远程主机（VPS），没有远程同步可刷新。";return;}
        if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen)
        {
            if(syncBadges.TryGetValue(slot.Id,out var nonMutagen))RefreshNonMutagenBadge(slot,host,nonMutagen);
            ProgressText.Text=host.WorkspaceSyncMode==WorkspaceSyncMode.None?"该主机同步模式为 none：不构成三台同权，保存与启动已被拒收；远端改动不会落回本机。":"该主机使用 SSHFS 挂载同步，无 Mutagen 会话可查询；徽章显示挂载提示，不是已收敛。";
            return;
        }
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
            await ApplySyncListToBadgesAsync(ok?output:null,ok,error,DateTimeOffset.UtcNow);
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            await ApplySyncListToBadgesAsync(null,false,"同步状态探测失败："+e.Message,DateTimeOffset.UtcNow);
        }
        finally{syncProbing=false;}
    }
    // 同步可靠性徽章决策表（规格 §4）：list 成功且匹配到会话 → 七态评估；
    // 命名会话在输出里但无法匹配/解析 → fail-closed 未知；无会话 → A1/B3 旧回退链（终态 sync-final）；
    // list 失败 → 先走终态回退，否则未知（探针失败 ≠ 未同步）。文件读取离开 UI 线程。
    internal async Task ApplySyncListToBadgesAsync(string? output,bool ok,string error,DateTimeOffset observedAt)
    {
        var targets=config.Draft.Slots.Where(s=>s.RemoteHostId!=null&&RemoteHostForSlot(s) is {WorkspaceSyncMode:WorkspaceSyncMode.Mutagen}).ToArray();
        foreach(var slot in targets)
        {
            var host=RemoteHostForSlot(slot)!;
            var endpoint=BindSyncEndpoint(slot,host);
            var local=endpoint.Local;
            var entry=LatestMatchingSyncEntry(slot,host);
            var evidence=await Task.Run(()=>ReadAttemptSyncEvidence(entry));
            var persisted=evidence.Name;
            if(!IsCurrentEndpoint(slot.Id,endpoint))continue;
            var found=ok&&output is not null?WorkspaceInsights.FindSyncSession(output,local,host.Address,slot.RemoteDirectory??"",host.AgentUser,host.SshPort,persisted):null;
            if(found is {} f)
            {
                var observation=SyncReliability.EvaluateLink(f.Session,observedAt,DateTimeOffset.UtcNow);
                syncLinkCache[slot.Id]=observation;syncParsedCache[slot.Id]=f.Session;syncLinkObservedAt[slot.Id]=observedAt;
                if(DateTimeOffset.UtcNow-observedAt<=TimeSpan.FromSeconds(SyncReliability.FreshnessSeconds))syncLastState[slot.Id]=observation.State;
                if(syncBadges.TryGetValue(slot.Id,out var badge))ApplyLinkBadge(badge,observation,f.Name);
                continue;
            }
            syncLinkCache.Remove(slot.Id);syncParsedCache.Remove(slot.Id);
            if(ok&&output is not null&&persisted is not null&&output.Contains("Name: "+persisted,StringComparison.Ordinal))
            {
                var unknown=SyncReliability.FailedProbe("mutagen sync list 输出无法解析该会话。",LastSyncObservation(slot.Id),syncLastState.TryGetValue(slot.Id,out var last)?last:null,DateTimeOffset.UtcNow);
                syncLinkCache[slot.Id]=unknown;
                if(syncBadges.TryGetValue(slot.Id,out var badge))ApplyLinkBadge(badge,unknown,persisted);
                continue;
            }
            var health=WorkspaceInsights.EvaluateSyncBadge(new(SyncHealthState.NotSynced,"","未找到当前端点的同步会话。"),evidence.Final);
            if(health.State==SyncHealthState.NotSynced&&(!ok||string.IsNullOrWhiteSpace(output)||
                (!output.Contains("Name: ",StringComparison.Ordinal)&&!output.Contains("No sessions found",StringComparison.Ordinal))))
            {
                var unknown=SyncReliability.FailedProbe(ok?"mutagen sync list 输出为空或无法识别。":error,LastSyncObservation(slot.Id),syncLastState.TryGetValue(slot.Id,out var last)?last:null,DateTimeOffset.UtcNow);
                syncLinkCache[slot.Id]=unknown;
                if(syncBadges.TryGetValue(slot.Id,out var badge))ApplyLinkBadge(badge,unknown,persisted??"");
                continue;
            }
            syncHealthCache[slot.Id]=health;
            if(syncBadges.TryGetValue(slot.Id,out var terminalBadge))ApplySyncBadge(slot.Id,terminalBadge,health,cache:false);
        }
    }
    private string SyncLocalDirectory(Slot slot)=>NormalizeSyncDirectory(string.IsNullOrWhiteSpace(slot.Directory)?config.Draft.Project:slot.Directory);
    private static string NormalizeSyncDirectory(string path)
    {
        if(string.IsNullOrWhiteSpace(path))return "";
        try{return Path.GetFullPath(path).TrimEnd('\\','/').ToUpperInvariant();}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return "";}
    }
    private ControlEntry? LatestMatchingSyncEntry(Slot slot,RemoteHost host)
    {
        var entry=LatestRemoteEntryForSlot(slot.Id);
        if(entry==null||!MatchesSyncAttempt(slot,host,entry))return null;
        if(host.WorkspaceSyncMode==WorkspaceSyncMode.SshfsMount&&entry.Plan.RemoteExecution?.WorkspaceSync?.Mode!=WorkspaceSyncMode.SshfsMount)return null;
        return entry;
    }
    private bool MatchesSyncAttempt(Slot slot,RemoteHost host,ControlEntry entry)
    {
        var remote=entry.Plan.RemoteExecution;
        return remote!=null&&SyncLocalDirectory(slot).Length>0&&
            SyncLocalDirectory(slot)==NormalizeSyncDirectory(remote.WorkspaceSync?.LocalPath??entry.Plan.Cwd)&&
            string.Equals(remote.Host.Trim('[',']'),host.Address.Trim('[',']'),StringComparison.OrdinalIgnoreCase)&&
            remote.AgentUser==host.AgentUser&&remote.SshPort==host.SshPort&&
            remote.RemoteWorkingDirectory.TrimEnd('/')==(slot.RemoteDirectory??"").TrimEnd('/')&&
            (remote.WorkspaceSync==null||remote.WorkspaceSync.Mode==host.WorkspaceSyncMode);
    }
    private DateTimeOffset? LastSyncObservation(Guid slotId)=>syncLinkObservedAt.TryGetValue(slotId,out var at)?at:null;
    private static (string? Name,SyncFinalRecord? Final) ReadAttemptSyncEvidence(ControlEntry? entry)
    {
        if(entry==null)return(null,null);
        var session=WorkspaceInsights.ReadSyncEvidence<SyncSessionRecord>(Store.SyncSessionPath(entry.Plan.Id));
        var final=Store.IsTerminal(entry)?WorkspaceInsights.ReadSyncEvidence<SyncFinalRecord>(Store.SyncFinalPath(entry.Plan.Id)):null;
        return(session is {SchemaVersion:1}?session.Name:null,final is {SchemaVersion:1}?final:null);
    }
    // 同步详情（只读）：整体标签 + 逐链路状态/上次观测 + 冲突清单（≤50 行 + 剩余计数）。
    // 本轮不提供暂停/恢复、冲突处理或重置按钮（规格 §7/§10）；打开对话框不改变任何会话状态。
    internal void ShowSyncDetails(Guid slotId)
    {
        var selected=config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId);
        if(selected==null)return;
        var directory=SyncLocalDirectory(selected);
        var targets=config.Draft.Slots.Where(s=>s.Enabled&&s.RemoteHostId!=null&&
            (directory.Length>0?SyncLocalDirectory(s)==directory:s.Id==slotId))
            .Where(s=>RemoteHostForSlot(s)!=null).ToArray();
        var window=new Window{Owner=this,Title="同步详情",Width=Math.Min(760,SystemParameters.WorkArea.Width-32),Height=Math.Min(640,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)Application.Current.Resources[typeof(Window)]};
        var root=new DockPanel{Margin=new(24)};
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var close=new Button{Content="关闭",IsDefault=true,MinWidth=88};close.Click+=(_,_)=>window.DialogResult=true;footer.Children.Add(close);root.Children.Add(footer);
        var header=new StackPanel{Margin=new(0,0,0,12)};DockPanel.SetDock(header,Dock.Top);
        var overallText=new TextBlock{FontSize=18,FontWeight=FontWeights.Bold};AutomationProperties.SetName(overallText,"同步整体标签");
        header.Children.Add(overallText);
        header.Children.Add(new TextBlock{Text="只读诊断：展示最近一次 mutagen sync list --long 观测，超过 30 秒显示未知。此窗口不发起同步操作；刷新请用 Agent 菜单。", TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),Margin=new(0,8,0,0)});
        root.Children.Add(header);
        var grid=new Grid();
        grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        var linkList=new ListBox{HorizontalContentAlignment=HorizontalAlignment.Stretch};AutomationProperties.SetName(linkList,"同步链路列表");
        linkList.SetResourceReference(Control.BackgroundProperty,"Surface");linkList.SetResourceReference(Control.ForegroundProperty,"Ink");linkList.SetResourceReference(Control.BorderBrushProperty,"Line");
        ScrollViewer.SetHorizontalScrollBarVisibility(linkList,ScrollBarVisibility.Disabled);
        var conflictPanel=new DockPanel{Margin=new(0,12,0,0)};
        var remaining=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),Margin=new(0,0,0,8)};
        AutomationProperties.SetName(remaining,"同步冲突剩余计数");DockPanel.SetDock(remaining,Dock.Top);conflictPanel.Children.Add(remaining);
        var conflictList=new ListBox();AutomationProperties.SetName(conflictList,"同步冲突清单");
        conflictList.SetResourceReference(Control.BackgroundProperty,"Surface");conflictList.SetResourceReference(Control.ForegroundProperty,"Ink");conflictList.SetResourceReference(Control.BorderBrushProperty,"Line");
        conflictPanel.Children.Add(conflictList);
        Grid.SetRow(linkList,0);Grid.SetRow(conflictPanel,1);grid.Children.Add(linkList);grid.Children.Add(conflictPanel);
        root.Children.Add(grid);window.Content=root;UiScale.Apply(window);
        void RefreshDetails()
        {
            var links=new List<(SyncEndpoint Endpoint,string Label,string Role,SyncLinkObservation Observation,ParsedMutagenSession? Session)>();
            foreach(var slot in targets)
            {
                var host=RemoteHostForSlot(slot);if(host==null)continue;
                BindSyncEndpoint(slot,host);
                SyncLinkObservation observation;
                ParsedMutagenSession? parsed=null;
                if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen)
                {
                    var entry=LatestMatchingSyncEntry(slot,host);
                    var (text,detail)=SyncReliability.NonMutagenSyncBadge(host.WorkspaceSyncMode,entry?.State is "Started" or "Reattaching",entry?.State=="FailedBeforeStart");
                    observation=new(SyncLinkState.NotConverged,text+"："+detail,null);
                }
                else if(syncParsedCache.TryGetValue(slot.Id,out parsed))observation=SyncReliability.EvaluateLink(parsed,syncLinkObservedAt[slot.Id],DateTimeOffset.UtcNow);
                else observation=syncLinkCache.GetValueOrDefault(slot.Id)??new(SyncLinkState.Unknown,"尚未成功观测该链路；请刷新同步状态。",LastSyncObservation(slot.Id));
                links.Add((EndpointFor(slot,host) with{HostId=Guid.Empty},"本机—"+host.Name,host.Name,observation,parsed));
            }
            // 同一端点可有不同会话；先评估全部观测，再按七态优先级合并，不能先去重而丢掉故障。
            var endpointLinks=links.GroupBy(l=>l.Endpoint).Select(g=>g.OrderBy(l=>l.Observation.State).First()).ToArray();
            var overall=SyncReliability.OverallLabel(endpointLinks.Select(l=>l.Observation.State).ToArray());
            if(overall=="全部已收敛"&&endpointLinks.Select(l=>l.Endpoint.Address).Distinct().Count()<2)overall="未收敛";
            overallText.Text=overall;
            var conflicts=SyncReliability.BuildConflictList(links.Where(l=>l.Session!=null).DistinctBy(l=>(l.Endpoint,l.Session!.Name)).Select(l=>(l.Session!,l.Label,"本机",l.Role)).ToArray());
            var linkTexts=endpointLinks.Select(l=>$"{l.Label}：{l.Observation.Label} · 上次观测 {(l.Observation.LastObservedAt is {} at?at.ToLocalTime().ToString("HH:mm:ss"):"尚无")} · {l.Observation.Detail}").ToArray();
            if(!linkList.Items.OfType<ListBoxItem>().Select(i=>((TextBlock)i.Content).Text).SequenceEqual(linkTexts))
            {
                linkList.Items.Clear();
                foreach(var text in linkTexts)linkList.Items.Add(new ListBoxItem{HorizontalContentAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap}});
            }
            remaining.Text=$"冲突清单：显示 {conflicts.Rows.Length} 行，剩余 {conflicts.RemainingCount} 条未显示"+(conflicts.DiscardedCount>0?$"；另有 {conflicts.DiscardedCount} 个条目无法展示（路径不安全或输出只有计数）。":"。");
            var conflictTexts=conflicts.Rows.Select(r=>$"{r.RelativePath} · {r.LinkLabel} · {r.AlphaRole} ↔ {r.BetaRole}").ToArray();
            if(conflictTexts.Length==0)conflictTexts=["没有可展示的冲突。"];
            if(!conflictList.Items.OfType<ListBoxItem>().Select(i=>(string)i.Content).SequenceEqual(conflictTexts))
            {
                conflictList.Items.Clear();
                foreach(var text in conflictTexts)conflictList.Items.Add(new ListBoxItem{Content=text,IsEnabled=conflicts.Rows.Length>0});
            }
        }
        RefreshDetails();
        var refresh=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        refresh.Tick+=(_,_)=>RefreshDetails();refresh.Start();
        try{window.ShowDialog();}finally{refresh.Stop();}
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
