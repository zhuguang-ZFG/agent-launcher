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

    public static void AddPaneArguments(ProcessStartInfo start,LaunchItem item,bool useSystemColors)=>AddPaneArguments(start.ArgumentList,item,useSystemColors);

    internal static void AddPaneArguments(ICollection<string> arguments,LaunchItem item,bool useSystemColors)
    {
        // Command-line overrides affect only this pane. Do not install fragments or rewrite the user's Terminal settings.
        arguments.Add("--title");
        var folder=Path.GetFileName(item.Cwd.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        arguments.Add($"{item.Position:00}  {DisplayText(item.Name,40)} · {DisplayText(folder,40)}".Replace(";","\\;"));
        arguments.Add("--suppressApplicationTitle");
        arguments.Add("--startingDirectory");arguments.Add(item.Cwd.Replace(";","\\;"));
        if(!useSystemColors)
        {
            arguments.Add("--colorScheme");arguments.Add("One Half Dark");
            arguments.Add("--tabColor");arguments.Add(TabColor(item));
        }
    }

    private static string TabColor(LaunchItem item)
    {
        var id=item.AgentId;
        if(string.IsNullOrEmpty(id))
        {
            foreach(var agent in Agents.All)
                if(StringComparer.OrdinalIgnoreCase.Equals(agent.Name,item.Name)||StringComparer.OrdinalIgnoreCase.Equals(agent.Id,item.Name)){id=agent.Id;break;}
        }
        return id switch
        {
            "pi"=>"#61AFEF","codex"=>"#E5C07B","claude"=>"#C678DD","gemini"=>"#56B6C2","opencode"=>"#98C379",
            "hermes"=>"#E06C75","omp"=>"#D19A66","kimi"=>"#BE5046","qoder"=>"#528BFF",_=>"#ABB2BF"
        };
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
