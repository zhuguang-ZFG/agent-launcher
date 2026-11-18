using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

// Terminal window recall: each launched batch lives in one Windows Terminal window whose title follows
// the focused pane's --title ("NN  名称 · 目录", composed by TerminalAppearance.AddPaneArguments).
public partial class MainWindow
{
    private async void FocusTerminalWindow(Guid batchId,Guid? attemptId=null)=>await GuardAsync(async()=>
    {
        var plans=(status?.Entries??[]).Where(entry=>entry.Plan.BatchId==batchId).Select(entry=>entry.Plan).ToArray();
        var selected=plans.FirstOrDefault(p=>p.Id==attemptId)??plans.FirstOrDefault();
        if(selected!=null&&await TryShowEmbeddedAsync(selected))return;
        var titles=plans.Select(TerminalAppearance.PaneTitle).Distinct().ToArray();
        var handle=titles.Length==0?IntPtr.Zero:TerminalWindowFinder.Find(titles);
        if(handle==IntPtr.Zero){ProgressText.Text="未找到该批次的唯一终端窗口；窗口可能已关闭、标题已更改，或属于旧版启动记录。请从任务栏选择终端。";return;}
        var target=plans.SingleOrDefault(plan=>plan.Id==attemptId);
        if(target is not null)
        {
            var reference=await Task.Run(()=>Store.Read<TerminalPaneReference>(System.IO.Path.Combine(Store.Root,"attempts",target.Id.ToString("N"),"terminal.json")));
            if(reference is not null&&reference.AttemptId==target.Id&&reference.BatchId==batchId&&reference.PaneId is >=0 and <16)
            {
                var terminal=await Task.Run(()=>Commands.Terminal(config.TerminalPath));
                if(TerminalWindowFinder.Find(titles)!=handle){ProgressText.Text="终端窗口已变化，请重试。";return;}
                var start=Commands.TerminalFocusStartInfo(terminal,reference);
                using var process=Process.Start(start)??throw new System.IO.IOException("无法请求切换窗格。");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){ProgressText.Text="已请求切换窗格，但终端尚未确认，请从任务栏核对。";return;}
                for(var i=0;i<10&&TerminalWindowFinder.Find([TerminalAppearance.PaneTitle(target)])!=handle;i++)await Task.Delay(50);
                if(process.ExitCode!=0||TerminalWindowFinder.Find([TerminalAppearance.PaneTitle(target)])!=handle){TerminalWindowFinder.Focus(handle);ProgressText.Text="已找到批次窗口，但未确认目标窗格；窗格可能已关闭或移至其他标签页。";return;}
            }
            else{TerminalWindowFinder.Focus(handle);ProgressText.Text="已找到批次窗口，当前记录没有可核对的窗格编号，请手动选择窗格。";return;}
        }
        ProgressText.Text=TerminalWindowFinder.Focus(handle)?"已定位并切到对应终端。":"已找到对应终端，但 Windows 未允许切到前台，请点击任务栏。";
    });
}

internal static class TerminalWindowFinder
{
    public static IntPtr Find(IReadOnlyList<string> titles)
    {
        var found=IntPtr.Zero;var count=0;
        _ = EnumWindows((handle,_) =>
        {
            if(!IsWindowVisible(handle))return true;
            var length=GetWindowTextLength(handle);
            if(length==0)return true;
            var text=new StringBuilder(length+1);
            _ = GetWindowText(handle,text,text.Capacity);
            var title=text.ToString();
            foreach(var expected in titles)
                if(string.Equals(title,expected,StringComparison.Ordinal)&&IsTerminalProcess(handle)){found=handle;count++;break;}
            return true;
        },IntPtr.Zero);
        return count==1?found:IntPtr.Zero;
    }

    public static bool Focus(IntPtr handle)
    {
        _ = ShowWindow(handle,9); // SW_RESTORE: un-minimize without changing size otherwise.
        return SetForegroundWindow(handle)&&GetForegroundWindow()==handle;
    }

    private static bool IsTerminalProcess(IntPtr handle)
    {
        try
        {
            _ = GetWindowThreadProcessId(handle,out var pid);
            using var process=Process.GetProcessById((int)pid);
            return process.ProcessName.StartsWith("WindowsTerminal",StringComparison.OrdinalIgnoreCase);
        }
        catch(ArgumentException){return false;} // The window's process exited between enumeration and query.
        catch(InvalidOperationException){return false;}
    }

    private delegate bool EnumWindowsProc(IntPtr handle,IntPtr parameter);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumWindows(EnumWindowsProc callback,IntPtr parameter);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr handle,StringBuilder text,int capacity);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint processId);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ShowWindow(IntPtr handle,int command);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
}
