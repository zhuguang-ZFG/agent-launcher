using System.Diagnostics;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

// Measurements for the S3.5 comparison. Only the shared pool total cap differs between the two modes:
// every measurement uses the same [root] -> [structural parent] -> [leaf item] chain, the same leaf cap,
// the same allocate-and-hold workload and the same root CPU rate. In standalone mode the structural parent
// has no memory limit and no pool peak is reported, because summing leaf peaks would not be a pool quota
// observation. Nothing here measures physical memory savings or throughput.
public sealed record PoolComparisonItem(int Index, int ExitCode, bool Allocated, bool AllocationRefused,
    ulong LeafLimitBytes, ulong LeafPeakBytes, long DurationMilliseconds, int HoldMilliseconds);

public sealed record PoolComparisonMemory(ulong PhysicalTotalBytes, ulong PhysicalMarginBytes,
    ulong CommitLimitBytes, ulong CommitMarginBytes, DateTimeOffset At);

public sealed record PoolComparisonMeasurement(string Scenario, string Mode, ulong ItemCapBytes, ulong? PoolCapBytes,
    int RootCpuRate, int AllocationMiB, int Concurrency, PoolComparisonItem[] Items, ulong? PoolPeakBytes,
    PoolComparisonMemory StartMemory, PoolComparisonMemory EndMemory);

public sealed record PoolComparisonReport(int SchemaVersion, string Limitation, PoolComparisonMeasurement[] Measurements);

internal static class PoolComparisonChecks
{
    private const int AllocationMiB = 64;
    private const int ItemCapMiB = 160;
    private const int LightHoldMilliseconds = 3000;
    private const int ContentionFirstHoldMilliseconds = 3000;
    private const int LightPoolCapMiB = 384;
    private const int ContentionPoolCapMiB = 128;
    private const string Limitation =
        "仅比较同一双任务负载下的额度行为与系统余量，不证明物理内存节省、吞吐改进或生产稳定性。";

    public static void Run(Action<string, Action> test, string temp)
    {
        var measurements = new List<PoolComparisonMeasurement>();
        test("ACC-S3P09 identical dual-task load completes in standalone and light shared-pool modes", () =>
        {
            var standalone = Measure("light", "independent", null, LightHoldMilliseconds, LightHoldMilliseconds, stagger: false, temp);
            measurements.Add(standalone);
            Assert(standalone.Items.All(item => item.ExitCode == 10 && item.Allocated && !item.AllocationRefused), "standalone light load did not complete both allocations");
            Assert(standalone.PoolCapBytes is null && standalone.PoolPeakBytes is null, "standalone mode fabricated a pool cap or pool peak");
            Assert(standalone.Items.All(item => item.LeafPeakBytes >= (ulong)AllocationMiB * ResourcePolicy.MiB), "standalone leaf peak lost a completed allocation");
            var shared = Measure("light", "shared", LightPoolCapMiB, LightHoldMilliseconds, LightHoldMilliseconds, stagger: false, temp);
            measurements.Add(shared);
            Assert(shared.Items.All(item => item.ExitCode == 10 && item.Allocated), "light shared pool refused an allocation inside its cap");
            Assert(shared.Items.All(item => item.LeafPeakBytes >= (ulong)AllocationMiB * ResourcePolicy.MiB), "shared leaf peak lost a completed allocation");
            Assert(shared.PoolCapBytes == (ulong)LightPoolCapMiB * ResourcePolicy.MiB, "shared pool did not expose the configured cap");
            Assert(shared.PoolPeakBytes is { } peak && peak >= 2UL * AllocationMiB * ResourcePolicy.MiB, "shared pool peak did not account both simultaneous tasks");
        });
        test("ACC-S3P10 shared pool cap refuses the second identical task while standalone mode admits both", () =>
        {
            var standalone = Measure("pool-limit", "independent", null, ContentionFirstHoldMilliseconds, 0, stagger: true, temp);
            measurements.Add(standalone);
            Assert(standalone.Items.All(item => item.ExitCode == 10 && item.Allocated), "standalone mode did not admit both identical tasks");
            var shared = Measure("pool-limit", "shared", ContentionPoolCapMiB, ContentionFirstHoldMilliseconds, 0, stagger: true, temp);
            measurements.Add(shared);
            Assert(shared.Items[0].ExitCode == 10 && shared.Items[0].Allocated && !shared.Items[0].AllocationRefused, "first shared-pool task lost its leaf allowance");
            Assert(shared.Items[0].LeafPeakBytes >= (ulong)AllocationMiB * ResourcePolicy.MiB, "first shared-pool leaf peak lost its allocation");
            Assert(shared.Items[1].ExitCode == 0 && !shared.Items[1].Allocated && shared.Items[1].AllocationRefused, "shared pool did not refuse the second identical allocation");
            Assert(shared.PoolCapBytes == (ulong)ContentionPoolCapMiB * ResourcePolicy.MiB, "shared pool did not expose the configured cap");
            Assert(shared.PoolPeakBytes is { } peak && peak > 0, "shared pool never recorded its peak");
        });
        Store.Save(Path.Combine(temp, "pool-comparison.json"), new PoolComparisonReport(1, Limitation, [.. measurements]));
    }

