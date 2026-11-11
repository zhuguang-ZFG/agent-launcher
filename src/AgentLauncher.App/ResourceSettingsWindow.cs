using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using AgentLauncher.Core;

namespace AgentLauncher.App;
public sealed class ResourceSettingsWindow : Window
{
    internal readonly ComboBox ModeChoice=new(){Margin=new(0,4,0,12),SelectedValuePath="Tag"};
    internal readonly TextBox JobLimitGiB=new();
    internal readonly TextBox PlanningMiB=new();
    internal readonly TextBox PhysicalFloorGiB=new();
    internal readonly TextBox CommitFloorGiB=new();
    internal readonly TextBox CpuPercent=new();
    internal readonly TextBlock ValidationMessage=new();
    private readonly Button apply=new(){Content="应用到新批次",IsDefault=true};
    private readonly TextBlock fieldStatus=new(){TextWrapping=TextWrapping.Wrap};
    private readonly ResourceSettings initial;
    private readonly MemorySnapshot sample;
    private readonly bool activeOrUnknown,readOnly;
    internal ResourceSettings? Value { get; private set; }

    public ResourceSettingsWindow(Window owner,ResourceSettings settings,MemorySnapshot memory,bool hasActiveOrUnknown,bool isReadOnly)
    {
        initial=settings.Copy();sample=memory;activeOrUnknown=hasActiveOrUnknown;readOnly=isReadOnly;
        Owner=owner;Title="资源模式与保护";Width=Math.Min(680,SystemParameters.WorkArea.Width-32);Height=Math.Min(720,SystemParameters.WorkArea.Height-32);WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Style=(Style)Application.Current.Resources[typeof(Window)];
        var root=new DockPanel{Margin=new(24)};
        // Errors and actions sit outside the scrolling form so feedback stays visible and the footer keeps its size at 150% text.
        var footer=new StackPanel{Margin=new(0,12,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        ValidationMessage.SetResourceReference(TextBlock.ForegroundProperty,"ErrorInk");AutomationProperties.SetLiveSetting(ValidationMessage,AutomationLiveSetting.Assertive);
        var errorShell=Shell(ValidationMessage);
        var hideEmpty=new DataTrigger{Binding=new Binding(nameof(TextBlock.Text)){Source=ValidationMessage},Value=""};
        hideEmpty.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Collapsed));
        var errorStyle=new Style(typeof(Border));errorStyle.Triggers.Add(hideEmpty);errorShell.Style=errorStyle;
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="取消",IsCancel=true,Margin=new(0,0,8,0),MinWidth=88};apply.MinWidth=132;
        buttons.Children.Add(cancel);buttons.Children.Add(apply);footer.Children.Add(errorShell);footer.Children.Add(buttons);root.Children.Add(footer);

