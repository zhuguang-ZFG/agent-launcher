namespace AgentLauncher.Core;

public sealed record JobMemoryEvidence(ulong LimitBytes, ulong PeakBytes, [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool LimitReached=false, [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? OffendingProcessId=null);

public static class ExitDiagnostics
{
    public static string MemoryText(JobMemoryEvidence? memory)
    {
        if(memory is null)return "内存峰值未能读取，退出原因尚未确定。";
        var text=(memory.LimitReached?$"内核已报告该窗格的内存分配触及 Job 上限（PID {memory.OffendingProcessId}）；分配被拒绝，保护仍生效。请减少该项工具并发或缩小任务；不会结束其他窗格。":"")+$"该项及工具子进程合计提交峰值 {memory.PeakBytes/(double)ResourcePolicy.MiB:0} MiB / 上限 {memory.LimitBytes/(double)ResourcePolicy.MiB:0} MiB。";
        if(NearLimit(memory))text+="峰值已接近内存上限；后续批次可提高每项上限，或减少工具并发。此读数不能单独证明退出原因。";
        return text;
    }
    public static bool NearLimit(JobMemoryEvidence? memory)=>memory is {LimitBytes:>0}&&memory.PeakBytes>=(decimal)memory.LimitBytes*0.9m;
    public static string Describe(int exit,JobMemoryEvidence? memory)
    {
        var text=$"退出码 {exit}（0x{unchecked((uint)exit):X8}）。";
        if(exit==unchecked((int)0xC0000409))text+="Agent 原生进程快速失败；内存分配失败等情况可触发此码，不能仅凭退出码归因。";
        return text+MemoryText(memory);
    }
}
