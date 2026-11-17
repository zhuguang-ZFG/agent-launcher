using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task ShowCleanupInventoryAsync(Window owner,string target,string reason="")
    {
        var git=Commands.FindExe("git.exe")??throw new IOException("未找到 Git，无法列出文件。");
        var window=new Window{Owner=owner,Title="清理前检查文件",Width=Math.Min(780,SystemParameters.WorkArea.Width-32),Height=Math.Min(620,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var root=new DockPanel{Margin=new Thickness(20)};
        var note=new TextBlock{Text=reason+"\n已跟踪修改：检查并提交需要保留的内容。未跟踪文件：添加提交或备份。忽略内容：可能包含 .env、本地数据和构建产物，先备份再手动处理；此处不会删除。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};
        DockPanel.SetDock(note,Dock.Top);root.Children.Add(note);
        var actions=new WrapPanel();DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        var list=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.NoWrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};
        var refresh=new Button{Content="刷新清单"};refresh.Click+=async(_,_)=>await Refresh();actions.Children.Add(refresh);
        var open=new Button{Content="打开工作目录"};open.Click+=(_,_)=>{try{Process.Start(new ProcessStartInfo(target){UseShellExecute=true})?.Dispose();}catch(Exception ex)when(ex is System.ComponentModel.Win32Exception or IOException){note.Text=ex.Message;}};actions.Children.Add(open);
        var copy=new Button{Content="复制清单"};copy.Click+=(_,_)=>{try{Clipboard.SetText(list.Text);}catch(System.Runtime.InteropServices.COMException){note.Text="剪贴板正忙，请稍后重试。";}};actions.Children.Add(copy);
        root.Children.Add(list);window.Content=root;UiScale.Apply(window);window.Show();await Refresh();
        async Task Refresh()
        {
            refresh.IsEnabled=false;
            try{var files=await WorkspaceCleanupShell.InventoryAsync(git,target,CancellationToken.None);list.Text=files.Count==0?"当前没有文件阻止清理。清理时仍会重新核对分支、索引和会话占用。":string.Join("\n",files.GroupBy(f=>f.Category).Select(group=>$"{group.Key} · {group.Count()} 项\n"+string.Join("\n",group.Select(f=>f.Display))));}
            catch(Exception ex)when(ex is not OutOfMemoryException){list.Text="清单读取失败："+ex.Message;}
            finally{refresh.IsEnabled=true;}
        }
    }
}
