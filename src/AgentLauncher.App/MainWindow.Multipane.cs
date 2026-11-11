using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;
public partial class MainWindow
{
    private readonly Dictionary<Guid,Slot> presetSlotDefaults=[];
    private readonly HashSet<Guid> presetEditedSlots=[];
    private bool applyingScalePreset;
    private bool showingResourceSettings;

    private void RenderMultipanePreview(Slot[] active)
    {
        Preview.Children.Clear();Preview.RowDefinitions.Clear();Preview.ColumnDefinitions.Clear();
        if(active.Length==0)
        {
            var empty=new TextBlock{Text="勾选 Agent 后，这里实时预览实际窗格；未勾选项不会启动。",VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Margin=new(8)};
            empty.SetResourceReference(TextBlock.ForegroundProperty,"PreviewMuted");Preview.Children.Add(empty);PaneCount.Text="0 个窗格";return;
        }
        var map=PaneLayout.Map(active.Length,config.Draft.Layout);var rows=PaneLayout.RowsPerColumn(active.Length,config.Draft.Layout);
        var columns=new Grid[rows.Length];
        for(var column=0;column<rows.Length;column++)
        {
            Preview.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            var grid=new Grid();for(var row=0;row<rows[column];row++)grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
            Grid.SetColumn(grid,column);columns[column]=grid;Preview.Children.Add(grid);
        }
        foreach(var cell in map)
        {
            var pane=CreatePreviewPane(active[cell.EnabledIndex]);pane.Tag=cell;pane.MinWidth=0;pane.MinHeight=0;
            if(pane.Child is StackPanel content)
            {
                var lines=content.Children.OfType<TextBlock>().ToArray();for(var i=0;i<lines.Length;i++){lines[i].TextWrapping=TextWrapping.NoWrap;lines[i].TextTrimming=TextTrimming.CharacterEllipsis;lines[i].MinWidth=0;if(i>0)lines[i].Visibility=active.Length>4?Visibility.Collapsed:Visibility.Visible;}pane.ToolTip=$"第 {config.Draft.Slots.IndexOf(active[cell.EnabledIndex])+1} 项 · {active[cell.EnabledIndex].DisplayName}";
            }
            Grid.SetRow(pane,cell.Row);columns[cell.Column].Children.Add(pane);
        }
        PaneCount.Text=$"{active.Length} 个实际窗格";
    }

