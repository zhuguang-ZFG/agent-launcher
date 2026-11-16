using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentLauncher.Core;

namespace AgentLauncher.App;

// Terminal window recall: each launched batch lives in one Windows Terminal window whose title follows
// the focused pane's --title ("NN  名称 · 目录", composed by TerminalAppearance.AddPaneArguments).
public partial class MainWindow
{
    private void FocusTerminalWindow(Guid batchId)
    {
        // Titles derive from position+name+folder, so two batches configured identically share every title;
        // EnumWindows then focuses the first match. Accepted limitation: the recall target is ambiguous by content.
        var titles=(status?.Entries??[]).Where(entry=>entry.Plan.BatchId==batchId).Select(entry=>PaneTitle(entry.Plan)).Distinct().ToArray();
        var handle=titles.Length==0?IntPtr.Zero:TerminalWindowFinder.Find(titles);
        if(handle==IntPtr.Zero){ProgressText.Text="未找到该批次的分屏终端窗口；窗口可能已关闭，运行记录中的工作目录仍然可用。";return;}
        TerminalWindowFinder.Focus(handle);
        ProgressText.Text="已把该批次的分屏终端窗口切到前台。";
    }

    // Mirror of TerminalAppearance's title contract (Windows assembly, private there); keep the format in sync.
    private static string PaneTitle(LaunchItem item)
    {
        var folder=System.IO.Path.GetFileName(item.Cwd.TrimEnd(System.IO.Path.DirectorySeparatorChar,System.IO.Path.AltDirectorySeparatorChar));
        return $"{item.Position:00}  {DisplayText(item.Name,40)} · {DisplayText(folder,40)}";
    }

    private static string DisplayText(string value,int maximum)
    {
        var text=new StringBuilder(Math.Min(value.Length,maximum));
        foreach(var ch in value)
        {
            if(char.IsControl(ch))continue;
            if(text.Length==maximum){text.Append('…');break;}
            text.Append(ch);
        }
        return text.ToString();
    }
}

internal static class TerminalWindowFinder
{
    public static IntPtr Find(IReadOnlyList<string> titles)
    {
        var found=IntPtr.Zero;
        _ = EnumWindows((handle,_) =>
        {
            if(!IsWindowVisible(handle))return true;
            var length=GetWindowTextLength(handle);
            if(length==0)return true;
            var text=new StringBuilder(length+1);
            _ = GetWindowText(handle,text,text.Capacity);
            var title=text.ToString();
            foreach(var expected in titles)
                if(string.Equals(title,expected,StringComparison.Ordinal)&&IsTerminalProcess(handle)){found=handle;return false;}
            return true;
        },IntPtr.Zero);
        return found;
    }

    public static void Focus(IntPtr handle)
    {
        _ = ShowWindow(handle,9); // SW_RESTORE: un-minimize without changing size otherwise.
        _ = SetForegroundWindow(handle);
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
}
