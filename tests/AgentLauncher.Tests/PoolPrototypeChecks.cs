using System.ComponentModel;
using System.Diagnostics;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class PoolPrototypeChecks
{
    public static void Run(Action<string,Action> test, string temp)
    {
        var setups = new List<ProtectedProcess>();
        void Case(string name, Action body) => test(name, () =>
        {
            try { body(); }
            finally
            {
                foreach (var setup in setups) { Stop(setup); setup.Dispose(); }
                setups.Clear();
            }
        });
        ProtectedProcess Start(Job root, string pool, string prefix, int items, string ready)
        {
            var process = StartSetup(root, temp, pool, prefix, items, ready);
            setups.Add(process);
            return process;
        }
        Case("ACC-S3P01 three-level job chain joins atomically, keeps root CPU and enforces the leaf cap", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var rootName = $@"Local\AL-proto-root-{s}";
            var poolName = $@"Local\AL-proto-pool-{s}";
            var itemPrefix = $@"Local\AL-proto-item-{s}-";
            var ready = System.IO.Path.Combine(temp, $"proto-{s}-ready");
            using var root = new Job(rootName, root: true);
            var cpuBefore = root.QueryCpuRate();
            var setup = Start(root, poolName, itemPrefix, 1, ready);
            setup.Resume();
            WaitFor(ready, "nested setup never reported a pool hierarchy");
            using var pool = new Job(poolName, openOnly: true);
            using var item = new Job(itemPrefix + "0", openOnly: true);
            pool.SetMemoryLimit(192 * ResourcePolicy.MiB);
            var limit = pool.QueryMemory();
            Assert(limit is { LimitBytes: 192 * ResourcePolicy.MiB }, "the opened pool did not accept the dynamic memory limit");
            using var worker = new ProtectedProcess(new(Environment.ProcessPath!, ["--allocate-mib", "64"], "exe"), temp, [root, pool, item]);
            Assert(Member(worker, root) && Member(worker, pool) && Member(worker, item), "worker did not join all three levels");
            worker.Resume();
            Assert(worker.Process.WaitForExit(15000), "worker timed out");
            Assert(worker.Process.ExitCode == 10, "leaf allowance did not permit a 64 MiB allocation");
            var cpuAfter = root.QueryCpuRate();
            Assert(cpuBefore is not null && cpuAfter is not null && cpuBefore.Value.Flags == cpuAfter.Value.Flags && cpuBefore.Value.Rate == cpuAfter.Value.Rate, "pool setup or limit change altered the root CPU configuration");
        });

        Case("ACC-S3P02 leaf memory limits refuse allocation independently of a larger pool", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var poolName = $@"Local\AL-proto2-pool-{s}";
            var itemPrefix = $@"Local\AL-proto2-item-{s}-";
            var ready = System.IO.Path.Combine(temp, $"proto2-{s}-ready");
            using var root = new Job($@"Local\AL-proto2-root-{s}", root: true);
            var setup = Start(root, poolName, itemPrefix, 1, ready);
            setup.Resume();
            WaitFor(ready, "nested setup never reported a pool hierarchy");
            using var pool = new Job(poolName, openOnly: true);
            using var item = new Job(itemPrefix + "0", openOnly: true);
            pool.SetMemoryLimit(192 * ResourcePolicy.MiB);
            item.SetMemoryLimit(64 * ResourcePolicy.MiB);
            using var worker = new ProtectedProcess(new(Environment.ProcessPath!, ["--allocate-mib", "96"], "exe"), temp, [root, pool, item]);
            worker.Resume();
            Assert(worker.Process.WaitForExit(15000), "worker timed out");
            Assert(worker.Process.ExitCode == 0, "the leaf limit did not refuse a 96 MiB allocation");
        });

        Case("ACC-S3P03 pool limit caps the whole tree while leaf limits stay independent", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var poolName = $@"Local\AL-proto3-pool-{s}";
            var prefix = $@"Local\AL-proto3-item-{s}-";
            var ready = System.IO.Path.Combine(temp, $"proto3-{s}-ready");
            var firstOk = System.IO.Path.Combine(temp, $"proto3-{s}-first");
            var secondOk = System.IO.Path.Combine(temp, $"proto3-{s}-second");
            using var root = new Job($@"Local\AL-proto3-root-{s}", root: true);
            var setup = Start(root, poolName, prefix, 2, ready);
            setup.Resume();
            WaitFor(ready, "nested setup never reported a pool hierarchy");
            using var pool = new Job(poolName, openOnly: true);
            using var item0 = new Job(prefix + "0", openOnly: true);
            using var item1 = new Job(prefix + "1", openOnly: true);
            pool.SetMemoryLimit(128 * ResourcePolicy.MiB);
            item0.SetMemoryLimit(160 * ResourcePolicy.MiB);
            item1.SetMemoryLimit(160 * ResourcePolicy.MiB);
            using var first = new ProtectedProcess(new(Environment.ProcessPath!, ["--allocate-mib", "64", "6000", firstOk], "exe"), temp, [root, pool, item0]);
            first.Resume();
            WaitFor(firstOk, "first allocation never completed");
            using var second = new ProtectedProcess(new(Environment.ProcessPath!, ["--allocate-mib", "64", "0", secondOk], "exe"), temp, [root, pool, item1]);
            second.Resume();
            Assert(second.Process.WaitForExit(15000), "second worker timed out");
            Assert(second.Process.ExitCode == 0 && !File.Exists(secondOk), "pool limit did not refuse the second 64 MiB allocation");
            Assert(pool.QueryMemory() is { PeakBytes: > 0 }, "pool never recorded its peak");
            Assert(first.Process.WaitForExit(15000) && first.Process.ExitCode == 10, "first worker lost its independent leaf allowance");
        });

        Case("ACC-S3P04 a process created directly under the pool is visible there and not under any item", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var poolName = $@"Local\AL-proto4-pool-{s}";
            var itemPrefix = $@"Local\AL-proto4-item-{s}-";
            var ready = System.IO.Path.Combine(temp, $"proto4-{s}-ready");
            using var root = new Job($@"Local\AL-proto4-root-{s}", root: true);
            var setup = Start(root, poolName, itemPrefix, 1, ready);
            setup.Resume();
            WaitFor(ready, "nested setup never reported a pool hierarchy");
            using var pool = new Job(poolName, openOnly: true);
            using var item = new Job(itemPrefix + "0", openOnly: true);
            using var worker = new ProtectedProcess(new(Environment.ProcessPath!, ["--hold", "1200"], "exe"), temp, [root, pool]);
            worker.Resume();
            Assert(pool.Members().Contains(worker.Process.Id), "pool did not list its direct member");
            Assert(!item.Members().Contains(worker.Process.Id), "unattributed process leaked into an item");
            Assert(worker.Process.WaitForExit(6000), "pool member timed out");
        });

        Case("ACC-S3P05 an existing pool name is reused instead of duplicated", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var poolName = $@"Local\AL-proto5-pool-{s}";
            using var created = new Job(poolName, 48 * ResourcePolicy.MiB);
            using var opened = new Job(poolName);
            Assert(!opened.CreatedNew, "existing pool reported as newly created");
            opened.SetMemoryLimit(64 * ResourcePolicy.MiB);
            Assert(created.QueryMemory() is { LimitBytes: 64 * ResourcePolicy.MiB }, "second handle reached a different pool object");
        });

        Case("ACC-S3P06 a lost pool handle cannot be reopened by name while its member survives", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var poolName = $@"Local\AL-proto6-pool-{s}";
            var itemPrefix = $@"Local\AL-proto6-item-{s}-";
            var ready = System.IO.Path.Combine(temp, $"proto6-{s}-ready");
            using var root = new Job($@"Local\AL-proto6-root-{s}", root: true);
            var setup = Start(root, poolName, itemPrefix, 1, ready);
            setup.Resume();
            WaitFor(ready, "nested setup never reported a pool hierarchy");
            var pool = new Job(poolName, openOnly: true);
            using var worker = new ProtectedProcess(new(Environment.ProcessPath!, ["--hold", "2500"], "exe"), temp, [root, pool]);
            worker.Resume();
            pool.Dispose();
            setup.Process.Kill();
            setup.Process.WaitForExit(5000);
            var code = 0;
            try { using var reopened = new Job(poolName, openOnly: true); }
            catch (Win32Exception e) { code = e.NativeErrorCode; }
            Assert(code == 2, $"a lost job with a live member was reopened by name (Win32={code})");
            Assert(!worker.Process.HasExited, "losing the handle terminated the member");
            Assert(worker.Process.WaitForExit(8000) && worker.Process.ExitCode == 0, "member did not survive handle loss");
        });

        Case("ACC-S3P07 pool members outlive the process that created the pool", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var poolName = $@"Local\AL-proto7-pool-{s}";
            var itemPrefix = $@"Local\AL-proto7-item-{s}-";
            var ready = System.IO.Path.Combine(temp, $"proto7-{s}-ready");
            using var root = new Job($@"Local\AL-proto7-root-{s}", root: true);
            var setup = Start(root, poolName, itemPrefix, 1, ready);
            setup.Resume();
            WaitFor(ready, "nested setup never reported a pool hierarchy");
            using var pool = new Job(poolName, openOnly: true);
            using var item = new Job(itemPrefix + "0", openOnly: true);
            using var worker = new ProtectedProcess(new(Environment.ProcessPath!, ["--hold", "2500"], "exe"), temp, [root, pool, item]);
            worker.Resume();
            setup.Process.Kill();
            Assert(setup.Process.WaitForExit(5000), "pool creator did not exit");
            Assert(!worker.Process.HasExited, "creator exit terminated its pool member");
            Assert(pool.Members().Contains(worker.Process.Id), "pool lost its member after the creator exited");
            Assert(worker.Process.WaitForExit(8000) && worker.Process.ExitCode == 0, "member did not finish under a pool whose creator exited");
            Assert(pool.Members().Length == 0, "pool still listed a member after it exited");
        });
        Case("ACC-S3P08 a descendant spawned inside an item inherits the chain and survives its parent", () =>
        {
            var s = Guid.NewGuid().ToString("N");
            var poolName = $@"Local\AL-proto8-pool-{s}";
            var itemPrefix = $@"Local\AL-proto8-item-{s}-";
            var ready = System.IO.Path.Combine(temp, $"proto8-{s}-ready");
            var pidFile = System.IO.Path.Combine(temp, $"proto8-{s}-pid");
            using var root = new Job($@"Local\AL-proto8-root-{s}", root: true);
            var setup = Start(root, poolName, itemPrefix, 1, ready);
            setup.Resume();
            WaitFor(ready, "nested setup never reported a pool hierarchy");
            using var pool = new Job(poolName, openOnly: true);
            using var item = new Job(itemPrefix + "0", openOnly: true);
            pool.SetMemoryLimit(192 * ResourcePolicy.MiB);
            Process? child = null;
            try
            {
                using var parent = new ProtectedProcess(new(Environment.ProcessPath!, ["--spawn-child", "400", "4000", pidFile], "exe"), temp, [root, pool, item]);
                parent.Resume();
                WaitFor(pidFile, "parent never reported its child");
                Assert(parent.Process.WaitForExit(5000) && parent.Process.ExitCode == 0, "item parent did not exit cleanly");
                child = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture));
                Assert(!child.HasExited, "descendant died with its parent");
                foreach (var (level, name) in new[] { (root, "root"), (pool, "pool"), (item, "item") })
                {
                    Native.Check(Native.IsProcessInJob(child.Handle, level.Handle, out var joined));
                    Assert(joined, $"descendant lost its {name} membership");
                }
                Assert(item.Members().Contains(child.Id), "item did not list its inherited descendant");
                Assert(pool.QueryMemory() is { PeakBytes: > 0 }, "pool did not account the descendant");
                Assert(child.WaitForExit(8000) && child.ExitCode == 0, "descendant did not finish after its parent exited");
            }
            finally
            {
                if (child is { HasExited: false }) { child.Kill(); child.WaitForExit(3000); }
                child?.Dispose();
            }
        });
    }

    private static void Stop(ProtectedProcess process)
    {
        try { if (!process.Process.HasExited) process.Process.Kill(); process.Process.WaitForExit(5000); } catch { }
    }

    private static ProtectedProcess StartSetup(Job root, string temp, string poolName, string itemPrefix, int items, string ready)
        => new(new(Environment.ProcessPath!, ["--nested-setup", poolName, itemPrefix, items.ToString(), ready], "exe"), temp, [root]);

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

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
