namespace AgentLauncher.Core;

/// <summary>Early backpressure, not a replacement for the kernel hard limit.</summary>
public static class ToolMemoryPolicy
{
    public static Admission Check(MemorySnapshot sample, ulong limit, ulong? used, ulong? poolLimit,
        ulong? poolUsed, ulong physicalFloor, ulong commitFloor)
    {
        if (!ResourcePolicy.IsSampleValid(sample) || used is null || limit == 0 || (poolLimit.HasValue && (poolLimit == 0 || poolUsed is null)))
            return new(false, "内存读数暂不可用，暂停新增工具；稍后重试，已有工作继续运行。");
        var reserve = Math.Max(512 * ResourcePolicy.MiB, limit / 4);
        if (used >= limit || limit - used < reserve)
            return new(false, "该任务内存余量不足，已提前暂停新增工具，避免触及硬上限。请等待已有工具结束后串行重试；持续不足时保存会话，调高该项上限后续接。");
        if (poolLimit is > 0 && (poolUsed >= poolLimit || poolLimit - poolUsed < Math.Max(reserve, poolLimit.Value / 4)))
            return new(false, "共享池内存余量不足，已暂停新增工具。请等待池内已有工具结束后重试。");
        // A fresh tool needs growth room above the system floor, not merely a sample that
        // has not crossed it yet. Subtract only after checking the floor to avoid overflow.
        if (sample.Available < physicalFloor || sample.Available - physicalFloor < reserve
            || sample.CommitAvailable < commitFloor || sample.CommitAvailable - commitFloor < reserve)
            return new(false, "整机内存不足以在保留余量之上启动新工具，已提前暂停新增工具。请等待已有工作结束或释放内存后重试。");
        return new(true, "内存余量充足");
    }
}
