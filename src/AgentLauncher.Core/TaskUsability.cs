using System.Text;

namespace AgentLauncher.Core;

/// <summary>User-facing task text and ordering. No command is built or executed from this text.</summary>
public static class TaskUsability
{
    public static string StateLabel(string state) => state switch
    {
        "Running" => "运行中", "Review" => "等待验收", "Completed" => "已完成", "Failed" => "失败", "Cancelled" => "已取消",
        "Pending" => "等待终端领取", "Creating" => "正在创建进程", "Started" => "已启动", "Exited" => "主进程已退出，等待核对", "NeedsReconciliation" => "需要核对占用",
        "Settled" => "全部进程已退出", "Revoked" => "未启动，预约已撤销", "FailedBeforeStart" => "启动前失败", _ => state
    };

    public static QueueEntry[] OrderedQueue(TaskBook book) => book.Queue.OrderByDescending(entry => entry.Priority).ThenBy(entry => entry.Sequence).ToArray();

    public static string Brief(TaskCard task)
    {
        var text = new StringBuilder().AppendLine("任务：" + task.Name).AppendLine("项目：" + task.Project);
        if (task.Description.Length > 0) text.AppendLine().AppendLine(task.Description);
        if (task.Acceptance.Count > 0)
        {
            text.AppendLine().AppendLine("验收要求：");
            foreach (var item in task.Acceptance) text.AppendLine("- " + item.Text);
        }
        return text.ToString().TrimEnd();
    }

    public static string CloseQueueNotice(int waiting, bool dispatching) => waiting > 0
        ? $"还有 {waiting} 项等待执行。关闭窗口会暂停后续排队；重新打开后需要点「继续执行」。已运行的 Agent 会继续运行。\n\n关闭并暂停队列？"
        : dispatching ? "当前正在处理启动。关闭会停止后续准备，已运行的 Agent 会继续运行。\n\n关闭窗口？" : "";

    public static string SessionGuide(string agentId) => agentId switch
    {
        "codex" => "1. 回到本次任务原来的 Codex 终端，查看退出时给出的 codex resume 提示，复制其中完整的会话 ID。\n2. 若未保留提示，先在 Codex 自身的会话历史中核对项目和对话，再取得该会话的 ID。\n3. 仅粘贴会话 ID，不粘贴 codex resume、引号或整条命令。任务编号不是会话 ID。",
        "omp" => "1. 在本次任务原来的 Omp 会话信息或保存记录中找到对应会话文件。\n2. 核对项目目录和对话内容，复制完整会话文件路径（也支持会话 ID 前缀，建议使用完整路径避免重名）。\n3. 仅粘贴路径或 ID，不粘贴 omp --resume 或整条命令。任务编号不是会话 ID。",
        _ => "请从原 Agent 的会话信息或保存记录中取得会话标识，核对项目和对话后再填写。不要填写任务编号，也不要粘贴整条启动命令。"
    };
}
