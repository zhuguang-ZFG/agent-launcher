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

    public static void SubmitTerminal(string terminal,IReadOnlyList<LaunchItem> items,string layout,CancellationToken cancellationToken=default)
    {
        var arguments=ComposeTerminalArguments(items,layout,TerminalAppearance.UseSystemColors);
        _ = CreateTerminalStartInfo(terminal,arguments); // Keep the complete-batch limit and validate before opening any pane.
        var prefix=arguments.TakeWhile(argument=>argument!="new-tab").ToArray();var headerLength=prefix.Length;
        var fragments=new List<List<string>>();var pending=new List<string>(prefix);var command=new List<string>();
        foreach(var argument in arguments.Skip(headerLength).Append(";"))
        {
            if(argument!=";"){command.Add(argument);continue;}
            if(pending.Count>headerLength)pending.Add(";");pending.AddRange(command);
            if(command[0] is "new-tab" or "split-pane"){fragments.Add(pending);pending=[arguments[0],arguments[1]];headerLength=2;}
            command.Clear();
        }
        // The final focus belongs to the last creation command, while the window is known to exist.
        if(pending.Count>2){fragments[^1].Add(";");fragments[^1].AddRange(pending.Skip(2));}
        if(fragments.Count!=items.Count)throw new IOException("窗格提交计划与启动项数量不一致，未提交。");
        var order=PaneLayout.Map(items.Count,layout).OrderBy(cell=>cell.CreationOrder).Select(cell=>items[cell.EnabledIndex]).ToArray();
        if(order.Any(item=>File.Exists(Path.Combine(Store.Root,"attempts",item.Id.ToString("N"),"claim"))))throw new IOException("本批次已有窗格领取记录，请核对运行记录，不要重复提交。");
        for(var index=0;index<fragments.Count;index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process=Process.Start(CreateTerminalStartInfo(terminal,fragments[index]))??throw new IOException("无法提交给 Windows Terminal。");
            var claim=Path.Combine(Store.Root,"attempts",order[index].Id.ToString("N"),"claim");var deadline=Stopwatch.StartNew();
            // A pane command is asynchronous in Terminal. Wait for its Worker before sending the next dependent focus/split.
            // Never resend a command whose outcome is unknown: a late Worker must still use its original one-shot claim.
            while(!File.Exists(claim))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(deadline.Elapsed>TimeSpan.FromSeconds(8))throw new IOException($"第 {order[index].Position} 项的窗格尚未确认领取，已停止提交后续窗格。请核对运行记录，勿重复启动。");
                Thread.Sleep(20);
            }
        }
    }

    private static ProcessStartInfo CreateTerminalStartInfo(string terminal,IReadOnlyList<string> arguments)
    {
        long length=QuotedLength(terminal);foreach(var argument in arguments)length+=1L+QuotedLength(argument);
        if(length>ResourcePolicy.CommandLineLimit)throw new IOException($"Windows Terminal 命令行超过 {ResourcePolicy.CommandLineLimit} 字符，请减少窗格或缩短工作目录、安装路径；未提交任何窗格。");
        var start=new ProcessStartInfo(terminal){UseShellExecute=false};foreach(var argument in arguments)start.ArgumentList.Add(argument);return start;
    }
}
