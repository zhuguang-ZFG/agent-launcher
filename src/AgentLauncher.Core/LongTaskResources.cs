namespace AgentLauncher.Core;

public static class LongTaskResources
{
    public const ulong MemoryLimit = 4 * ResourcePolicy.GiB;
    public const ulong Planning = 1536 * ResourcePolicy.MiB;

    public static bool Suggested(Draft draft) => draft.Resource.JobMemoryLimit < MemoryLimit
        && draft.Slots.Any(slot => slot.Enabled && slot.AgentId == "omp");

    public static ResourceSettings Create(ResourceSettings current)
    {
        var next = current.Copy();
        next.Mode = "custom";
        next.JobMemoryLimit = Math.Max(current.JobMemoryLimit, MemoryLimit);
        next.PlanningBytes = Math.Max(current.PlanningBytes, Planning);
        next.Pool = ResourcePolicy.PoolIndependent;
        next.PoolMemoryLimit = null;
        return next;
    }
}
