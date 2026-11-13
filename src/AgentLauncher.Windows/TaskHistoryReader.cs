using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public sealed record TaskHistoryItem(Guid AttemptId, Guid? TaskId, string Name, string State, DateTimeOffset? At, string Detail, TaskRun? Run = null);
public sealed record TaskHistorySnapshot(TaskBook? Book, TaskHistoryItem[] Items, string Notice);

public static class TaskHistoryReader
{
    public static TaskHistorySnapshot Load(TaskStore store, Guid? taskId)
    {
        var notices = new List<string>();
        TaskBook? book = null;
        Ledger? ledger = null;
        try { book = store.Load(); } catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { notices.Add("任务记录不可读取：" + e.Message); }
        try { ledger = TaskRecoveryReader.ReadLedger(); } catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { notices.Add("运行记录不可读取：" + e.Message); }
        var items = new Dictionary<Guid, TaskHistoryItem>();
        if (book is not null)
        {
            foreach (var run in book.Attempts.Where(run => taskId is null || run.TaskId == taskId))
            {
                var name = book.Tasks.FirstOrDefault(task => task.Id == run.TaskId)?.Name ?? "任务记录未保留";
                items[run.AttemptId] = new(run.AttemptId, run.TaskId, name, run.State, run.At, run.Detail, run.Copy());
            }
        }
        if (ledger is not null)
        {
            foreach (var entry in ledger.Entries.Where(entry => entry.Plan.Kind == "agent" && (taskId is null || entry.Plan.SourceSlotId == taskId)))
            {
                if (!items.ContainsKey(entry.Plan.Id)) items[entry.Plan.Id] = new(entry.Plan.Id, entry.Plan.SourceSlotId, entry.Plan.Name, entry.State, entry.Created, entry.Detail);
            }
            // Claims remain visible even if neither the attempt nor its archive survived; absence is an unknown result.
            foreach (var claim in ledger.TaskClaims)
            {
                var queue = book?.Queue.FirstOrDefault(entry => entry.Id == claim.EntryId);
                if (taskId is not null && queue?.TaskId != taskId) continue;
                if (!items.ContainsKey(claim.AttemptId)) items[claim.AttemptId] = new(claim.AttemptId, queue?.TaskId, "已领取的历史尝试", "结果未知", null, "领取证据仍保留；请选择记录核对身份归档。缺少记录不能视为已结束。");
            }
        }
        var root = Store.FilePath("attempts");
        if (Directory.Exists(root))
        {
            // This runs off the UI thread. Verify archives so old attempts remain searchable by task and name.
            var count = 0;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (++count > 10000) { notices.Add("历史目录超过 10000 个，本次只列出前 10000 个；可打开记录目录查阅其余档案。"); break; }
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || items.TryGetValue(id, out var known) && known.TaskId is not null) continue;
                try
                {
                    var entry = TaskRecoveryReader.ReadAttempt(id, ledger);
                    if (entry is not null && entry.Plan.Kind == "agent" && (taskId is null || entry.Plan.SourceSlotId == taskId))
                        items[id] = new(id, entry.Plan.SourceSlotId, entry.Plan.Name, entry.State, entry.Created, entry.Detail);
                    else if (entry is null && taskId is null && !items.ContainsKey(id))
                        items[id] = new(id, null, "历史尝试 " + id.ToString("N")[..8], "待核对", null, "选择记录读取保留的结果与回执；目录存在不代表执行成功。");
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
                {
                    if (taskId is null && !items.ContainsKey(id)) items[id] = new(id, null, "身份待核对 " + id.ToString("N")[..8], "结果未知", null, "历史身份无法读取，不能推断执行结果。");
                    if (!notices.Contains("部分历史身份无法核对，已保留未知状态。")) notices.Add("部分历史身份无法核对，已保留未知状态。");
                }
            }
        }
        return new(book, items.Values.OrderByDescending(item => item.At).ThenBy(item => item.AttemptId).ToArray(), string.Join("\n", notices));
    }

    public static string Diagnostic(TaskHistoryItem item)
    {
        Ledger? ledger = null;
        var ledgerNotice = "";
        try { ledger = TaskRecoveryReader.ReadLedger(); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { ledgerNotice = "控制记录不可读取；以下仅为可验证的历史档案，不授权释放占用。"; }
        var entry = TaskRecoveryReader.ReadAttempt(item.AttemptId, ledger);
        if (entry is not null && item.Run is not null && !AttemptIdentity.Matches(item.Run, entry)) throw new InvalidDataException("历史任务与启动记录身份不一致，不能展示为同一次执行。");
        Receipt? receipt = null;
        var receiptNotice = "";
        try { receipt = Store.Read<Receipt>(Store.ReceiptPath(item.AttemptId)); }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException) { receiptNotice = "回执不可读取；不能由缺失或损坏的回执推断执行结果。"; }
        var runnerNotice="";
        RunnerEvidence? entered=null, failure=null;
        try
        {
            var directory=Path.Combine(Store.Root,"attempts",item.AttemptId.ToString("N"));
            entered=Store.Read<RunnerEvidence>(Path.Combine(directory,"runner-entered.json"));
            failure=Store.Read<RunnerEvidence>(Path.Combine(directory,"runner-error.json"));
        }
        catch(Exception e)when(e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException){runnerNotice="运行器诊断不可读取；保留原文件供核对。";}
        return JsonSerializer.Serialize(new
        {
            item.AttemptId, item.TaskId, item.Name, TaskState = item.State, item.At,
            ProcessState = entry?.State ?? "没有可验证的启动身份记录，结果未知", LedgerNotice = ledgerNotice,
            ProcessDetail = entry?.Detail, Workspace = entry?.Plan.Cwd,
            ReceiptPhase = receipt?.Phase ?? "没有可读取的回执", ReceiptAt = receipt?.At, receipt?.ExitCode, ReceiptNotice = receiptNotice,
            receipt?.JobMemory, receipt?.PoolMemory,
            RunnerEntered=entered,RunnerFailure=failure,RunnerNotice=runnerNotice,
            Notice = "此预览不包含启动参数、会话 ID、环境变量或凭据；工作目录可能包含用户名或项目名。单独的回执不替代身份与全部进程退出证明。"
        }, new JsonSerializerOptions { WriteIndented = true });
    }
    // Raw exception messages/stacks may include arguments or paths. Export only native identity fields.
    private sealed record RunnerEvidence(DateTimeOffset At,int Pid,string? Type,int? HResult);
}
