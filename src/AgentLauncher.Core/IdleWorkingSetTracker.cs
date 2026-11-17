namespace AgentLauncher.Core;

// The caller supplies monotonic time and aggregate Job CPU usage. Poll frequency must not shorten idle time.
public sealed class IdleWorkingSetTracker
{
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);
    private long? previousCpuTicks;
    private TimeSpan idleSince;
    private TimeSpan observedAt;
    private TimeSpan? lastAttemptAt;

    public void Observe(long? cpuTicks, TimeSpan now)
    {
        if (now < observedAt && lastAttemptAt is not null) lastAttemptAt = now;
        if (cpuTicks is null or < 0 || previousCpuTicks != cpuTicks || now < observedAt) idleSince = now;
        previousCpuTicks = cpuTicks is >= 0 ? cpuTicks : null;
        observedAt = now;
    }

    // Reserve the cooldown before the native call, including failures or a zero-byte reclaim.
    public bool TryBeginTrim(TimeSpan now)
    {
        if (previousCpuTicks is null || now != observedAt || now - idleSince < IdleDelay
            || lastAttemptAt is {} attempted && now - attempted < Cooldown) return false;
        lastAttemptAt = now;
        return true;
    }
}
