using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;
using Microsoft.Win32;

namespace AgentLauncher.App;

public sealed class TaskHistoryWindow : Window
{
    private readonly TaskHistorySnapshot snapshot;
    private readonly ListBox list = new() { MinHeight = 90, DisplayMemberPath = "Label" };
    private readonly TextBox search = new();
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button review = new() { Content = "查看本次改动", IsEnabled = false };
    private readonly List<Button> diagnosticActions = [];
    private long selectionRevision;
    private sealed record Row(TaskHistoryItem Item)
    {
        public string Label => $"{(Item.At is { } at ? at.ToLocalTime().ToString("MM-dd HH:mm") : "时间待核对")} · {Item.Name} · {TaskUsability.StateLabel(Item.State)} · {Item.AttemptId.ToString("N")[..8]}";
    }

    public TaskHistoryWindow(Window owner, TaskHistorySnapshot snapshot)
    {
        this.snapshot = snapshot; Owner = owner; Title = "任务历史与诊断";
        Width = Math.Min(860, SystemParameters.WorkArea.Width - 32); Height = Math.Min(720, SystemParameters.WorkArea.Height - 32); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.Resources[typeof(Window)];
        var root = new DockPanel { Margin = new Thickness(20) };
        notice.Text = $"已保留 {snapshot.Items.Length} 条记录，可按任务名称、状态或尝试编号搜索。" + (snapshot.Notice.Length > 0 ? "\n" + snapshot.Notice : "");
        DockPanel.SetDock(notice, Dock.Top); root.Children.Add(notice);
        AutomationProperties.SetName(search, "搜索历史任务、状态或尝试编号"); DockPanel.SetDock(search, Dock.Top); search.Margin = new Thickness(0, 0, 0, 8); root.Children.Add(search);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        Add("导出当前筛选列表", async () =>
        {
            var picker = new SaveFileDialog { Filter = "JSON|*.json", FileName = "agent-launcher-history.json" };
            if (picker.ShowDialog(this) == true)
                await File.WriteAllTextAsync(picker.FileName, JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Records = Filtered().Select(item => new { item.AttemptId, item.TaskId, item.Name, item.State, item.At }), Notice = "任务状态与进程状态分别记录；缺失历史不表示已完成。" }, new JsonSerializerOptions { WriteIndented = true }));
        });
        diagnosticActions.Add(Add("复制本条诊断", () => { Clipboard.SetText(detail.Text); notice.Text = "已复制当前显示的诊断；其中可能包含工作目录。"; return Task.CompletedTask; }));
        diagnosticActions.Add(Add("导出本条诊断", async () =>
        {
            var text = detail.Text;
            var picker = new SaveFileDialog { Filter = "JSON|*.json", FileName = "agent-launcher-attempt.json" };
            if (picker.ShowDialog(this) == true) await File.WriteAllTextAsync(picker.FileName, text);
        }));
        actions.Children.Add(review);
        review.Click += (_, _) =>
        {
            if (list.SelectedItem is Row { Item.Run: { } run } && snapshot.Book?.Tasks.FirstOrDefault(task => task.Id == run.TaskId) is { } task)
                new TaskReviewWindow(this, task, run).Show();
        };
        Add("打开记录目录", () => { Directory.CreateDirectory(Store.Root); Process.Start(new ProcessStartInfo(Store.Root) { UseShellExecute = true }); return Task.CompletedTask; });
        var close = new Button { Content = "关闭", IsCancel = true }; close.Click += (_, _) => Close(); actions.Children.Add(close);
        var body = new Grid(); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(list, "全部保留的尝试记录"); AutomationProperties.SetName(detail, "选中尝试的诊断预览");
        body.Children.Add(list); Grid.SetRow(detail, 1); detail.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(detail); root.Children.Add(body);
        Content = root; UiScale.Apply(this);
        search.TextChanged += (_, _) => Render(); list.SelectionChanged += async (_, _) => await LoadSelectionAsync(); Render();

        Button Add(string text, Func<Task> action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 4) };
            button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } catch (Exception e) when (e is not OutOfMemoryException) { notice.Text = e.Message; } finally { button.IsEnabled = true; } };
            actions.Children.Add(button);
            return button;
        }
    }

    private IEnumerable<TaskHistoryItem> Filtered()
    {
        var query = search.Text.Trim();
        return snapshot.Items.Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.State.Contains(query, StringComparison.OrdinalIgnoreCase) || TaskUsability.StateLabel(item.State).Contains(query, StringComparison.OrdinalIgnoreCase) || item.AttemptId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) || item.AttemptId.ToString("N").Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void Render()
    {
        selectionRevision++; list.ItemsSource = Filtered().Select(item => new Row(item)).ToArray();
        detail.Text = list.Items.Count == 0 ? "没有匹配的历史记录。" : "选择一条记录，查看身份、回执和退出信息。";
        review.IsEnabled = false;
        foreach (var action in diagnosticActions) action.IsEnabled = false;
    }

    private async Task LoadSelectionAsync()
    {
        var revision = ++selectionRevision;
        review.IsEnabled = false;
        foreach (var action in diagnosticActions) action.IsEnabled = false;
        if (list.SelectedItem is not Row row) return;
        detail.Text = "正在读取并核对本条历史…";
        try
        {
            var text = await Task.Run(() => TaskHistoryReader.Diagnostic(row.Item));
            if (revision != selectionRevision) return;
            detail.Text = text;
            foreach (var action in diagnosticActions) action.IsEnabled = true;
            review.IsEnabled = row.Item.Run is not null && snapshot.Book?.Tasks.Any(task => task.Id == row.Item.TaskId) == true;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { if (revision == selectionRevision) detail.Text = "无法核对：" + e.Message; }
    }
}
