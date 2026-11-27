namespace AgentLauncher.Core;

public static class ToolRecovery
{
    public const string BlockedMarker = "上次工具被内存检查拦截：";
    public const string ContinuePrompt = "请核对上次工具的执行结果。仅对明确被启动器内存检查在执行前拒绝、且尚未执行的步骤串行重试一次；已执行或结果不明的步骤先核对，不能重复写入。若内存检查仍拒绝，请停止并说明原因，不要绕过保护或循环重试。";

    public static bool CanOffer(ControlEntry entry) => entry.State == "Started"
        && entry.Plan.AgentId == "omp" && entry.Detail.Contains(BlockedMarker, StringComparison.Ordinal);
}