    private static string ResourceGiB(decimal value)=>(value/ResourcePolicy.GiB).ToString("0.00",CultureInfo.CurrentCulture);
    private static string ResourceMiB(ulong value)=>(value/(decimal)ResourcePolicy.MiB).ToString("0.##",CultureInfo.CurrentCulture);
    private void UpdateMultipaneSummary(AdmissionDecision admission)
    {
        var settings=config.Draft.Resource??new ResourceSettings();var enabled=config.Draft.Slots.Count(s=>s.Enabled);
        SelectionSummary.Text=$"已选 {enabled} 个，共 {config.Draft.Slots.Count} 个";ConfiguredCountValue.Text=config.Draft.Slots.Count.ToString(CultureInfo.CurrentCulture);EnabledCountValue.Text=enabled.ToString(CultureInfo.CurrentCulture);
        var authoritative=status?.Ok==true&&status.Entries is not null&&!status.More;
        if(authoritative)
        {
            var agents=status!.Entries!.Where(e=>e.Plan.Kind is null or "agent").Where(e=>!Store.IsTerminal(e)).DistinctBy(e=>e.Plan.Id).ToArray();
            ReservedCountValue.Text=agents.Length.ToString(CultureInfo.CurrentCulture);StartedCountValue.Text=agents.Count(e=>e.State=="Started").ToString(CultureInfo.CurrentCulture);
            var creating=agents.Count(e=>e.State=="Creating");var unknown=agents.Count(e=>e.State=="Unknown");
            CreatedPendingText.Text=$"另有创建中 {creating} 项、待核对 {unknown} 项；均不计为已启动进程。";CreatedPendingText.Visibility=creating+unknown>0?Visibility.Visible:Visibility.Collapsed;
        }
        else
        {
            ReservedCountValue.Text="—";StartedCountValue.Text="—";CreatedPendingText.Text="运行状态尚未核对，预约与已启动进程数未知。";CreatedPendingText.Visibility=Visibility.Visible;
        }
        System.Windows.Automation.AutomationProperties.SetName(ConfiguredCountValue,"已配置槽数 "+ConfiguredCountValue.Text);
        System.Windows.Automation.AutomationProperties.SetName(EnabledCountValue,"已勾选槽数 "+EnabledCountValue.Text);
        System.Windows.Automation.AutomationProperties.SetName(ReservedCountValue,"活动预约数 "+ReservedCountValue.Text);
        System.Windows.Automation.AutomationProperties.SetName(StartedCountValue,"已启动进程数 "+StartedCountValue.Text);
        ScalePresets.IsEnabled=!busy&&!readOnly&&!loading&&!closeRequested;ResourceSettingsButton.IsEnabled=!busy&&!loading&&!closeRequested;
        ResourceModeText.Text="资源模式："+(settings.Mode switch{"conservative"=>"保守","custom"=>"自定义",_=>"实用（默认）"});
        PerSlotBudgetText.Text=$"每个启用项：规划 {ResourceMiB(settings.PlanningBytes)} MiB / Job 提交上限 {ResourceGiB(settings.JobMemoryLimit)} GiB";
        var planningTotal=(decimal)admission.PlanningRequested+admission.PlanningReserved;var capTotal=(decimal)admission.CapSumRequested+admission.CapSumReserved;
        var sample=status?.Memory;var sampleValid=status?.Ok==true&&admission.SampleValid&&ResourcePolicy.IsSampleValid(sample);
        BudgetValue.Text=sample is null?"—":$"{ResourceGiB(admission.PlanningRequested)} GiB";
        PlanningRequestedValue.Text=sample is null?"—":$"{ResourceGiB(admission.PlanningRequested)} GiB";
        PlanningReservedValue.Text=sample is null?"—":$"{ResourceGiB(admission.PlanningReserved)} GiB";
        PlanningTotalValue.Text=sample is null?"—":$"{ResourceGiB(planningTotal)} GiB";
        JobCapTotalValue.Text=sample is null?"—":$"{ResourceGiB(capTotal)} GiB";
        JobCapDifferenceText.Text=sample is null?"软预算对照待采样":$"软预算 {ResourceGiB(admission.SoftCap)} GiB · "+(capTotal>admission.SoftCap?$"软超差额 {ResourceGiB(capTotal-admission.SoftCap)} GiB":$"未超，余量 {ResourceGiB(admission.SoftCap-capTotal)} GiB");
        var cpuLimit=$"新批次 CPU 封顶 {settings.CpuRate/100d:0.##}%";
        if(status?.RootCpuRate is {} rootRate)cpuLimit+=$" · 当前根 Job {rootRate/100d:0.##}%";
        if(sampleValid&&sample is not null)
        {
            AvailableValue.Text=$"{ResourceGiB(sample.Available)} GiB";
            var physicalRemaining=(decimal)sample.Available-planningTotal;var commitRemaining=(decimal)sample.CommitAvailable-planningTotal;
            var planningRemaining=Math.Min(physicalRemaining-admission.PhysicalFloor,commitRemaining-admission.CommitFloor);
            PlanningRemainingValue.Text=$"{ResourceGiB(planningRemaining)} GiB";
            PhysicalHeadroomText.Text=$"物理可用 {ResourceGiB(sample.Available)} GiB / 共 {ResourceGiB(sample.Total)} GiB\n规划后 {ResourceGiB(physicalRemaining)} GiB · 临界余量 {ResourceGiB(admission.PhysicalFloor)} GiB";
            CommitHeadroomText.Text=$"提交可用 {ResourceGiB(sample.CommitAvailable)} GiB / 上限 {ResourceGiB(sample.CommitLimit)} GiB\n规划后 {ResourceGiB(commitRemaining)} GiB · 临界余量 {ResourceGiB(admission.CommitFloor)} GiB";
            CpuLimitText.Text=$"整机 CPU {sample.CpuPercent:0.0}% · {cpuLimit}";
        }
        else
        {
            AvailableValue.Text="—";PlanningRemainingValue.Text="—";PhysicalHeadroomText.Text="物理可用与临界余量：等待有效采样";CommitHeadroomText.Text="提交可用与临界余量：等待有效采样";CpuLimitText.Text="整机 CPU 待采样 · "+cpuLimit;
        }
        var reason=status?.Ok!=true?"资源检测不可用，暂缓启动":!sampleValid?"系统采样无效，请稍后重试。":!string.IsNullOrWhiteSpace(status.AdmissionBlockedReason)?status.AdmissionBlockedReason:admission.Reason;
        ResourceText.Text=reason+(sampleValid&&sample is not null?$" · {sample.At.LocalDateTime:HH:mm:ss} 采样":"");
    }

