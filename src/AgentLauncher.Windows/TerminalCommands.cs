using System.Diagnostics;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public static partial class Commands
{
    public static IReadOnlyList<string> ComposeTerminalArguments(IReadOnlyList<LaunchItem> items,string layout,bool useSystemColors=false)
    {
        ArgumentNullException.ThrowIfNull(items);
        var map=PaneLayout.Map(items.Count,layout);var enabledByCreation=new int[items.Count];
        foreach(var cell in map)enabledByCreation[cell.CreationOrder]=cell.EnabledIndex;
        var arguments=new List<string>(32+items.Count*32){"-w","agent-"+items[0].BatchId.ToString("N")};
        if(items.Count>1)arguments.Add("--maximized");
        var worker=Store.WorkerPath.Replace(";","\\;");var creation=0;var panePending=false;
        foreach(var step in PaneLayout.SplitSteps(items.Count,layout))
        {
            if(step==";"&&panePending)
            {
                var item=items[enabledByCreation[creation++]];TerminalAppearance.AddPaneArguments(arguments,item,useSystemColors);
                arguments.Add(worker);arguments.Add("pane");arguments.Add(item.Id.ToString("D"));panePending=false;
            }
            arguments.Add(step);if(step is "new-tab" or "split-pane")panePending=true;
        }
        return arguments;
    }

    public static int QuotedLength(string value)
    {
        var length=checked(value.Length+2);var slashes=0;
        // Count the same always-quoted Windows argv encoding as ProtectedProcess.Quote without allocating it.
        foreach(var ch in value)
        {
            if(ch=='\\'){slashes++;continue;}
            if(ch=='\0')throw new ArgumentException("参数不能含 NUL。");
            if(ch=='"')length=checked(length+slashes+1);slashes=0;
        }
        return checked(length+slashes);
    }

    public static void SubmitTerminal(string terminal,IReadOnlyList<LaunchItem> items,string layout)
    {
        var arguments=ComposeTerminalArguments(items,layout,TerminalAppearance.UseSystemColors);
        using var process=Process.Start(CreateTerminalStartInfo(terminal,arguments))??throw new IOException("无法提交给 Windows Terminal。");
    }

    private static ProcessStartInfo CreateTerminalStartInfo(string terminal,IReadOnlyList<string> arguments)
    {
        long length=QuotedLength(terminal);foreach(var argument in arguments)length+=1L+QuotedLength(argument);
        if(length>ResourcePolicy.CommandLineLimit)throw new IOException($"Windows Terminal 命令行超过 {ResourcePolicy.CommandLineLimit} 字符，请减少窗格或缩短工作目录、安装路径；未提交任何窗格。");
        var start=new ProcessStartInfo(terminal){UseShellExecute=false};foreach(var argument in arguments)start.ArgumentList.Add(argument);return start;
    }
}
