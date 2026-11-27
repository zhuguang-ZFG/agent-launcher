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
        ProgressText.Text="未找到该批次的嵌入式终端；窗口可能已关闭，或属于旧版启动记录。";
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
