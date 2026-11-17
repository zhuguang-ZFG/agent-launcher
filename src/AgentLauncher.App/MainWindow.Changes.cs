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
        var baseline = await Task.Run(() => LaunchEvidence.ReadBaseline(entry.Plan));
        var changes = await Task.Run(() => GitChanges.GetWorktreeChanges(entry.Plan.Cwd, baselineCommit: baseline));
        if (!changes.Ok) { ErrorText.Text = "查看变更失败：" + changes.Error; return; }
        var head = changes.PinnedBaseline ? $"{changes.Branch}（启动时提交 {changes.Baseline}，新增提交 {changes.Ahead}）" : changes.HasUpstream
            ? $"{changes.Branch}（基线 {changes.Baseline}，领先 {changes.Ahead} / 落后 {changes.Behind}）"
            : $"{changes.Branch}（无上游分支，参照 {changes.Baseline}）";
        var stat = (changes.UncommittedCount > 0 ? $"未提交更改 {changes.UncommittedCount} 项。" : "工作区干净。")
            + (changes.DiffStat.Length > 0 ? "\n\n已跟踪文件：\n" + changes.DiffStat : "\n\n已跟踪文件与比较基线没有差异。")
            + (changes.UntrackedFiles.Length > 0 ? "\n\n新增文件（尚未跟踪，未计入上方统计）：\n" + string.Join("\n",changes.UntrackedFiles.Select(path=>System.Text.Json.JsonSerializer.Serialize(path,new System.Text.Json.JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))) : "")
            + (changes.PinnedBaseline ? "\n\n比较基准是启动时的提交，可能包含启动前已有的未提交修改；共享目录中的其他任务修改也会显示。" : "\n\n此记录没有可用的启动基线，以上是当前参考分支的比较结果，不能视为本次任务独有的改动。");
        var dialog = new FormWindow(this, "查看变更 · " + entry.Plan.Name,
            [("head", "分支与基线", head, false), ("stat", "变更统计（git diff --stat）", stat, true)],
            "此处内容只读核对用，编辑不会保存。审阅、推送与创建 PR 请用任务卡的「审查改动」（先钉住已核对内容，不推可变分支）。");
        dialog.ShowDialog();
    }
}
