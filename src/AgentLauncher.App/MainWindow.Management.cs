using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using Microsoft.Win32;

namespace AgentLauncher.App;
public partial class MainWindow
{
    private string DiagnosticText(bool includePaths)
    {
        return JsonSerializer.Serialize(new {
            Version=typeof(MainWindow).Assembly.GetName().Version?.ToString(),Runtime=Environment.Version.ToString(),At=DateTimeOffset.UtcNow,
            Protection="每个 Agent 2 GiB；Git 准备 1 GiB；受管进程合计 CPU 35%",
            ConfigReadOnly=readOnly,Unsaved=saveError!=null,CommandCheckFailed=validationError.Length>0,
            Monitor=status==null?"unavailable":status.Ok?"healthy":"needs-reconciliation",Memory=status?.Memory,
            Sessions=status?.Entries?.GroupBy(e=>new{e.Plan.Kind,e.State}).Select(g=>new{g.Key.Kind,g.Key.State,Count=g.Count()}).ToArray(),
            Commands=discoveredInstalls.Select(p=>new{Agent=p.Key,Found=p.Value.Length>0,Paths=includePaths?p.Value:null}).ToArray(),
            Project=includePaths?config.Draft.Project:null,ConfigPath=includePaths?configPath:null,
            Notice="不包含对话、启动参数、环境变量或凭据；命令存在不代表已登录/已验证版本。"
        },new JsonSerializerOptions{WriteIndented=true});
    }
    private void ShowDiagnostics(Window owner)
    {
        var window=new Window{Owner=owner,Title="诊断预览",Width=Math.Min(760,SystemParameters.WorkArea.Width-32),Height=Math.Min(660,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var root=new DockPanel{Margin=new(24)};
        var include=new CheckBox{Content="包含本地完整路径（可能包含用户名和项目名）",Margin=new(0,0,0,12)};DockPanel.SetDock(include,Dock.Top);root.Children.Add(include);
        var actions=new WrapPanel{Margin=new(0,12,0,0)};DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        var text=new TextBox{Text=DiagnosticText(false),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};AutomationProperties.SetName(text,"即将导出的诊断内容");root.Children.Add(text);
        include.Checked+=(_,_)=>text.Text=DiagnosticText(true);include.Unchecked+=(_,_)=>text.Text=DiagnosticText(false);
        var export=new Button{Content="导出 JSON",Margin=new(0,0,8,0)};export.Click+=async(_,_)=>{var picker=new SaveFileDialog{Filter="JSON|*.json",FileName="agent-launcher-diagnostics.json"};if(picker.ShowDialog(window)!=true)return;var content=text.Text;export.IsEnabled=false;try{await File.WriteAllTextAsync(picker.FileName,content);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){MessageBox.Show(window,"导出失败："+e.Message);}finally{export.IsEnabled=true;}};actions.Children.Add(export);
        var copy=new Button{Content="复制",Margin=new(0,0,8,0)};copy.Click+=(_,_)=>{try{Clipboard.SetText(text.Text);}catch(System.Runtime.InteropServices.COMException){MessageBox.Show(window,"剪贴板正忙，请稍后重试。");}};actions.Children.Add(copy);
        var close=new Button{Content="关闭",IsCancel=true};actions.Children.Add(close);window.Content=root;UiScale.Apply(window);window.ShowDialog();
    }
    private void ManageRecentProjects(Window owner)
    {
        var window=new Window{Owner=owner,Title="管理最近项目",Width=Math.Min(680,SystemParameters.WorkArea.Width-32),Height=Math.Min(500,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var root=new DockPanel{Margin=new(24)};var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var cancel=new Button{Content="取消",IsCancel=true,Margin=new(0,0,8,0)};footer.Children.Add(cancel);var apply=new Button{Content="应用",IsDefault=true,IsEnabled=!readOnly&&!busy};footer.Children.Add(apply);
        var list=new StackPanel();var snapshot=config.RecentProjects.ToList();RenderRecent();
        void RenderRecent(){list.Children.Clear();list.Children.Add(new TextBlock{Text="只移除最近项目记录，不删除文件夹，也不修改当前项目。",Margin=new(0,0,0,12)});foreach(var path in snapshot.ToArray()){var row=new DockPanel{Margin=new(0,0,0,8)};var remove=new Button{Content="移除",Margin=new(8,0,0,0)};AutomationProperties.SetName(remove,"移除最近项目 "+path);DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);remove.Click+=(_,_)=>{snapshot.Remove(path);RenderRecent();};row.Children.Add(new TextBox{Text=path,IsReadOnly=true});list.Children.Add(row);}}
        apply.Click+=(_,_)=>window.DialogResult=true;root.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});window.Content=root;UiScale.Apply(window);
        if(window.ShowDialog()==true&&!readOnly&&!busy){config.RecentProjects=snapshot;QueueSave();Render();}
    }
    private void ManageTemplates(Window owner)
    {
        var window=new Window{Owner=owner,Title="管理组合",Width=Math.Min(640,SystemParameters.WorkArea.Width-32),Height=Math.Min(520,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var root=new DockPanel{Margin=new(24)};var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var cancel=new Button{Content="取消",IsCancel=true,Margin=new(0,0,8,0)};footer.Children.Add(cancel);var apply=new Button{Content="应用",IsDefault=true,IsEnabled=!readOnly&&!busy};footer.Children.Add(apply);
        var entries=config.Templates.Select(p=>(Name:new TextBox{Text=p.Key},Snapshot:p.Value.Copy())).ToList();var list=new StackPanel();
        list.Children.Add(new TextBlock{Text="重命名或移除保存的组合；不会更改当前草稿，也不会结束会话。",Margin=new(0,0,0,12)});
        foreach(var entry in entries.ToArray()){var row=new DockPanel{Margin=new(0,0,0,8)};var remove=new Button{Content="移除",Margin=new(8,0,0,0)};DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);remove.Click+=(_,_)=>{entries.Remove(entry);list.Children.Remove(row);};AutomationProperties.SetName(entry.Name,"组合名称 "+entry.Name.Text);row.Children.Add(entry.Name);list.Children.Add(row);}
        apply.Click+=(_,_)=>{if(entries.Any(e=>string.IsNullOrWhiteSpace(e.Name.Text))||entries.Select(e=>e.Name.Text.Trim()).Distinct().Count()!=entries.Count){MessageBox.Show(window,"组合名称不能为空或重复，尚未修改任何组合。");return;}window.DialogResult=true;};
        root.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});window.Content=root;UiScale.Apply(window);
        if(window.ShowDialog()==true&&!readOnly&&!busy){config.Templates=entries.ToDictionary(e=>e.Name.Text.Trim(),e=>e.Snapshot);QueueSave();Render();}
    }
}
