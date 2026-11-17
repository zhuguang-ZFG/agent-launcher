using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    // Rail summaries reuse the ledger snapshot and task book that RenderSessions/RenderTaskCards already render:
    // no extra I/O, no sampling, no timers; rows are rebuilt inside the existing Render() cycle and stay bounded.
    private const int RailSummaryRows = 3;

    partial void RenderRail()
    {
        var muted = (Brush)FindResource("Muted");
        RenderRailSessions(muted);
        RenderRailQueue(muted);
        RenderRailResource();
    }

    private void RenderRailSessions(Brush muted)
    {
        RailSessions.Children.Clear();
        var active = (status?.Entries ?? []).Where(entry => entry.Plan.Kind == "agent" && !Store.IsTerminal(entry)).OrderByDescending(entry => entry.Created).ToArray();
        foreach (var entry in active.Take(RailSummaryRows))
        {
            var remote = entry.Plan.RemoteExecution is { } spec ? " · ☁ VPS " + (config.RemoteHosts.FirstOrDefault(h => h.Address == spec.Host)?.Name ?? spec.Host) : "";
            var text = $"{entry.Plan.Position:00} {entry.Plan.Name} · {RailSessionState(entry)}{remote}";
            RailSessions.Children.Add(RailRecordButton(text, text + "\n查看这条运行记录", "查看会话 " + entry.Plan.Name, () => NavigateToSession(entry.Plan.Id)));
        }
        if (active.Length == 0) RailSessions.Children.Add(new TextBlock { Text = "无活动会话", FontSize = 12, Foreground = muted, Margin = new Thickness(0, 2, 0, 2) });
        if (active.Length > RailSummaryRows) RailSessions.Children.Add(RailMoreButton("查看全部会话", () => JumpTo(SessionExpander)));
    }

    private void RenderRailQueue(Brush muted)
    {
        RailQueue.Children.Clear();
        var book = taskBook;
        QueueEntry[] waiting = book is null ? [] : TaskUsability.OrderedQueue(book);
        var rank = 0;
        foreach (var entry in waiting.Take(RailSummaryRows))
        {
            var task = book!.Tasks.FirstOrDefault(candidate => candidate.Id == entry.TaskId);
            if (task is null) continue;
            rank++;
            var text = $"{rank:00} {task.Name} · {(entry.Deferred ? "已暂缓" : !InQueueScope(task.Id) ? "范围外" : !queueEnabled ? "已暂停" : "等待调度")}";
            RailQueue.Children.Add(RailRecordButton(text, entry.Detail.Length > 0 ? $"{text}\n{entry.Detail}" : text, "查看等待项 " + task.Name, () => NavigateToQueueEntry(entry.Id)));
        }
        if (waiting.Length == 0) RailQueue.Children.Add(new TextBlock { Text = "队列为空", FontSize = 12, Foreground = muted, Margin = new Thickness(0, 2, 0, 2) });
        if (waiting.Length > RailSummaryRows) RailQueue.Children.Add(RailMoreButton("查看全部队列", () => JumpTo(TaskExpander)));
    }

    // Expanding alone leaves the section below the fold; scroll it into view once the new content has been arranged.
    private void JumpTo(FrameworkElement section)
    {
        if (section is Expander expander) expander.IsExpanded = true;
        Dispatcher.BeginInvoke(new Action(() => section.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RenderRailResource()
    {
        // Same cached snapshot LoadResourceViewAsync/ShowResourceSettingsAsync read; a render pass never samples or calls the monitor.
        RailResourceText.Text = status?.Memory is { } sample
            ? $"{RailResourceMode(config.Draft.Resource?.Mode)} · 可用 {GiB(sample.Available)} GiB"
            : "资源：未读取 · 打开设置";
        RailResourceText.ToolTip = RailResourceText.Text;
    }

    private Button RailMoreButton(string name, Action open)
    {
        var button = new Button { Content = "全部…", Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(6, 2, 6, 2) };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => open();
        return button;
    }
    private Button RailRecordButton(string text, string tooltip, string name, Action open)
    {
        var label = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        var button = new Button { Content = label, ToolTip = tooltip, Style = (Style)FindResource("QuietButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 2) };
        AutomationProperties.SetName(button, name); button.Click += (_, _) => open(); return button;
    }

    // Active attempts report "Started"; the rail presents them as running, other states reuse the shared label table.
    private static string RailSessionState(ControlEntry entry) => entry.State is "Started" or "Running" ? "运行中" : TaskUsability.StateLabel(entry.State);

    private static string RailResourceMode(string? mode) => mode switch { "conservative" => "保守", "custom" => "自定义", _ => "实用" };
}
