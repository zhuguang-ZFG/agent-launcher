using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private sealed class PaneView
    {
        public required TabItem Tab;
        public required TerminalCard Card;
        public required TextBlock Status;
        public Button Reconnect=new();
        public readonly CancellationTokenSource Stop=new();
        public TerminalPipeClient? Client;
        public int PendingBytes;
        public bool Finished;
    }

    private readonly Dictionary<Guid,PaneView> embeddedViews=[];

    private Task<string> ResolveLaunchTerminalAsync(CancellationToken ct=default)=>
        config.EmbeddedTerminal?Task.FromResult(""):Task.Run(()=>Commands.Terminal(config.TerminalPath),ct);

    private async Task SubmitPanesAsync(string terminal,IReadOnlyList<LaunchItem> plans,string layout,CancellationToken ct=default,bool reattach=false)
    {
        if(terminal.Length!=0)
        {
            await Task.Run(()=>
            {
                if(reattach)Commands.SubmitReattachPane(terminal,plans.Single());
                else Commands.SubmitTerminal(terminal,plans,layout,ct,TerminalProfile.TryPrepare(out _));
            },ct);
            return;
        }
        foreach(var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var start=new ProcessStartInfo(Store.WorkerPath){UseShellExecute=false,CreateNoWindow=true};
            start.ArgumentList.Add("terminal-host");start.ArgumentList.Add(plan.Id.ToString());start.ArgumentList.Add(reattach?"reattach":"start");
            using var host=Process.Start(start)??throw new IOException("无法打开应用内终端。");
            if(reattach&&embeddedViews.ContainsKey(plan.Id))RemoveEmbedded(plan.Id);
            OpenEmbedded(plan);
        }
    }

    private sealed record EmbeddedMarker(int Pid,long ProcessTime);
    private async Task<bool> TryShowEmbeddedAsync(LaunchItem plan)
    {
        if(!embeddedViews.ContainsKey(plan.Id))
        {
            var marker=Store.FilePath(Path.Combine("attempts",plan.Id.ToString("N"),"embedded.json"));
            if(!await Task.Run(()=>EmbeddedHostAlive(marker)))return false;
        }
        OpenEmbedded(plan);
        if(windowMode==MainWindowMode.Workbench){AttachWorkbenchPane(plan);RelayoutWorkbench();}
        return true;
    }
    // 重新打开必须核对记录的宿主仍在：只看文件存在会把已死或已崩溃的会话当成可重开；PID 复用不算存活，启动时间（FILETIME UTC）必须一致。启动时间读不出时保持可重开，方向与 Worker 的 ProcessAlive 相同。
    private static bool EmbeddedHostAlive(string marker)
    {
        try
        {
            var recorded=Store.Read<EmbeddedMarker>(marker);
            if(recorded is not{Pid:>0,ProcessTime:>0})return false;
            using var process=Process.GetProcessById(recorded.Pid);
            return !process.HasExited&&process.StartTime.ToUniversalTime().ToFileTimeUtc()==recorded.ProcessTime;
        }
        catch(ArgumentException){return false;}
        catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){return true;}
        catch{return false;}
    }

    private void OpenEmbedded(LaunchItem plan,bool connect=true)
    {
        EmbeddedPanel.Visibility=ShowTerminalsButton.Visibility=Visibility.Collapsed;
        if(embeddedViews.TryGetValue(plan.Id,out var existing))
        {
            if(windowMode==MainWindowMode.Workbench)AttachWorkbenchPane(plan);
            existing.Card.Focus();
            if(existing.Finished)_=ConnectEmbeddedAsync(plan,existing);
            return;
        }
        var card=new TerminalCard{HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
        AutomationProperties.SetName(card,plan.Name+" 终端");
        var statusText=new TextBlock{Text="正在连接…",Margin=new Thickness(10,3,10,3)};
        statusText.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        var dock=new DockPanel();dock.SetResourceReference(DockPanel.BackgroundProperty,"Surface");DockPanel.SetDock(statusText,Dock.Bottom);dock.Children.Add(statusText);
        var toolbar=new WrapPanel{Margin=new Thickness(6,3,6,3)};DockPanel.SetDock(toolbar,Dock.Top);dock.Children.Add(toolbar);
        Button Tool(string title,string help,Action action)
        {
            var button=new Button{Content=title,ToolTip=help,Margin=new Thickness(2),Padding=new Thickness(8,3,8,3)};
            AutomationProperties.SetName(button,title);button.Click+=(_,_)=>action();toolbar.Children.Add(button);return button;
        }
        var searchBar=new DockPanel{Visibility=Visibility.Collapsed,Margin=new Thickness(8,2,8,5)};
        DockPanel.SetDock(searchBar,Dock.Top);dock.Children.Add(searchBar);
        var searchBox=new TextBox{MaxLength=256,MinWidth=100,ToolTip="搜索保留的输出（最多 2000 行 / 2 MiB），Enter 下一处，Shift+Enter 上一处"};
        AutomationProperties.SetName(searchBox,"搜索终端输出");
        foreach(var direction in new[]{-1,1})
        {
            var next=new Button{Content=direction<0?"上一处":"下一处",Margin=new Thickness(3,0,0,0),Padding=new Thickness(7,2,7,2)};
            DockPanel.SetDock(next,Dock.Right);next.Click+=(_,_)=>card.Find(searchBox.Text,direction<0);searchBar.Children.Add(next);
        }
        var closeSearch=new Button{Content="关闭搜索",Margin=new Thickness(3,0,0,0),Padding=new Thickness(7,2,7,2)};
        DockPanel.SetDock(closeSearch,Dock.Right);closeSearch.Click+=(_,_)=>{searchBar.Visibility=Visibility.Collapsed;card.Focus();};searchBar.Children.Add(closeSearch);searchBar.Children.Add(searchBox);
        searchBox.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Enter){e.Handled=true;card.Find(searchBox.Text,Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));}
            else if(e.Key==Key.Escape){e.Handled=true;searchBar.Visibility=Visibility.Collapsed;card.Focus();}
        };
        void Search(){searchBar.Visibility=Visibility.Visible;searchBox.Focus();searchBox.SelectAll();}
        void Copy()
        {
            try{if(card.HasSelection){Clipboard.SetText(card.SelectionText);statusText.Text="已复制所选输出。";}else statusText.Text="拖动选择输出，再按 Ctrl+C 或 Ctrl+Shift+C 复制。";}
            catch(System.Runtime.InteropServices.COMException){statusText.Text="剪贴板忙，请重试。";}
        }
        void Paste()
        {
            try
            {
                var text=Clipboard.GetText();var confirmed=false;
                if(TerminalInput.RequiresPasteConfirmation(text,card.Applied?.BracketedPaste==true))
                {
                    var preview=System.Text.Encoding.UTF8.GetString(TerminalInput.Paste(text,false,true)!);
                    if(preview.Length>160)preview=preview[..160]+"…";
                    confirmed=MessageBox.Show(this,"当前程序未启用括号粘贴。多行内容中的换行可能立即执行命令。确认一次发送这些内容？\n\n"+preview,
                        "确认多行粘贴",MessageBoxButton.OKCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel)==MessageBoxResult.OK;
                    if(!confirmed){statusText.Text="已取消多行粘贴，未发送输入。";return;}
                }
                if(!card.Paste(text,confirmed))statusText.Text=card.RejectedPaste;
                card.Focus();
            }
            catch(System.Runtime.InteropServices.COMException){statusText.Text="剪贴板忙，请重试。";}
        }
        Tool("复制","拖动选择后 Ctrl+C；无选择时 Ctrl+C 发送中断",Copy);
        Tool("粘贴","Ctrl+V；多行内容会按终端模式安全粘贴或先确认",Paste);
        Tool("搜索","Ctrl+F 搜索保留的终端输出",Search);
        Tool("回到最新","Esc 返回最新输出；Shift+PageUp/PageDown 回看",()=>{card.ReturnToLive();card.Focus();});
        var reconnect=Tool("重新连接","重新连接已有终端，不会新建或停止 Agent",()=>OpenEmbedded(plan));reconnect.IsEnabled=false;
        var historyText=new TextBlock{Margin=new Thickness(8,4,0,0)};historyText.SetResourceReference(TextBlock.ForegroundProperty,"Muted");toolbar.Children.Add(historyText);
        var scroll=new ScrollBar{Orientation=Orientation.Vertical,Width=14,Minimum=0,SmallChange=3};DockPanel.SetDock(scroll,Dock.Right);dock.Children.Add(scroll);dock.Children.Add(card);
        var updatingScroll=false;
        var searchStatusShown=false;
        card.ViewportChanged+=()=>
        {
            updatingScroll=true;scroll.Maximum=card.HistoryLines;scroll.ViewportSize=card.Applied?.Rows??4;scroll.LargeChange=scroll.ViewportSize;
            scroll.Value=Math.Clamp(card.TopLine-card.FirstLine,0,card.HistoryLines);updatingScroll=false;
            historyText.Text=card.IsReadingHistory?$"回看 · {card.HistoryLines} 行历史":"实时 · 滚轮回看";
            if(card.FirstLine>0)historyText.Text+=$" · 已截去 {card.FirstLine} 行";
            if(card.SearchStatus is {} searchStatus){statusText.Text=searchStatus;searchStatusShown=true;}
            else if(searchStatusShown)
            {
                statusText.Text=card.Applied?.ExitCode is {} exitCode?$"进程已结束（退出码 {exitCode}），可回看和搜索输出；关闭视图将释放输出。":
                    plan.RemoteExecution is {} remote?$"{remote.Host} · {remote.RemoteWorkingDirectory}":plan.Cwd;
                searchStatusShown=false;
            }
        };
        scroll.ValueChanged+=(_,_)=>{if(!updatingScroll)card.ScrollTo(card.FirstLine+(int)scroll.Value);};
        card.CopyRequested+=Copy;card.PasteRequested+=Paste;card.SearchRequested+=Search;
        card.NavigateTab+=direction=>{if(EmbeddedTabs.Items.Count>0)EmbeddedTabs.SelectedIndex=(EmbeddedTabs.SelectedIndex+direction+EmbeddedTabs.Items.Count)%EmbeddedTabs.Items.Count;};
        var menu=new ContextMenu();
        foreach(var command in new (string Label,Action Run)[]{("复制所选输出",Copy),("粘贴",Paste),("搜索输出",Search),("回到最新",card.ReturnToLive)})
        {var item=new MenuItem{Header=command.Label};item.Click+=(_,_)=>command.Run();menu.Items.Add(item);}
        card.ContextMenu=menu;
        var tab=new TabItem{Header=plan.Name,ToolTip=plan.RemoteExecution?.Host??plan.Cwd,Content=dock,Tag=plan.Id};
        tab.SetResourceReference(TabItem.ForegroundProperty,"Ink");
        tab.SetResourceReference(TabItem.BackgroundProperty,"RailSurface");
        var view=new PaneView{Tab=tab,Card=card,Status=statusText,Reconnect=reconnect};
        embeddedViews.Add(plan.Id,view);
        EmbeddedTabs.Items.Add(tab);EmbeddedTabs.SelectedItem=tab;
        card.Input+=async bytes=>
        {
            if(card.Applied?.ExitCode is {} exitCode){statusText.Text=$"进程已结束（退出码 {exitCode}），可回看输出。";return;}
            if(view.Client is not {} client)return;
            if(view.PendingBytes+bytes.Length>TerminalPipeClient.MaxInput){statusText.Text="输入尚在发送，请稍后再试。";return;}
            view.PendingBytes+=bytes.Length;
            try{await client.SendInputAsync(bytes,view.Stop.Token);}
            catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){statusText.Text="终端已断开，请重新连接。";client.Dispose();}
            finally{view.PendingBytes-=bytes.Length;}
        };
        TerminalReadRequest? pendingRead=null;
        var reading=false;
        card.ReadRequested+=async request=>
        {
            pendingRead=request;if(reading)return;reading=true;
            try
            {
                while(pendingRead is {} next)
                {
                    pendingRead=null;if(view.Client is not {} client)break;
                    await client.SendReadAsync(next,view.Stop.Token);
                }
            }
            catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){statusText.Text="读取历史失败，请重新连接。";view.Client?.Dispose();}
            finally{reading=false;}
        };
        var resize=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(120)};
        resize.Tick+=async(_,_)=>{resize.Stop();await ResizeEmbeddedAsync(view);};
        card.SizeChanged+=(_,_)=>{if(!view.Finished&&!view.Stop.IsCancellationRequested){resize.Stop();resize.Start();}};
        card.PreviewMouseDown+=(_,_)=>card.Focus();
        card.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.V&&Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled=true;
                try{if(!card.Paste(Clipboard.GetText()))statusText.Text=card.RejectedPaste;}
                catch(System.Runtime.InteropServices.COMException){statusText.Text="剪贴板忙，请重试。";}
            }
        };
        if(connect)_=ConnectEmbeddedAsync(plan,view);
    }

    private static async Task ResizeEmbeddedAsync(PaneView view)
    {
        if(view.Client is not {} client || !view.Card.IsVisible)return;
        var columns=Math.Clamp((int)(view.Card.ActualWidth/TerminalCard.CellWidth),TerminalScreen.MinColumns,TerminalScreen.MaxColumns);
        var rows=Math.Clamp((int)(view.Card.ActualHeight/TerminalCard.CellHeight),TerminalScreen.MinRows,TerminalScreen.MaxRows);
        try{await client.SendResizeAsync(columns,rows,view.Stop.Token);}
        catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){}
    }

    private async Task ConnectEmbeddedAsync(LaunchItem plan,PaneView view)
    {
        view.Finished=false;view.Reconnect.IsEnabled=false;view.Status.Text="正在连接…";
        try
        {
            using var client=await TerminalPipeClient.ConnectAsync(plan.Id,TimeSpan.FromSeconds(5),view.Stop.Token);
            view.Client=client;
            view.Status.Text=plan.RemoteExecution is {} remote?$"{remote.Host} · {remote.RemoteWorkingDirectory}":plan.Cwd;
            if(EmbeddedTabs.SelectedItem==view.Tab)view.Card.Focus();
            await ResizeEmbeddedAsync(view);
            view.Card.ReturnToLive();
            await client.PumpAsync(snapshot=>Dispatcher.InvokeAsync(()=>
                {
                    view.Card.Show(snapshot);
                    if(snapshot.ExitCode is {} exitCode)view.Status.Text=$"进程已结束（退出码 {exitCode}），可回看和搜索输出；关闭视图将释放输出。";
                }).Task,view.Stop.Token,
                page=>Dispatcher.InvokeAsync(()=>{view.Card.ShowReadback(page);}).Task);
            view.Status.Text="终端连接已结束，可尝试重新连接。";
        }
        catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {view.Status.Text="终端连接已结束或无法建立。"+e.Message;}
        finally{view.Client=null;view.Finished=true;view.Reconnect.IsEnabled=!view.Stop.IsCancellationRequested;}
    }

    private void ShowEmbeddedClick(object sender,RoutedEventArgs e){EmbeddedPanel.Visibility=Visibility.Visible;if(EmbeddedTabs.SelectedItem is TabItem {Tag:Guid id})embeddedViews[id].Card.Focus();}
    private void HideEmbeddedClick(object sender,RoutedEventArgs e)=>EmbeddedPanel.Visibility=Visibility.Collapsed;
    private void EmbeddedTabChanged(object sender,SelectionChangedEventArgs e)
    {
        if(EmbeddedTabs?.SelectedItem is TabItem {Tag:Guid id}&&embeddedViews.TryGetValue(id,out var view))Dispatcher.BeginInvoke(()=>view.Card.Focus(),DispatcherPriority.Input);
    }
    private void CloseEmbeddedClick(object sender,RoutedEventArgs e){if(EmbeddedTabs.SelectedItem is TabItem {Tag:Guid id})RemoveEmbedded(id);}
    private void RemoveEmbedded(Guid id)
    {
        if(!embeddedViews.Remove(id,out var view))return;
        view.Stop.Cancel();view.Client?.Dispose();EmbeddedTabs.Items.Remove(view.Tab);
        DetachWorkbenchPane(id);
        EmbeddedPanel.Visibility=ShowTerminalsButton.Visibility=Visibility.Collapsed;
    }
    protected override void OnClosed(EventArgs e)
    {
        foreach(var id in embeddedViews.Keys.ToArray())RemoveEmbedded(id);
        base.OnClosed(e);
    }
}
