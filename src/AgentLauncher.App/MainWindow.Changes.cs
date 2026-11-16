using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

// 查看变更 / 审阅改动（会话卡操作）：摘要为只读核对；推送与创建 PR 一律走任务卡的审阅窗口
// （TaskReviewWindow → ReviewService.PushCore/PullRequestCore，钉住已核对 SHA，不推可变分支）。
public partial class MainWindow
{
    private void AddChangeActions(StackPanel row, ControlEntry e)
    {
        if (e.State != "Settled" || e.Plan.Kind != "agent" || !System.IO.Directory.Exists(e.Plan.Cwd)) return;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var view = new Button { Content = "查看变更", Style = (Style)FindResource("QuietButton"), ToolTip = "查看该工作目录相对基线的变更统计；只读，不修改任何内容。" };
        AutomationProperties.SetName(view, $"查看变更 {e.Plan.Name}");
        view.Click += async (_, _) => await GuardAsync(() => ViewChangesAsync(e));
        actions.Children.Add(view);
        var linked = FindLinkedRun(e.Plan.Id);
        if (linked is not null)
        {
            var review = new Button { Content = "审阅改动", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "打开该任务的审阅窗口：核对变更、推送已核对内容并创建 PR。" };
            AutomationProperties.SetName(review, $"审阅改动 {e.Plan.Name}");
            review.Click += async (_, _) => await GuardAsync(() => { new TaskReviewWindow(this, linked.Value.Task, linked.Value.Run, status?.Entries).Show(); return Task.CompletedTask; });
            actions.Children.Add(review);
        }
        row.Children.Add(actions);
    }

    private (TaskCard Task, TaskRun Run)? FindLinkedRun(Guid attemptId)
    {
        if (taskBook is null) return null;
        var run = taskBook.Attempts.LastOrDefault(run => run.AttemptId == attemptId && run.Terminal);
        if (run is null) return null;
        var task = taskBook.Tasks.SingleOrDefault(candidate => candidate.Id == run.TaskId);
        return task is null ? null : (task, run);
    }

    private async Task ViewChangesAsync(ControlEntry entry)
    {
        var changes = await Task.Run(() => GitChanges.GetWorktreeChanges(entry.Plan.Cwd));
        if (!changes.Ok) { ErrorText.Text = "查看变更失败：" + changes.Error; return; }
        var head = changes.HasUpstream
            ? $"{changes.Branch}（基线 {changes.Baseline}，领先 {changes.Ahead} / 落后 {changes.Behind}）"
            : $"{changes.Branch}（无上游分支，参照 {changes.Baseline}）";
        var stat = (changes.UncommittedCount > 0 ? $"未提交更改 {changes.UncommittedCount} 项。" : "工作区干净。")
            + (changes.DiffStat.Length > 0 ? "\n\n" + changes.DiffStat : "\n\n与基线没有差异。");
        var dialog = new FormWindow(this, "查看变更 · " + entry.Plan.Name,
            [("head", "分支与基线", head, false), ("stat", "变更统计（git diff --stat）", stat, true)],
            "此处内容只读核对用，编辑不会保存。审阅、推送与创建 PR 请用任务卡的「审查改动」（先钉住已核对内容，不推可变分支）。");
        dialog.ShowDialog();
    }
}
