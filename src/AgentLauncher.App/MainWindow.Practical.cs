using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

// 实用三件套（参考 crystal / claude-squad 的多 Agent 管理实践，全部只读、additive）：
// F1 查看改动：按槽位核对本机工作目录的 git status（VPS 槽位即同步落回本机的文件，证明远端 agent 改了本机文件）。
// F2 内存看板：页脚实时显示本进程工作集/专用内存与系统可用内存，提示 VPS 会话降低本机内存占用。
// F3 同步状态：VPS 槽位的 Mutagen 健康徽标（就绪/同步中/有问题/未同步），判据与 WorkspaceSync.WaitReadyAsync 一致；
//    菜单「刷新同步状态」按需刷新，内存定时器顺带每 ~15 秒节流探测一次（单次 mutagen sync list 覆盖全部槽位）。
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
        var files=changes.Entries.Length==0?"工作区干净，没有未提交改动。":string.Join("\n",changes.Entries.Take(maxRows).Select(DescribeChange))+(changes.Entries.Length>maxRows?$"\n…（其余 {changes.Entries.Length-maxRows} 项未显示，请在终端运行 git status 查看）":"");
        var note=host!=null
            ?$"此项绑定 VPS「{host.Name}」。以下是本机工作目录的改动：Mutagen 双向同步把远端 agent 的修改落回这里，看到改动即证明远端修改了本机文件。只读核对，不修改任何内容。"
            :"只读核对本机工作目录的改动（git status --porcelain=v1 -b），不修改任何内容。";
        var dialog=new FormWindow(this,"查看改动 · "+slot.DisplayName,
            [("dir","本机工作目录",directory,false),("branch","分支（git status -b）",changes.Branch,false),("files","改动文件",files,true)],note);
        dialog.ShowDialog();
    }
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
        if(host.WorkspaceSyncMode==WorkspaceSyncMode.None)ApplySyncBadge(slot.Id,sync,new(SyncHealthState.NotSynced,"","该主机未启用工作区同步；远端 agent 的改动不会落回本机。"),cache:false);
        else if(host.WorkspaceSyncMode==WorkspaceSyncMode.SshfsMount)ApplySyncBadge(slot.Id,sync,new(SyncHealthState.Syncing,"","该主机使用 SSHFS 挂载同步（无 Mutagen 会话可查询）；文件经挂载点直接读写。"),cache:false);
        else ApplySyncBadge(slot.Id,sync,syncHealthCache.GetValueOrDefault(slot.Id)??new(SyncHealthState.NotSynced,"","尚未检查；点菜单「刷新同步状态」或等待自动刷新（约 15 秒一次）。"),cache:false);
        var panel=new StackPanel{Orientation=Orientation.Horizontal};
        panel.Children.Add(badge);panel.Children.Add(sync);
        return panel;
    }
    private void ApplySyncBadge(Guid slotId,TextBlock badge,SyncHealth health,bool cache=true)
    {
        badge.Text="同步："+health.Label;
        badge.SetResourceReference(TextBlock.ForegroundProperty,health.State switch{SyncHealthState.Ready=>"StatusOk",SyncHealthState.Problems=>"StatusBad",SyncHealthState.Syncing=>"Muted",_=>"StatusOff"});
        badge.ToolTip=health.Detail+(health.SessionName.Length>0?"\n会话："+health.SessionName:"");
        if(cache)syncHealthCache[slotId]=health;
    }
    internal async Task RefreshSlotSyncHealthAsync(Guid slotId)
    {
        var slot=config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId);
        var host=slot is null?null:RemoteHostForSlot(slot);
        if(slot==null||host==null){ErrorText.Text="此项未绑定远程主机（VPS），没有远程同步可刷新。";return;}
        if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen){ProgressText.Text=host.WorkspaceSyncMode==WorkspaceSyncMode.None?"该主机未启用工作区同步，远端改动不会落回本机。":"该主机使用 SSHFS 挂载同步，无 Mutagen 会话可查询。";return;}
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
                ApplySyncBadge(slot.Id,badge,health);
            }
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            foreach(var slot in targets)if(syncBadges.TryGetValue(slot.Id,out var badge))ApplySyncBadge(slot.Id,badge,new(SyncHealthState.NotSynced,"","同步状态探测失败："+e.Message));
        }
        finally{syncProbing=false;}
    }
}