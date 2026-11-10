namespace AgentLauncher.Core;

public sealed class PressureTracker
{
    public bool Pressure { get; private set; }
    public int HighStreak { get; private set; }
    public int HealthyStreak { get; private set; }

    public void Observe(MemorySnapshot sample, ulong physicalFloor)
    {
        if (!ResourcePolicy.IsSampleValid(sample))
        {
            HighStreak = 0;
            HealthyStreak = 0;
            return;
        }

        var high = sample.CpuPercent >= ResourcePolicy.CpuBusyPercent || sample.Available < physicalFloor;
        if (high)
        {
            HighStreak++;
            HealthyStreak = 0;
            if (HighStreak >= 3) Pressure = true;
            return;
        }

        HighStreak = 0;
        if (!Pressure)
        {
            HealthyStreak = 0;
            return;
        }

        HealthyStreak++;
        if (HealthyStreak >= 5) Pressure = false;
    }
}