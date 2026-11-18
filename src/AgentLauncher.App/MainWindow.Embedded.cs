using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        OpenEmbedded(plan);return true;
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

    private void OpenEmbedded(LaunchItem plan)
    {
        EmbeddedPanel.Visibility=ShowTerminalsButton.Visibility=Visibility.Visible;
        if(embeddedViews.TryGetValue(plan.Id,out var existing)){EmbeddedTabs.SelectedItem=existing.Tab;existing.Card.Focus();return;}
        var card=new TerminalCard{HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
        AutomationProperties.SetName(card,plan.Name+" 终端");
        var statusText=new TextBlock{Text="正在连接…",Margin=new Thickness(10,3,10,3)};
        statusText.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        var dock=new DockPanel();dock.SetResourceReference(DockPanel.BackgroundProperty,"Surface");DockPanel.SetDock(statusText,Dock.Bottom);dock.Children.Add(statusText);dock.Children.Add(card);
        var tab=new TabItem{Header=plan.Name,ToolTip=plan.RemoteExecution?.Host??plan.Cwd,Content=dock,Tag=plan.Id};
        tab.SetResourceReference(TabItem.ForegroundProperty,"Ink");
        tab.SetResourceReference(TabItem.BackgroundProperty,"RailSurface");
        var view=new PaneView{Tab=tab,Card=card,Status=statusText};
        embeddedViews.Add(plan.Id,view);
        EmbeddedTabs.Items.Add(tab);EmbeddedTabs.SelectedItem=tab;
        card.Input+=async bytes=>
        {
            if(view.Client is not {} client)return;
            if(view.PendingBytes+bytes.Length>TerminalPipeClient.MaxInput){statusText.Text="输入尚在发送，请稍后再试。";return;}
            view.PendingBytes+=bytes.Length;
            try{await client.SendInputAsync(bytes,view.Stop.Token);}
            catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){statusText.Text="终端已断开。";}
            finally{view.PendingBytes-=bytes.Length;}
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
        _=ConnectEmbeddedAsync(plan,view);
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
        try
        {
            using var client=await TerminalPipeClient.ConnectAsync(plan.Id,TimeSpan.FromSeconds(5),view.Stop.Token);
            view.Client=client;
            view.Status.Text=plan.RemoteExecution is {} remote?$"{remote.Host} · {remote.RemoteWorkingDirectory}":plan.Cwd;
            if(EmbeddedTabs.SelectedItem==view.Tab)view.Card.Focus();
            await ResizeEmbeddedAsync(view);
            await client.PumpAsync(snapshot=>Dispatcher.InvokeAsync(()=>{view.Card.Show(snapshot);}).Task,view.Stop.Token);
            view.Status.Text="终端连接已结束。";
        }
        catch(Exception e)when(e is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {view.Status.Text="终端连接已结束或无法建立。"+e.Message;}
        finally{view.Client=null;view.Finished=true;}
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
        if(EmbeddedTabs.Items.Count==0)EmbeddedPanel.Visibility=ShowTerminalsButton.Visibility=Visibility.Collapsed;
    }
    protected override void OnClosed(EventArgs e)
    {
        foreach(var id in embeddedViews.Keys.ToArray())RemoveEmbedded(id);
        base.OnClosed(e);
    }
}
