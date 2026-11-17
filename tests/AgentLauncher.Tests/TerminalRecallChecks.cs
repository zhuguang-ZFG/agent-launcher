using System.Runtime.InteropServices;
using System.Text;

internal static class TerminalRecallChecks
{
    internal static bool HasWindow(string title)
    {
        var found=false;
        EnumWindows((window,_)=>{var text=new StringBuilder(512);GetWindowText(window,text,text.Capacity);if(text.ToString()==title)found=true;return true;},IntPtr.Zero);
        return found;
    }
    private delegate bool Callback(IntPtr window,IntPtr argument);
    [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,IntPtr argument);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
}