    private void ScalePresetClick(object sender,RoutedEventArgs e)
    {
        if(sender is Button{Tag:string text}&&int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var count))ApplyScalePreset(count);
    }
    private void ApplyScalePreset(int count)
    {
        if(count is not (1 or 2 or 4 or 8 or 16))throw new ArgumentOutOfRangeException(nameof(count));
        if(busy||readOnly||loading||rendering||closeRequested)return;
        var appended=new List<Slot>();var preserved=new List<int>();var disabled=0;var applied=false;
        applyingScalePreset=true;
        try
        {
            applied=Edit(d=>
            {
                while(d.Slots.Count<count){var slot=new Slot{Enabled=true};d.Slots.Add(slot);appended.Add(slot.Copy());}
                for(var i=count;i<d.Slots.Count;i++)
                {
                    var slot=d.Slots[i];
                    if(presetSlotDefaults.TryGetValue(slot.Id,out var baseline)&&!presetEditedSlots.Contains(slot.Id)&&SamePresetSlot(slot,baseline))
                    {if(slot.Enabled){slot.Enabled=false;disabled++;}}
                    else preserved.Add(i+1);
                }
            });
        }
        finally{applyingScalePreset=false;}
        if(!applied)return;
        foreach(var slot in appended)presetSlotDefaults[slot.Id]=slot;
        foreach(var slot in config.Draft.Slots)if(!presetEditedSlots.Contains(slot.Id)&&presetSlotDefaults.TryGetValue(slot.Id,out var baseline))baseline.Enabled=slot.Enabled;
        var retained=preserved.Count==0?"":string.Join("、",preserved.Select(position=>$"第 {position} 项"))+"配置已保留，未删除；已编辑项的勾选状态不变。";
        ProgressText.Text=$"规模预设 {count}：新增并启用 {appended.Count} 项，取消勾选 {disabled} 个未编辑超出项。{retained} 实际启动仅包含已勾选项。";
    }
    private void TrackMultipaneSlotEdits(Draft before,Draft after)
    {
        if(applyingScalePreset)return;
        foreach(var slot in after.Slots)
        {
            if(!presetSlotDefaults.ContainsKey(slot.Id)||presetEditedSlots.Contains(slot.Id))continue;
            var previous=before.Slots.FirstOrDefault(s=>s.Id==slot.Id);
            if(previous is not null&&!SamePresetSlot(previous,slot))presetEditedSlots.Add(slot.Id);
        }
    }
    private static bool SamePresetSlot(Slot first,Slot second)
    {
        if(first.AgentId!=second.AgentId||first.CustomName!=second.CustomName||first.Directory!=second.Directory||first.RequiresDirectory!=second.RequiresDirectory||first.Note!=second.Note||first.Enabled!=second.Enabled||first.Profiles.Count!=second.Profiles.Count)return false;
        foreach(var pair in first.Profiles)if(!second.Profiles.TryGetValue(pair.Key,out var profile)||pair.Value.Executable!=profile.Executable||!pair.Value.Arguments.SequenceEqual(profile.Arguments))return false;
        return true;
    }
    private void ApplyAgentToSlots(Guid sourceId,bool all)
    {
        if(busy||readOnly||loading||rendering||closeRequested)return;
        var source=config.Draft.Slots.FirstOrDefault(s=>s.Id==sourceId);if(source is null)return;
        var agentId=source.AgentId;var profile=source.Current.Copy();var sourceIndex=config.Draft.Slots.IndexOf(source);var copied=0;
        if(Edit(d=>
        {
            for(var i=all?0:sourceIndex+1;i<d.Slots.Count;i++)
            {
                var target=d.Slots[i];if(target.Id==sourceId)continue;target.AgentId=agentId;target.Profiles[agentId]=profile.Copy();copied++;
            }
        }))ProgressText.Text=$"已将第 {sourceIndex+1} 项的 Agent 与对应程序/参数复制到 {copied} 项；目录、备注、名称、ID 与勾选状态均保留。";
    }
    private string SlotCostText(Slot slot)
    {
        var settings=config.Draft.Resource??new ResourceSettings();var cost=$"规划 {ResourceMiB(settings.PlanningBytes)} MiB / Job 上限 {ResourceGiB(settings.JobMemoryLimit)} GiB（非实测）";
        if(!slot.Enabled)return cost+" · 未勾选：不预约、不创建 worktree、不启动进程。";
        var active=config.Draft.Slots.Where(s=>s.Enabled).ToArray();
        var worktree=config.Draft.DirectoryMode switch
        {
            "shared"=>"共享目录 · 不建 worktree",
            "prepared"=>"已准备目录 · 启动前核对",
            _ when active.Length<=1=>"单项用原目录 · 不建 worktree",
            _=>"同工作区多项才新建或复用 worktree"
        };
        return cost+" · "+worktree+"。";
    }

    private async Task ShowResourceSettingsAsync()
    {
        if(showingResourceSettings||busy||loading||closeRequested)return;showingResourceSettings=true;
        try
        {
            await RefreshAsync();if(status?.Memory is not {} sample){ErrorText.Text="资源检测不可用，暂不能编辑资源参数；请稍后重试。";return;}
            var revision=config.Draft.Revision;var initial=(config.Draft.Resource??new ResourceSettings()).Copy();
            var activeOrUnknown=status.Ok!=true||status.Entries is null||status.Entries.Any(e=>!Store.IsTerminal(e));
            var dialog=new ResourceSettingsWindow(this,initial,sample,activeOrUnknown,readOnly||status.Ok!=true||!ResourcePolicy.IsSampleValid(sample));
            if(dialog.ShowDialog()!=true||dialog.Value is not {} next)return;
            if(readOnly||busy||closeRequested||revision!=config.Draft.Revision){ErrorText.Text="配置已改变或当前不可编辑，资源设置未应用；请重新打开。";return;}
            await RefreshAsync();
            if(status?.Ok!=true||status.Memory is not {} latest||!ResourcePolicy.IsSampleValid(latest)){ErrorText.Text="系统采样或运行状态不可用，资源设置未应用；请稍后重试。";return;}
            if(revision!=config.Draft.Revision){ErrorText.Text="配置已改变，资源设置未应用；请重新打开。";return;}
            ResourcePolicy.ValidateSettings(next,latest.Total,latest.CommitLimit);
            var nowActive=status.Entries is null||status.Entries.Any(e=>!Store.IsTerminal(e));
            if(nowActive&&next.CpuRate!=initial.CpuRate){ErrorText.Text="存在活动批次或尚未核对的预约，CPU 上限不能修改；请重新打开。";return;}
            if(!Edit(d=>d.Resource=next.Copy()))return;
            await saveTask;await RefreshAsync();ProgressText.Text="资源设置已更新，只作用于之后的新批次；不会更改已运行 Job。";
        }
        finally{showingResourceSettings=false;}
    }

    private bool ConfirmResourceWaiver(LaunchItem[] items,AdmissionDecision decision,MemorySnapshot sample)
    {
        if(decision.Kind!=AdmissionKind.RequireWaiver||!decision.SampleValid||!ResourcePolicy.IsSampleValid(sample)||items.Length==0||items.Any(item=>item.Kind is not (null or "agent" or "git")))return false;
        var text=new StringBuilder("仅申请本次启动的一次性资源授权，不会关闭硬保护。\n\n范围（只包含本次实际启动槽）：\n");
        foreach(var item in items.Where(item=>item.Kind is null or "agent").OrderBy(item=>item.Position))
        {
            text.AppendLine($"第 {item.Position:00} 项：规划 {ResourceMiB(item.PlanningBytes??ResourcePolicy.DefaultPlanningBytes)} MiB / Job 提交上限 {ResourceGiB(item.Budget)} GiB");
        }
        if(items.Any(item=>item.Kind=="git"))text.AppendLine($"另含 Git 准备：规划 {ResourceMiB(ResourcePolicy.DefaultGitPlanningBytes)} MiB / Job 上限 {ResourceGiB(ResourcePolicy.GitJobMemoryLimit)} GiB");
        text.AppendLine($"\n采样时间：{sample.At.LocalDateTime:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"本次规划 {ResourceGiB(decision.PlanningRequested)} GiB，活动规划 {ResourceGiB(decision.PlanningReserved)} GiB。");
        text.AppendLine($"硬保护余量：物理至少 {ResourceGiB(decision.PhysicalFloor)} GiB；提交至少 {ResourceGiB(decision.CommitFloor)} GiB。");
        var capTotal=(decimal)decision.CapSumRequested+decision.CapSumReserved;
        text.AppendLine($"若按 Job 上限加总：{ResourceGiB(capTotal)} GiB；软预算 {ResourceGiB(decision.SoftCap)} GiB；软超差额 {ResourceGiB(Math.Max(0,capTotal-decision.SoftCap))} GiB。");
        text.AppendLine($"\n授权由 monitor 重新采样并签发，有效期 {ResourcePolicy.WaiverTtlSeconds} 秒；仅限以上计划、单次使用，不可复用或转借。过期或配置改变须重新预检与确认。不能突破任何硬保护；monitor 若改判拒绝将停止启动。");
        text.Append("规划估算用于准入，不是实测占用。Job 上限是提交上限，不是当前占用；运行中仍可能分配失败、CLI 退出或系统变卡。确认不保证全部窗格成功，也不会修改已运行 Job。\n\n是否确认并申请本次授权？");
        var message=text.ToString();
#if INTEGRATION_TESTS
        if(TestConfirm is {} confirm)return confirm(message,"一次性资源确认",MessageBoxButton.YesNo)==MessageBoxResult.Yes;
#endif
        var window=new Window{Owner=this,Title="一次性资源确认",Width=Math.Min(680,SystemParameters.WorkArea.Width-32),Height=Math.Min(680,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var root=new DockPanel{Margin=new(24)};var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);
        var cancel=new Button{Content="取消",IsCancel=true,Margin=new(0,0,8,0)};var yes=new Button{Content="确认并申请一次性授权"};buttons.Children.Add(cancel);buttons.Children.Add(yes);root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=new TextBlock{Text=message,TextWrapping=TextWrapping.Wrap}});
        yes.Click+=(_,_)=>window.DialogResult=true;window.Content=root;UiScale.Apply(window);window.Loaded+=(_,_)=>cancel.Focus();return window.ShowDialog()==true;
    }
}
