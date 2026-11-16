using System.Globalization;
using AgentLauncher.Core;

namespace AgentLauncher.App;

public partial class MainWindow
{
    /// <summary>
    /// Copies one settled task into a 2–4 card comparison group: every copy keeps the source description, acceptance,
    /// directory policy and resources, carries a shared group marker in Note, and is enqueued through the normal path.
    /// </summary>
    private async Task CopyAsCompareGroupAsync(Guid taskId)
    {
        if (taskBook is null) { ErrorText.Text = taskLoadError; return; }
        var original = taskBook.Tasks.SingleOrDefault(candidate => candidate.Id == taskId);
        if (original is null) { ErrorText.Text = "任务不存在，请刷新后重试。"; return; }
        if (original.Archived) { ErrorText.Text = "该任务已归档，不能复制为对比组。"; return; }
        if (taskBook.Queue.Any(entry => entry.TaskId == taskId)) { ErrorText.Text = "该任务正在队列中，不能复制为对比组；请先移出队列。"; return; }
        if (taskBook.Attempts.Any(run => run.TaskId == taskId && !run.Terminal)) { ErrorText.Text = "该任务正在执行或结果未确认，不能复制为对比组；请等待结束后再试。"; return; }

        // Reuse the readiness signal already computed by DiscoverCatalogAsync/AgentChoices: a program is ready when
        // discovery found an install for it. Keep the source agent available so the default is always valid.
        var options = Agents.All
            .Where(agent => agent.Command.Length > 0 && (discoveredInstalls.GetValueOrDefault(agent.Id)?.Length > 0 || agent.Id == original.AgentId))
            .Select(agent => (agent.Id, agent.Name)).ToArray();
        var defaultAgent = options.Any(option => option.Id == original.AgentId) ? original.AgentId : options.FirstOrDefault().Id;
        if (defaultAgent is null) { ErrorText.Text = "没有程序与目录就绪的 Agent，请先运行程序发现。"; return; }

        var dialog = new FormWindow(this, "复制为对比组 · " + original.Name,
            [("count", "份数（2–4）", "2", false),
             ("agent1", "第 1 份 Agent", defaultAgent, false),
             ("agent2", "第 2 份 Agent", defaultAgent, false),
             ("agent3", "第 3 份 Agent", defaultAgent, false),
             ("agent4", "第 4 份 Agent", defaultAgent, false)],
            "将复制说明、验收项、目录策略与资源额度，生成 N 张独立任务并依次加入队列；原任务不变。每份可分别选择不同 Agent（只有前 N 份生效）。",
            choices: new()
            {
                ["count"] = [("2", "2 份"), ("3", "3 份"), ("4", "4 份")],
                ["agent1"] = options, ["agent2"] = options, ["agent3"] = options, ["agent4"] = options
            });
        if (dialog.ShowDialog() != true) return;
        if (!int.TryParse(dialog.Values["count"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count is < 2 or > 4)
        { ErrorText.Text = "份数必须是 2 到 4 之间的整数。"; return; }
        var selectedAgents = new[] { "agent1", "agent2", "agent3", "agent4" }.Take(count)
            .Select(key => options.Any(option => option.Id == dialog.Values[key]) ? dialog.Values[key] : defaultAgent).ToArray();

        var groupId = Guid.NewGuid().ToString("N")[..8];
        var createdIds = new List<Guid>(count);
        if (!await UpdateTasksAsync(book =>
        {
            var source = book.Tasks.Single(candidate => candidate.Id == taskId);
            TaskBookOps.EnsureTaskCapacity(book, count);
            for (var i = 0; i < count; i++)
            {
                var agent = selectedAgents[i];
                var suffix = $" · 对比 {i + 1}/{count}";
                var card = source.Copy();
                card.Id = Guid.NewGuid();
                card.Revision = 1;
                card.Name = (source.Name.Length + suffix.Length > TaskSchema.MaxNameLength ? source.Name[..(TaskSchema.MaxNameLength - suffix.Length)] : source.Name) + suffix;
                card.Note = $"[对比组 {groupId}]" + (string.IsNullOrWhiteSpace(source.Note) ? "" : " " + source.Note);
                card.UpdatedAt = DateTimeOffset.UtcNow;
                card.Archived = false;
                card.DependsOn = [];
                card.QuickLaunch = null;
                if (card.AgentId != agent) { card.AgentId = agent; card.Profile = new AgentProfile(); }
                card.Validate();
                book.Tasks.Add(card);
                createdIds.Add(card.Id);
            }
        }, "对比任务创建失败")) return;

        foreach (var id in createdIds) await EnqueueTaskAsync(id);
        var enqueued = createdIds.Count(id => taskBook?.Queue.Any(entry => entry.TaskId == id) == true || taskBook?.Attempts.Any(run => run.TaskId == id) == true);
        QueueStatusText.Text = enqueued == createdIds.Count
            ? $"已创建 {createdIds.Count} 张对比任务并加入队列（对比组 {groupId}）。"
            : $"已创建 {createdIds.Count} 张对比任务（对比组 {groupId}），其中 {enqueued} 张已入队；其余未入队，请查看错误提示。";
    }
}
