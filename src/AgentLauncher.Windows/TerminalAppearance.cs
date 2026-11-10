using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public static class TerminalAppearance
{
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="SystemParametersInfoW")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action,uint size,ref HighContrast settings,uint flags);

    public static bool UseSystemColors
    {
        get
        {
            var settings=new HighContrast{Size=(uint)Marshal.SizeOf<HighContrast>()};
            return !SystemParametersInfo(0x0042,settings.Size,ref settings,0)||(settings.Flags&1)!=0;
        }
    }

    public static void AddPaneArguments(ProcessStartInfo start,LaunchItem item,bool useSystemColors)
    {
        // Command-line overrides affect only this pane. Do not install fragments or rewrite the user's Terminal settings.
        start.ArgumentList.Add("--title");
        var folder=Path.GetFileName(item.Cwd.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        start.ArgumentList.Add($"{item.Position:00}  {DisplayText(item.Name,40)} · {DisplayText(folder,40)}".Replace(";","\\;"));
        start.ArgumentList.Add("--suppressApplicationTitle");
        start.ArgumentList.Add("--startingDirectory");start.ArgumentList.Add(item.Cwd.Replace(";","\\;"));
        if(!useSystemColors)
        {
            start.ArgumentList.Add("--colorScheme");start.ArgumentList.Add("One Half Dark");
            start.ArgumentList.Add("--tabColor");start.ArgumentList.Add("#61AFEF");
        }
    }

    public static void WritePaneHeading(LaunchItem item)
    {
        Console.OutputEncoding=new UTF8Encoding(false);
        var color=!Console.IsOutputRedirected&&!UseSystemColors;
        Console.WriteLine();
        if(color)Console.ForegroundColor=ConsoleColor.Cyan;
        Console.WriteLine($"  {item.Position:00}  {DisplayText(item.Name,80)}");
        if(color)Console.ResetColor();
        Console.WriteLine($"  {DisplayText(item.Cwd,180)}");
        Console.WriteLine("  正在加载 Agent · 已启用资源保护");
        Console.WriteLine("  ────────────────────────────────────────");
        Console.WriteLine();
        // Never redraw a dashboard over the Agent's terminal UI or consume its input.
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
