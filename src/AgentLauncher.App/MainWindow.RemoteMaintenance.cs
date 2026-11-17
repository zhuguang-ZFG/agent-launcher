using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private bool remoteMaintenanceRunning;
    internal RemoteHost? RemoteHostForSlot(Slot slot)=>slot.RemoteHostId is {} id?config.RemoteHosts.FirstOrDefault(h=>h.Id==id):null;

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
        var command=kind=="upgrade"?RemoteAgentMaintenance.UpgradeCommandFor(host,agent):RemoteAgentMaintenance.VersionCommandFor(agent);
        if(kind!="test"&&command==null){ErrorText.Text=$"Agent「{agent}」没有可用的{(kind=="upgrade"?"升级":"版本检查")}命令；可在远程主机配置中填写升级命令覆盖。";return;}
        if(kind=="upgrade"&&MessageBox.Show(this,$"将在「{host.Name}」上以 {host.AgentUser} 用户通过免密 SSH 执行：\n\n{command}\n\n继续？","升级 Agent",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        remoteMaintenanceRunning=true;
        try
        {
            ProgressText.Text=kind=="test"?$"正在测试「{host.Name}」免密连接…":$"正在「{host.Name}」上执行{(kind=="upgrade"?"升级":"版本检查")}…";
            string message;var ok=false;
            if(kind=="test"){(ok,message)=await SshRemoteCommand.TestConnectionAsync(host);}
            else
            {
                var result=kind=="upgrade"?await SshRemoteCommand.UpgradeAgentAsync(host,agent,slot.RemoteDirectory!):await SshRemoteCommand.CheckVersionAsync(host,agent,slot.RemoteDirectory!);
                ok=!result.TimedOut&&result.ExitCode==0;
                message=FormatMaintenanceResult(host,kind,result);
            }
            MessageBox.Show(this,message,kind=="test"?"远程免密连接":kind=="upgrade"?"升级 Agent":"检查远程版本",MessageBoxButton.OK,ok?MessageBoxImage.Information:MessageBoxImage.Warning);
        }
        finally{remoteMaintenanceRunning=false;ProgressText.Text="";}
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

    // 远程主机管理卡片上的维护操作行 + 内联结果文本（测试免密连接 / 检查版本 / 升级 Agent）。
    internal (WrapPanel Buttons,TextBlock Status) CreateRemoteHostActions(Window owner,RemoteHost host)
    {
        var status=new TextBlock{FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,0),Visibility=Visibility.Collapsed};
        status.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        AutomationProperties.SetName(status,"远程维护结果 "+host.Name);
        var test=new Button{Content="测试免密连接",Margin=new(0,0,8,0),ToolTip="ssh -o BatchMode=yes user@host whoami；只走密钥/ssh-agent/SSH config，不会提示密码"};
        AutomationProperties.SetName(test,"测试免密连接 "+host.Name);
        var version=new Button{Content="检查版本",Margin=new(0,0,8,0),ToolTip="通过免密 SSH 在远程工作目录执行 agent --version"};
        AutomationProperties.SetName(version,"检查版本 "+host.Name);
        var upgrade=new Button{Content="升级 Agent",ToolTip="通过免密 SSH 执行该 agent 的升级命令（可在编辑中覆盖默认命令）"};
        AutomationProperties.SetName(upgrade,"升级 Agent "+host.Name);
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
        test.Click+=async(_,_)=>await RunAsync(async()=>{var(_,message)=await SshRemoteCommand.TestConnectionAsync(host);return message;});
        version.Click+=(_,_)=>PromptMaintenance(false);
        upgrade.Click+=(_,_)=>PromptMaintenance(true);
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
            var command=up?RemoteAgentMaintenance.UpgradeCommandFor(host,agent):RemoteAgentMaintenance.VersionCommandFor(agent);
            if(command==null){MessageBox.Show(owner,$"Agent「{agent}」没有可用的{(up?"升级":"版本检查")}命令；请在主机编辑中填写升级命令覆盖。",caption,MessageBoxButton.OK,MessageBoxImage.Warning);return;}
            if(up&&MessageBox.Show(owner,$"将在「{host.Name}」上以 {host.AgentUser} 用户执行：\n\n{command}\n\n继续？",caption,MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            _=RunAsync(async()=>
            {
                var result=up?await SshRemoteCommand.UpgradeAgentAsync(host,agent,dir):await SshRemoteCommand.CheckVersionAsync(host,agent,dir);
                return FormatMaintenanceResult(host,up?"upgrade":"version",result);
            });
        }
        var buttons=new WrapPanel{Margin=new(0,8,0,0)};
        buttons.Children.Add(test);buttons.Children.Add(version);buttons.Children.Add(upgrade);
        return(buttons,status);
    }
}