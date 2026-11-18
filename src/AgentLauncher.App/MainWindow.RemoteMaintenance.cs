using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private bool remoteMaintenanceRunning;
    // B2 每主机资源采样节流（≥30s，成功与失败都缓存，避免反复打远端）；缓存的是任务本身，并发读取共享同一次采样。
    private readonly Dictionary<Guid,(DateTimeOffset At,Task<RemoteResourceSample?> Sample)> remoteResourceCache=[];
    internal RemoteHost? RemoteHostForSlot(Slot slot)=>slot.RemoteHostId is {} id?config.RemoteHosts.FirstOrDefault(h=>h.Id==id):null;

    internal Task<RemoteResourceSample?> SampleRemoteResourcesThrottledAsync(RemoteHost host)
    {
        if(remoteResourceCache.TryGetValue(host.Id,out var cached)&&DateTimeOffset.UtcNow-cached.At<TimeSpan.FromSeconds(30))return cached.Sample;
        var task=SampleCoreAsync(host);remoteResourceCache[host.Id]=(DateTimeOffset.UtcNow,task);return task;
        static async Task<RemoteResourceSample?> SampleCoreAsync(RemoteHost h){try{return await SshRemoteCommand.SampleResourcesAsync(h);}catch(Exception e)when(e is not OutOfMemoryException){return null;}}
    }

    internal static string RemoteResourceText(RemoteResourceSample? sample)
    {
        if(sample is null)return "远端资源：读取失败";
        static string GiB(long bytes)=>(bytes/1073741824.0).ToString("0.0",CultureInfo.InvariantCulture);
        return $"远端：可用 {GiB(sample.AvailableBytes)} GiB / 共 {GiB(sample.TotalBytes)} GiB · {sample.Cores} 核 · 负载 {sample.Load15.ToString("0.00",CultureInfo.InvariantCulture)}";
    }

    // VPS 槽位行菜单入口：免密自检 / 检查版本 / 升级 Agent。
    // 全部是短生命周期 SSH 命令（非交互、重定向输出、有界超时）；失败报错不挂起，绝不提示密码。
    internal async Task RunSlotRemoteMaintenanceAsync(Guid slotId,string kind)
    {
        var slot=config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId);
        var host=slot is null?null:RemoteHostForSlot(slot);
        if(slot==null||host==null){ErrorText.Text="此项未配置远程主机（VPS），无法执行远程维护。";return;}
        if(remoteMaintenanceRunning){ProgressText.Text="已有远程维护操作正在进行，请等待完成。";return;}
        if(kind!="test"&&!RemoteWorkspaceMappingHelper.IsValidRemoteDirectory(slot.RemoteDirectory??"")){ErrorText.Text="请先在此项「详情」中填写有效的远程工作目录（绝对路径或 ~/ 开头），再执行远程维护。";return;}
        var agent=slot.AgentId;
        string? command=null;
        if(kind=="version")command=RemoteAgentMaintenance.VersionCommandFor(agent);
        if(kind=="upgrade"){command=PickUpgradeCommand(this,host,agent);if(command==null)return;}
        if(kind!="test"&&command==null){ErrorText.Text=$"Agent「{agent}」没有可用的{(kind=="upgrade"?"升级":"版本检查")}命令；可在远程主机配置中填写升级命令覆盖。";return;}
        if(kind=="upgrade"&&MessageBox.Show(this,$"将在「{host.Name}」上以 {host.AgentUser} 用户通过免密 SSH 执行：\n\n{command}\n\n继续？","升级 Agent",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        remoteMaintenanceRunning=true;
        try
        {
            ProgressText.Text=kind=="test"?$"正在测试「{host.Name}」免密连接…":$"正在「{host.Name}」上执行{(kind=="upgrade"?"升级":"版本检查")}…";
            string message;var ok=false;
            if(kind=="test"){(ok,message)=await SshRemoteCommand.TestConnectionAsync(host);}
            else(ok,message)=await RunVersionOrUpgradeAsync(host,agent,slot.RemoteDirectory!,kind=="upgrade",command);
            MessageBox.Show(this,message,kind=="test"?"远程免密连接":kind=="upgrade"?"升级 Agent":"检查远程版本",MessageBoxButton.OK,ok?MessageBoxImage.Information:MessageBoxImage.Warning);
        }
        finally{remoteMaintenanceRunning=false;ProgressText.Text="";}
    }

    // 检查版本/升级的统一执行入口：成功后把 --version 首行语义版本写入版本历史（C2 回滚推导的证据）。
    // 升级成功后再跑一次 --version 记录升级结果；历史写入失败只附加提示，不改变维护结果。
    internal static async Task<(bool Ok,string Message)> RunVersionOrUpgradeAsync(RemoteHost host,string agent,string dir,bool upgrade,string? command)
    {
        if(!upgrade)
        {
            var result=await SshRemoteCommand.CheckVersionAsync(host,agent,dir);
            var ok=!result.TimedOut&&result.ExitCode==0;
            var warning=ok?TryRecordVersion(host,agent,result.StdOut,RemoteAgentMaintenance.VersionCommandFor(agent)!):null;
            return(ok,AppendNote(FormatMaintenanceResult(host,"version",result),warning));
        }
        var upgradeResult=await SshRemoteCommand.UpgradeAgentAsync(host,agent,dir,command:command);
        var upgradeOk=!upgradeResult.TimedOut&&upgradeResult.ExitCode==0;
        var message=FormatMaintenanceResult(host,"upgrade",upgradeResult);
        if(upgradeOk)
        {
            try
            {
                var after=await SshRemoteCommand.CheckVersionAsync(host,agent,dir);
                if(!after.TimedOut&&after.ExitCode==0)message=AppendNote(message,TryRecordVersion(host,agent,after.StdOut,command??RemoteAgentMaintenance.UpgradeCommandFor(host,agent)!));
                else message=AppendNote(message,"升级后版本核对未成功，本次未写入版本历史。");
            }
            catch(Exception e)when(e is not OutOfMemoryException){message=AppendNote(message,"升级后版本核对失败："+e.Message);}
        }
        return(upgradeOk,message);
    }

    private static string? TryRecordVersion(RemoteHost host,string agent,string versionOutput,string command)
    {
        try{RemoteVersionHistory.RecordFromVersionOutput(RemoteVersionHistory.DefaultPath,host,agent,versionOutput,command);return null;}
        catch(Exception e)when(e is IOException or InvalidDataException or UnauthorizedAccessException){return "版本历史写入失败："+e.Message;}
    }

    private static string AppendNote(string message,string? note)=>note is null?message:message+"\n"+note;

    // C2 升级方式选择：最新 / 回滚上一版（历史中上一个不同版本）/ 指定版本。
    // 覆盖命令不含 @latest/pip upgrade 形态时无法推导固定版本命令，只按覆盖原样执行；无历史时不提供回滚选项。
    // 返回 null 表示取消或无命令（无命令时已提示）；调用方随后仍显示确切命令并二次确认。
    internal static string? PickUpgradeCommand(Window owner,RemoteHost host,string agent)
    {
        var baseCommand=RemoteAgentMaintenance.UpgradeCommandFor(host,agent);
        if(baseCommand==null){MessageBox.Show(owner,$"Agent「{agent}」没有可用的升级命令；请在主机编辑中填写升级命令覆盖。","升级 Agent",MessageBoxButton.OK,MessageBoxImage.Warning);return null;}
        if(!RemoteAgentMaintenance.SupportsVersionPinning(baseCommand))return baseCommand;
        var previous=RemoteAgentMaintenance.PreviousVersion(RemoteVersionHistory.For(RemoteVersionHistory.DefaultPath,host.Id.ToString(),agent));
        var options=new List<(string Value,string Label)>{("latest","最新（默认命令）")};
        if(previous!=null)options.Add(("rollback",$"回滚上一版（{previous}）"));
        options.Add(("pinned","指定版本…"));
        var note=previous==null?"该主机此 Agent 尚无版本历史，回滚不可用；检查版本/升级成功后会自动记录。":"回滚目标取自版本历史中上一个不同版本；指定版本用于安装任意已发布版本。";
        var dialog=new FormWindow(owner,"选择升级版本",[("mode","升级方式","latest",false),("version","指定版本号（如 1.2.3，仅「指定版本」时生效）","",false)],note,choices:new(){["mode"]=[..options]});
        if(dialog.ShowDialog()!=true)return null;
        var mode=dialog.Values["mode"];
        if(mode=="latest")return baseCommand;
        var version=mode=="rollback"?previous!:dialog.Values["version"].Trim();
        var pinned=RemoteAgentMaintenance.PinUpgradeCommand(baseCommand,version);
        if(pinned==null){MessageBox.Show(owner,"版本号无效或该命令不支持固定版本；已取消升级。","升级 Agent",MessageBoxButton.OK,MessageBoxImage.Warning);return null;}
        return pinned;
    }

    internal static string FormatMaintenanceResult(RemoteHost host,string kind,SshCommandResult result)
    {
        var action=kind=="upgrade"?"升级":"版本检查";
        if(result.TimedOut)return $"{action}超时，本地 SSH 连接已终止。远程命令可能仍在运行，请手动核对主机状态。";
        var output=(result.StdOut.Trim().Length>0?result.StdOut.Trim():result.StdErr.Trim());
        if(output.Length>1500)output=output[..1500]+"\n…（输出已截断）";
        var error=result.StdErr.Trim();if(error.Length>800)error=error[..800]+"…";
        return result.ExitCode==0
            ?$"{action}成功（「{host.Name}」，退出码 0）。\n\n{output}"
            :$"{action}失败（「{host.Name}」，退出码 {result.ExitCode}）。\n\n{(error.Length>0?error:output)}";
    }

    // 远程主机管理卡片上的维护操作行 + 内联结果文本（测试免密连接 / 检查版本 / 升级 Agent / 远端资源）。
    internal (WrapPanel Buttons,TextBlock Status,TextBlock Resources) CreateRemoteHostActions(Window owner,RemoteHost host)
    {
        var status=new TextBlock{FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,0),Visibility=Visibility.Collapsed};
        status.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        AutomationProperties.SetName(status,"远程维护结果 "+host.Name);
        var resources=new TextBlock{Text="远端资源：读取中…",FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,0)};
        resources.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        AutomationProperties.SetName(resources,"远端资源 "+host.Name);
        var test=new Button{Content="测试免密连接",Margin=new(0,0,8,0),ToolTip="ssh -o BatchMode=yes user@host whoami；只走密钥/ssh-agent/SSH config，不会提示密码"};
        AutomationProperties.SetName(test,"测试免密连接 "+host.Name);
        var version=new Button{Content="检查版本",Margin=new(0,0,8,0),ToolTip="通过免密 SSH 在远程工作目录执行 agent --version"};
        AutomationProperties.SetName(version,"检查版本 "+host.Name);
        var upgrade=new Button{Content="升级 Agent",Margin=new(0,0,8,0),ToolTip="通过免密 SSH 执行该 agent 的升级命令（可选最新/回滚上一版/指定版本）"};
        AutomationProperties.SetName(upgrade,"升级 Agent "+host.Name);
        var sample=new Button{Content="远端资源",ToolTip="免密 SSH 读取 free -b / nproc / loadavg（15 秒上限，每主机 30 秒节流）；不显示任何凭据"};
        AutomationProperties.SetName(sample,"远端资源 "+host.Name);
        var running=false;
        async Task RunAsync(Func<Task<string>> action)
        {
            if(running)return;
            running=true;test.IsEnabled=version.IsEnabled=upgrade.IsEnabled=false;
            status.Visibility=Visibility.Visible;status.Text="正在执行…";
            try{status.Text=await action();}
            catch(Exception e)when(e is not OutOfMemoryException){status.Text="失败："+e.Message;}
            finally{running=false;test.IsEnabled=version.IsEnabled=upgrade.IsEnabled=true;}
        }
        var sampling=false;
        async Task ShowResourcesAsync()
        {
            if(sampling)return;
            sampling=true;sample.IsEnabled=false;resources.Text="远端资源：读取中…";
            try{resources.Text=RemoteResourceText(await SampleRemoteResourcesThrottledAsync(host));}
            finally{sampling=false;sample.IsEnabled=true;}
        }
        test.Click+=async(_,_)=>await RunAsync(async()=>{var(_,message)=await SshRemoteCommand.TestConnectionAsync(host);return message;});
        version.Click+=(_,_)=>PromptMaintenance(false);
        upgrade.Click+=(_,_)=>PromptMaintenance(true);
        sample.Click+=async(_,_)=>await ShowResourcesAsync();
        _=ShowResourcesAsync();
        void PromptMaintenance(bool up)
        {
            if(running)return;
            var defaultDir=config.Draft.Slots.FirstOrDefault(s=>s.RemoteHostId==host.Id&&s.RemoteDirectory is {Length:>0})?.RemoteDirectory??"";
            var defaultAgent=host.AvailableAgents.FirstOrDefault()??"pi";
            var fields=new (string Key,string Label,string Value,bool Multiline)[]
            {
                ("agent","Agent 类型",defaultAgent,false),
                ("dir","远程工作目录（如 /home/agent/work）",defaultDir,false)
            };
            var choices=new Dictionary<string,(string Value,string Label)[]>{["agent"]=[..host.AvailableAgents.Select(a=>(Value:a,Label:a))]};
            var caption=up?"升级 Agent":"检查版本";
            var dialog=new FormWindow(owner,caption,fields,up?"将通过免密 SSH 在远程主机上执行升级命令（默认命令可在主机编辑中覆盖）。":"将通过免密 SSH 在远程工作目录执行 --version。",choices:choices);
            if(dialog.ShowDialog()!=true)return;
            var agent=dialog.Values["agent"].Trim();var dir=dialog.Values["dir"].Trim();
            if(!RemoteWorkspaceMappingHelper.IsValidRemoteDirectory(dir)){MessageBox.Show(owner,"远程工作目录无效：必须是绝对路径或 ~/ 开头。",caption,MessageBoxButton.OK,MessageBoxImage.Warning);return;}
            var command=up?PickUpgradeCommand(owner,host,agent):RemoteAgentMaintenance.VersionCommandFor(agent);
            if(command==null){if(!up)MessageBox.Show(owner,$"Agent「{agent}」没有可用的版本检查命令。",caption,MessageBoxButton.OK,MessageBoxImage.Warning);return;}
            if(up&&MessageBox.Show(owner,$"将在「{host.Name}」上以 {host.AgentUser} 用户执行：\n\n{command}\n\n继续？",caption,MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            _=RunAsync(async()=>{var(_,message)=await RunVersionOrUpgradeAsync(host,agent,dir,up,command);return message;});
        }
        var buttons=new WrapPanel{Margin=new(0,8,0,0)};
        buttons.Children.Add(test);buttons.Children.Add(version);buttons.Children.Add(upgrade);buttons.Children.Add(sample);
        return(buttons,status,resources);
    }

    // C1 远程续接入口条件：槽位绑定 VPS、ResumeFlagsFor 非 null、远程目录有效，
    // 且存在同主机同远程目录同 agent 的已 Settled 远程 attempt（菜单打开时实时判定）。
    internal bool CanResumeRemoteSlot(Slot slot)
    {
        if(RemoteHostForSlot(slot) is not {} host||RemoteAgentMaintenance.ResumeFlagsFor(slot.AgentId) is null)return false;
        if(!RemoteWorkspaceMappingHelper.IsValidRemoteDirectory(slot.RemoteDirectory??""))return false;
        return status?.Entries?.Any(e=>IsResumableRemoteEntry(e,slot,host))==true;
    }

    private static bool IsResumableRemoteEntry(ControlEntry entry,Slot slot,RemoteHost host)=>
        entry.State=="Settled"&&entry.Plan.SourceSlotId==slot.Id&&entry.Plan.AgentId==slot.AgentId
        &&entry.Plan.RemoteExecution is {} remote&&remote.Host==host.Address&&remote.RemoteWorkingDirectory==slot.RemoteDirectory;

    // C1 续接上次远程会话：先经远程 --help 核对续接 flag（fail-closed），再多行输入新任务说明，
    // 复用既有远程启动链路（RemoteExecutionSpec → reserve → Terminal → Worker pane），不新造启动路径。
    internal async Task ResumeRemoteSessionAsync(Guid slotId)
    {
        if(closeRequested||busy||launchFinalizing||launchCancellation!=null||!cancelTask.IsCompleted||readOnly||submittedAttempts.Length>0){ErrorText.Text="当前有启动/停止操作正在进行，请稍后再续接。";return;}
        var slot=config.Draft.Slots.FirstOrDefault(s=>s.Id==slotId);
        var host=slot is null?null:RemoteHostForSlot(slot);
        if(slot==null||host==null){ErrorText.Text="此项未配置远程主机（VPS），无法续接。";return;}
        var flags=RemoteAgentMaintenance.ResumeFlagsFor(slot.AgentId);
        if(flags==null){ErrorText.Text=$"Agent「{slot.AgentId}」没有已知的远程续接参数，已拒绝续接。";return;}
        var dir=slot.RemoteDirectory??"";
        if(!RemoteWorkspaceMappingHelper.IsValidRemoteDirectory(dir)){ErrorText.Text="请先在此项「详情」中填写有效的远程工作目录（绝对路径或 ~/ 开头），再续接。";return;}
        await RefreshAsync();
        var previous=status?.Entries?.Where(e=>IsResumableRemoteEntry(e,slot,host)).OrderByDescending(e=>e.Created).FirstOrDefault();
        if(previous==null){ErrorText.Text="没有同远程目录的已结束（Settled）远程会话，无法续接；请先正常启动一次远程会话。";return;}
        ProgressText.Text=$"正在核对「{host.Name}」上 {slot.AgentId} --help 的续接参数…";
        bool helpOk;string helpMessage;
        try{(helpOk,helpMessage)=await SshRemoteCommand.VerifyResumeFlagsAsync(host,slot.AgentId,dir,flags);}
        finally{ProgressText.Text="";}
        if(!helpOk){MessageBox.Show(this,helpMessage,"续接上次远程会话",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        var dialog=new FormWindow(this,"续接上次远程会话",[("task","新任务说明（作为最后一个参数传给远程 Agent）","",true)],$"将在「{host.Name}」的 {dir} 续接上次会话：原参数中的 --no-session 替换为 {string.Join(" ",flags)}（无则追加），末尾旧任务说明替换为新说明。\n已通过远程 --help 核对续接参数；启动仍走既有远程预约与终端链路。");
        if(dialog.ShowDialog()!=true)return;
        string[] arguments;
        try
        {
            arguments=RemoteAgentMaintenance.ResumeArguments(previous.Plan.RemoteExecution!.Arguments,flags,dialog.Values["task"].Trim());
            (previous.Plan.RemoteExecution with{Arguments=arguments}).Validate();
        }
        catch(InvalidDataException e){MessageBox.Show(this,e.Message,"续接上次远程会话",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
        await LaunchResumeCoreAsync(previous.Plan,arguments);
    }

    // 与 RetrySessionCoreAsync 同构的单次提交：新 attempt 复用上次远程计划的工作目录/主机/预算，仅替换参数。
    private async Task LaunchResumeCoreAsync(LaunchItem previousPlan,string[] arguments)
    {
        launchCancellation=new();SetBusy(true);
        try
        {
            await saveTask;if(!SaveHealthy)throw new IOException("请先解决配置保存失败或等待保存完成。");
            var terminal=await Task.Run(()=>Commands.Terminal(config.TerminalPath));
            var plan=previousPlan with{Id=Guid.NewGuid(),BatchId=Guid.NewGuid(),RetryOf=null,Command=previousPlan.Command with{Arguments=arguments},RemoteExecution=previousPlan.RemoteExecution! with{Arguments=arguments}};
            await AuthorizePlansAsync([plan]);
            var bindings=ReservationBindings([plan]);
            var reserve=await Ipc.ReserveWithCleanupAsync([plan],launchCancellation.Token,UnlockForCleanup,bindings.Policy,bindings.Revision,bindings.Hash,bindings.Digest,bindings.Slots);if(!reserve.Ok)throw new IOException(reserve.Message);
            submittedRevision=config.Draft.Revision;submittedAttempts=[plan.Id];submissionGeneration++;
            if(launchCancellation.Token.IsCancellationRequested)return;
            try{await Task.Run(()=>Commands.SubmitTerminal(terminal,[plan],"vertical"));}
            catch{ProgressText.Text="Terminal 提交结果未确认。请核对记录或撤销未领取项，不要重复提交。";throw;}
            if(launchCancellation.Token.IsCancellationRequested)return;
            ProgressText.Text="已提交远程续接尝试，正在等待回执。进程出现不代表 Agent 已就绪。";
        }
        finally{await FinishLaunchAsync();}
    }
}