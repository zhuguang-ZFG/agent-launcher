namespace AgentLauncher.Core;

public sealed record JobMemoryEvidence(ulong LimitBytes, ulong PeakBytes, [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool LimitReached=false, [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? OffendingProcessId=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NotificationError=null);

public static class ExitDiagnostics
{
    public static string MemoryText(JobMemoryEvidence? memory,bool sharedPool=false)
    {
        if(memory is null)return "内存峰值未能读取，退出原因尚未确定。";
        var subject=sharedPool?"该共享池":"该窗格";
        var text=(memory.LimitReached?$"内核已报告{subject}的内存分配触及 Job 上限（PID {memory.OffendingProcessId}）；分配被拒绝，保护仍生效。请减少工具并发或缩小任务；不会结束其他窗格。":"")+$"{(sharedPool?"共享池内所有项及工具":"该项及工具子进程")}合计提交峰值 {memory.PeakBytes/(double)ResourcePolicy.MiB:0} MiB / 上限 {memory.LimitBytes/(double)ResourcePolicy.MiB:0} MiB。";
        if(NearLimit(memory))text+=sharedPool?"共享池峰值已接近上限；请减少池内并发，或为后续批次调整池上限。此读数不能单独证明某个窗格的退出原因。":"峰值已接近内存上限；后续批次可提高每项上限，或减少工具并发。此读数不能单独证明退出原因。";
        if(memory.NotificationError is not null)text+="内存分配通知不可用，Job 上限仍生效；未收到通知不能排除分配被拒绝。";
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