    private static PoolComparisonMeasurement Measure(string scenario, string mode, int? poolCapMiB,
        int firstHoldMilliseconds, int secondHoldMilliseconds, bool stagger, string temp)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var rootName = $@"Local\AL-cmp-{scenario}-{mode}-root-{suffix}";
        var parentName = $@"Local\AL-cmp-{scenario}-{mode}-parent-{suffix}";
        var itemPrefix = $@"Local\AL-cmp-{scenario}-{mode}-item-{suffix}-";
        var ready = Path.Combine(temp, $"cmp-{scenario}-{mode}-{suffix}-ready");
        var firstMarker = Path.Combine(temp, $"cmp-{scenario}-{mode}-{suffix}-first-ok");
        var secondMarker = Path.Combine(temp, $"cmp-{scenario}-{mode}-{suffix}-second-ok");
        var itemCap = (ulong)ItemCapMiB * ResourcePolicy.MiB;
        Job? root = null, parent = null, firstItem = null, secondItem = null;
        ProtectedProcess? setup = null;
        var workers = new List<ProtectedProcess>();
        try
        {
            root = new Job(rootName, root: true);
            var cpuBefore = root.QueryCpuRate() ?? throw new Exception("comparison root CPU was not readable");
            setup = StartSetup(root, temp, parentName, itemPrefix, 2, ready);
            setup.Resume();
            WaitFor(ready, "comparison setup never reported its hierarchy");
            parent = new Job(parentName, openOnly: true);
            firstItem = new Job(itemPrefix + "0", openOnly: true);
            secondItem = new Job(itemPrefix + "1", openOnly: true);
            if (poolCapMiB is { } capMiB) parent.SetMemoryLimit((ulong)capMiB * ResourcePolicy.MiB);
            firstItem.SetMemoryLimit(itemCap);
            secondItem.SetMemoryLimit(itemCap);
            var sampler = new Sampler();
            var start = sampler.Sample();
            var first = StartWorker(root, parent, firstItem, firstHoldMilliseconds, firstMarker, temp);
            workers.Add(first);
            Assert(Member(first, root) && Member(first, parent) && Member(first, firstItem), "first comparison task did not join the full hierarchy");
            ProtectedProcess second;
            long firstDuration, secondDuration;
            if (stagger)
            {
                // Prove the first allocation is live before starting the identical second task.
                var firstClock = Stopwatch.StartNew();
                first.Resume();
                WaitFor(firstMarker, "first comparison task never reported its allocation");
                second = StartWorker(root, parent, secondItem, secondHoldMilliseconds, secondMarker, temp);
                workers.Add(second);
                Assert(Member(second, root) && Member(second, parent) && Member(second, secondItem), "second comparison task did not join the full hierarchy");
                var secondClock = Stopwatch.StartNew();
                second.Resume();
                Assert(second.Process.WaitForExit(15000), "second comparison task timed out");
                secondDuration = secondClock.ElapsedMilliseconds;
                Assert(first.Process.WaitForExit(15000), "first comparison task timed out");
                firstDuration = firstClock.ElapsedMilliseconds;
            }
            else
            {
                second = StartWorker(root, parent, secondItem, secondHoldMilliseconds, secondMarker, temp);
                workers.Add(second);
                Assert(Member(second, root) && Member(second, parent) && Member(second, secondItem), "second comparison task did not join the full hierarchy");
                first.Resume();
                second.Resume();
                (firstDuration, secondDuration) = WaitForPair(first, second, 15000);
            }
            var end = sampler.Sample();
            var cpuAfter = root.QueryCpuRate() ?? throw new Exception("comparison root CPU became unreadable");
            Assert(cpuBefore.Flags == cpuAfter.Flags && cpuBefore.Rate == cpuAfter.Rate, "comparison setup changed the root CPU configuration");
            var firstPeak = firstItem.QueryMemory()?.PeakBytes ?? throw new Exception("first leaf peak was unavailable");
            var secondPeak = secondItem.QueryMemory()?.PeakBytes ?? throw new Exception("second leaf peak was unavailable");
            ulong? poolPeak = null;
            if (poolCapMiB is { } expectedCapMiB)
            {
                var evidence = parent.QueryMemory() ?? throw new Exception("shared pool evidence was unavailable");
                Assert(evidence.LimitBytes == (ulong)expectedCapMiB * ResourcePolicy.MiB, "shared pool did not accept the configured limit");
                poolPeak = evidence.PeakBytes;
            }
            return new PoolComparisonMeasurement(scenario, mode, itemCap,
                poolCapMiB is { } recordedCapMiB ? (ulong)recordedCapMiB * ResourcePolicy.MiB : null, (int)cpuBefore.Rate,
                AllocationMiB, 2,
                [
                    new PoolComparisonItem(0, first.Process.ExitCode, File.Exists(firstMarker),
                        first.Process.ExitCode == 0 && !File.Exists(firstMarker), itemCap, firstPeak, firstDuration, firstHoldMilliseconds),
                    new PoolComparisonItem(1, second.Process.ExitCode, File.Exists(secondMarker),
                        second.Process.ExitCode == 0 && !File.Exists(secondMarker), itemCap, secondPeak, secondDuration, secondHoldMilliseconds)
                ],
                poolPeak,
                new PoolComparisonMemory(start.Total, start.Available, start.CommitLimit, start.CommitAvailable, start.At),
                new PoolComparisonMemory(end.Total, end.Available, end.CommitLimit, end.CommitAvailable, end.At));
        }
        finally
        {
            foreach (var worker in workers) { Stop(worker); worker.Dispose(); }
            if (setup != null) { Stop(setup); setup.Dispose(); }
            secondItem?.Dispose();
            firstItem?.Dispose();
            parent?.Dispose();
            root?.Dispose();
        }
    }

    private static ProtectedProcess StartSetup(Job root, string temp, string parentName, string itemPrefix, int items, string ready)
        => new(new(Environment.ProcessPath!, ["--nested-setup", parentName, itemPrefix, items.ToString(), ready], "exe"), temp, [root]);

    private static ProtectedProcess StartWorker(Job root, Job parent, Job item, int holdMilliseconds, string marker, string temp)
        => new(new(Environment.ProcessPath!, ["--allocate-mib", AllocationMiB.ToString(), holdMilliseconds.ToString(), marker], "exe"), temp, [root, parent, item]);

    private static (long First, long Second) WaitForPair(ProtectedProcess first, ProtectedProcess second, int timeoutMilliseconds)
    {
        var watch = Stopwatch.StartNew();
        long firstDone = -1, secondDone = -1;
        while ((firstDone < 0 || secondDone < 0) && watch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (firstDone < 0 && first.Process.HasExited) firstDone = watch.ElapsedMilliseconds;
            if (secondDone < 0 && second.Process.HasExited) secondDone = watch.ElapsedMilliseconds;
            if (firstDone >= 0 && secondDone >= 0) break;
            Thread.Sleep(10);
        }
        Assert(firstDone >= 0 && secondDone >= 0, "comparison workers timed out");
        return (firstDone, secondDone);
    }

    private static bool Member(ProtectedProcess process, Job job)
    {
        Native.Check(Native.IsProcessInJob(process.Process.Handle, job.Handle, out var joined));
        return joined;
    }

    private static void WaitFor(string path, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert(watch.ElapsedMilliseconds < 10000, message);
            Thread.Sleep(50);
        }
    }

    private static void Stop(ProtectedProcess process)
    {
        try { if (!process.Process.HasExited) process.Process.Kill(); process.Process.WaitForExit(5000); } catch { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