        var panel=new StackPanel();
        // Mode comes first because it decides whether the numeric fields below can be edited.
        var modeLabel=new Label{Content="资源模式",Target=ModeChoice,Padding=new(0)};AutomationProperties.SetName(ModeChoice,"资源模式");
        ModeChoice.Items.Add(new ComboBoxItem{Content="保守",Tag="conservative"});ModeChoice.Items.Add(new ComboBoxItem{Content="实用（默认）",Tag="practical"});ModeChoice.Items.Add(new ComboBoxItem{Content="自定义",Tag="custom"});
        panel.Children.Add(modeLabel);panel.Children.Add(ModeChoice);
        panel.Children.Add(new TextBlock{Text="保守按软预算严格准入；实用按规划估算准入（默认）；自定义可调整下方资源数值。",Style=(Style)FindResource("Caption"),Margin=new(0,0,0,12)});
        // The reason the numbers are read only stays attached to the numbers themselves.
        panel.Children.Add(Shell(fieldStatus));
        panel.Children.Add(new TextBlock{Text="资源数值",FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,4)});
        var maxJob=Math.Min(ResourcePolicy.MaxJobMemoryLimit,sample.Total);var maxPhysical=Math.Min(ResourcePolicy.MaxCustomPhysicalFloor,sample.Total/2);var maxCommit=Math.Min(ResourcePolicy.MaxCustomCommitFloor,sample.CommitLimit/4);
        Field(JobLimitGiB,$"每项 Job 内存上限（GiB；1～{GiB(maxJob)}）");
        panel.Children.Add(new TextBlock{Text="该上限由 Agent、MCP、浏览器和编译等全部工具子进程共享。多工具任务可为新批次提高上限，同时减少并行项；规划估算应反映实际工作负载。",Style=(Style)FindResource("Caption"),Margin=new(0,0,0,12)});
        Field(PlanningMiB,"每项规划估算（MiB；至少 256，不能超过该项 Job 上限）");
        Field(PhysicalFloorGiB,$"物理临界余量（GiB；1.5～{GiB(maxPhysical)}；留空自动，当前默认 {GiB(ResourcePolicy.DefaultPhysicalFloor(sample.Total))}）");
        Field(CommitFloorGiB,$"提交临界余量（GiB；1～{GiB(maxCommit)}；留空自动，当前默认 {GiB(ResourcePolicy.DefaultCommitFloor(sample.CommitLimit))}）");
        Field(CpuPercent,"新根 Job CPU 封顶（整数百分比；15～80；默认 35）");
        panel.Children.Add(new TextBlock{Text=$"Git 准备固定：规划 {ResourcePolicy.DefaultGitPlanningBytes/ResourcePolicy.MiB} MiB / Job 上限 1 GiB。CPU ≥ {ResourcePolicy.CpuBusyPercent}%、采样失效或硬保护余量不足时，任何模式都不能确认绕过。",Margin=new(0,8,0,12)});
        panel.Children.Add(new TextBlock{Text="运行中仍可能分配失败、CLI 退出或系统变卡。根 CPU 封顶是节流而非保证整机空闲；配置变更不会热改正在运行的 Job。切换离开自定义模式后，资源数值重置为默认值。",Margin=new(0,0,0,8)});
        var details=new Expander{IsExpanded=false,Margin=new(0,0,0,8)};details.SetResourceReference(Control.ForegroundProperty,"Ink");
        details.Header=new TextBlock{Text="单位与技术说明",FontWeight=FontWeights.SemiBold};
        details.Content=new TextBlock{Text="所有单位均为 1024 进制：1 GiB = 1024 MiB，1 MiB = 1024 × 1024 字节。规划估算不是实测占用；Job 上限是提交上限，不是当前占用。",Margin=new(0,4,0,0)};
        panel.Children.Add(details);
        root.Children.Add(new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});Content=root;
        LoadValues(initial);ModeChoice.SelectedValue=initial.Mode;ModeChoice.IsEnabled=!readOnly;apply.IsEnabled=!readOnly;
        ModeChoice.SelectionChanged+=(_,_)=>{if(SelectedMode!="custom")LoadValues(new ResourceSettings());UpdateEditable();ValidationMessage.Text="";};
        UpdateEditable();UiScale.Apply(this);apply.Click+=(_,_)=>{if(TryApplySettings())DialogResult=true;};Loaded+=(_,_)=>ModeChoice.Focus();
        void Field(TextBox input,string label)
        {
            input.Margin=new(0,4,0,12);AutomationProperties.SetName(input,label);
            panel.Children.Add(new Label{Target=input,Padding=new(0),Content=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap}});panel.Children.Add(input);
        }
        Border Shell(UIElement child)
        {
            var border=new Border{Padding=new(10,6,10,6),Margin=new(0,0,0,12),Child=child};
            border.SetResourceReference(Border.BackgroundProperty,"Raised");border.SetResourceReference(Border.BorderBrushProperty,"InputLine");
            border.BorderThickness=new(1);border.CornerRadius=new(6);return border;
        }
    }
    private string SelectedMode=>(ModeChoice.SelectedItem as ComboBoxItem)?.Tag as string??initial.Mode;
    private static string GiB(ulong bytes)=>(bytes/(decimal)ResourcePolicy.GiB).ToString("0.################",CultureInfo.CurrentCulture);
    private void LoadValues(ResourceSettings settings)
    {
        JobLimitGiB.Text=GiB(settings.JobMemoryLimit);PlanningMiB.Text=(settings.PlanningBytes/(decimal)ResourcePolicy.MiB).ToString("0.################",CultureInfo.CurrentCulture);
        PhysicalFloorGiB.Text=settings.PhysicalFloorOverride is {} physical?GiB(physical):"";CommitFloorGiB.Text=settings.CommitFloorOverride is {} commit?GiB(commit):"";CpuPercent.Text=(settings.CpuRate/100m).ToString("0.##",CultureInfo.CurrentCulture);
    }
    private void UpdateEditable()
    {
        var editable=SelectedMode=="custom"&&!readOnly;
        foreach(var box in new[]{JobLimitGiB,PlanningMiB,PhysicalFloorGiB,CommitFloorGiB}){box.IsReadOnly=!editable;box.IsTabStop=editable;}
        CpuPercent.IsReadOnly=!editable||activeOrUnknown;CpuPercent.IsTabStop=!CpuPercent.IsReadOnly;
        if(activeOrUnknown)CpuPercent.Text=(initial.CpuRate/100m).ToString("0.##",CultureInfo.CurrentCulture);
        fieldStatus.Text=editable
            ?activeOrUnknown?"可编辑下一批内存设置：当前任务维持原上限；CPU 共用上限在任务运行期间保持只读。":"可编辑：新数值只作用于之后启动的批次，不会热改正在运行的 Job。"
            :readOnly
                ?"只读：当前配置只读或采样/运行状态不可用，只能查看，不会应用资源设置。"
                :activeOrUnknown
                    ?"只读：存在活动批次或运行状态尚未核对；预算、余量与 CPU 数值不可编辑，新值只对之后启动生效。"
                    :"只读：仅自定义模式可编辑资源数值；切换离开自定义会重置为默认值。";
    }
    internal bool TryApplySettings()
    {
        Value=null;
        if(readOnly){ValidationMessage.Text="当前资源设置只读，未应用。";return false;}
        try
        {
            ResourceSettings next;
            if(SelectedMode!="custom")next=new(){Mode=SelectedMode};
            else
            {
                if(!int.TryParse(CpuPercent.Text.Trim(),NumberStyles.None,CultureInfo.CurrentCulture,out var percent)||percent is <15 or >80)throw new InvalidDataException("CPU 封顶请输入 15～80 的整数百分比；未应用。 ");
                next=new()
                {
                    Mode="custom",JobMemoryLimit=ReadBytes(JobLimitGiB.Text,ResourcePolicy.GiB,"Job 内存上限"),PlanningBytes=ReadBytes(PlanningMiB.Text,ResourcePolicy.MiB,"规划估算"),
                    PhysicalFloorOverride=string.IsNullOrWhiteSpace(PhysicalFloorGiB.Text)?null:ReadBytes(PhysicalFloorGiB.Text,ResourcePolicy.GiB,"物理临界余量"),
                    CommitFloorOverride=string.IsNullOrWhiteSpace(CommitFloorGiB.Text)?null:ReadBytes(CommitFloorGiB.Text,ResourcePolicy.GiB,"提交临界余量"),CpuRate=percent*100
                };
            }
            if(activeOrUnknown&&next.CpuRate!=initial.CpuRate)throw new InvalidDataException("活动批次仍使用当前 CPU 上限；请保持此值，待任务结束后再修改。");
            ResourcePolicy.ValidateSettings(next,sample.Total,sample.CommitLimit);Value=next;ValidationMessage.Text="";return true;
        }
        catch(InvalidDataException error){ValidationMessage.Text=error.Message;return false;}
    }
    private static ulong ReadBytes(string text,ulong unit,string label)
    {
        const NumberStyles number=NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign|NumberStyles.AllowLeadingWhite|NumberStyles.AllowTrailingWhite;
        if(!decimal.TryParse(text,number,CultureInfo.CurrentCulture,out var value)||value<0||value>ulong.MaxValue/(decimal)unit)throw new InvalidDataException($"{label}请输入有效的非负数值（1024 进制），未应用。 ");
        var bytes=value*unit;
        if(bytes!=decimal.Truncate(bytes))throw new InvalidDataException($"{label}换算后必须是整数字节，请减少小数位；未应用。 ");
        return (ulong)bytes;
    }
}
