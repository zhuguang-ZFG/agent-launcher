using System.Globalization;
using System.IO;
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

    private void RenderLaunchPreview(Slot[] active)
    {
        ExternalLayoutOptions.Visibility=config.EmbeddedTerminal?Visibility.Collapsed:Visibility.Visible;
        PreviewHeading.Text=config.EmbeddedTerminal?"标签预览":"分屏预览";
        PreviewHostLabel.Text=config.EmbeddedTerminal?"启动器内 · 点击标签切换会话":"Windows Terminal";
        if(!config.EmbeddedTerminal){RenderMultipanePreview(active);return;}
        Preview.Children.Clear();Preview.RowDefinitions.Clear();Preview.ColumnDefinitions.Clear();
        var content=new StackPanel();var tabs=new WrapPanel();content.Children.Add(tabs);
        foreach(var slot in active)
        {
            var tab=CreatePreviewPane(slot);tab.Padding=new(8,4,8,4);tab.ToolTip="终端标签："+slot.DisplayName;
            if(tab.Child is StackPanel lines)foreach(var extra in lines.Children.OfType<TextBlock>().Skip(1))extra.Visibility=Visibility.Collapsed;
            tabs.Children.Add(tab);
        }
        var guide=new TextBlock{Text=active.Length==0?"勾选 Agent 后，每项打开一个终端标签。":"一次查看一个会话；Ctrl+Tab 切换标签，滚轮回看输出。",TextWrapping=TextWrapping.Wrap,Margin=new(8,12,8,8)};
        guide.SetResourceReference(TextBlock.ForegroundProperty,"PreviewMuted");content.Children.Add(guide);Preview.Children.Add(content);
        PaneCount.Text=$"{active.Length} 个标签";
    }

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
    private void UpdateMultipaneSummary()
    {
        var enabled=config.Draft.Slots.Count(s=>s.Enabled);
        SelectionSummary.Text=$"已选 {enabled} 个，共 {config.Draft.Slots.Count} 个";
        ScalePresets.IsEnabled=!busy&&!readOnly&&!loading&&!closeRequested;
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
        if(!Agents.IsSupported(source.AgentId)){ProgressText.Text="此 Agent 类型已移除，不能复制到其他窗格。";return;}
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
        var settings=config.Draft.Resource??new ResourceSettings();var cost=$"预计占用约 {ResourceMiB(settings.PlanningBytes)} MiB（估算），保护上限 {ResourceGiB(settings.JobMemoryLimit)} GiB";
        if(!slot.Enabled)return "未勾选：不启动";
        var active=config.Draft.Slots.Where(s=>s.Enabled).ToArray();
        var worktree=config.Draft.DirectoryMode switch
        {
            "shared"=>"多个 Agent 共用同一目录",
            "prepared"=>"使用已准备目录",
            _ when active.Length<=1=>"使用原目录",
            _=>"同目录多项会建独立副本（worktree）"
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
}
