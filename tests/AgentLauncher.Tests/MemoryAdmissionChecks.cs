using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class MemoryAdmissionChecks
{
    public static void Run(Action<string, Action> test, string temp)
    {
        const ulong gib = ResourcePolicy.GiB;
        var policy = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "conservative" }, 16 * gib, 32 * gib);
        MemorySnapshot Sample(ulong commit, ulong physical = 8 * gib) => new(16 * gib, physical, 32 * gib, commit, 5, DateTimeOffset.UtcNow);
        BudgetItem Item() => new(Guid.NewGuid(), "agent", 768 * ResourcePolicy.MiB, 2 * gib);
        test("ACC-R02 commit admission reserves planning-based growth above measured usage, not hard-cap remainders", () =>
        {
            var above = Item() with { CurrentCommitBytes = 3 * gib / 2 };
            var growing = Item() with { CurrentCommitBytes = gib / 4 };
            var requested = Item();
            var need = (768 * ResourcePolicy.MiB - gib / 4) + 768 * ResourcePolicy.MiB + policy.CommitFloor;
            Assert(ResourcePolicy.Evaluate(Sample(need - 1), policy, [above, growing], [requested], includeSoftBudget: false).Kind == AdmissionKind.Deny, "planning growth overcommitted");
            // Conservative floors keep the commit boundary strict; the soft budget is excluded so the modeling assertion stays isolated.
            Assert(ResourcePolicy.Evaluate(Sample(need), policy, [above, growing], [requested], includeSoftBudget: false).Kind == AdmissionKind.Allow, "exact planning headroom denied");
            var unknownNeed = 2 * 768 * ResourcePolicy.MiB + policy.CommitFloor;
            Assert(ResourcePolicy.Evaluate(Sample(unknownNeed), policy, [Item() with { CurrentCommitBytes = null }], [requested]).Kind == AdmissionKind.Allow,
                "unknown usage was charged the full hard-cap remainder instead of planning");
        });
        test("ACC-R04 only measured reserved private usage offsets existing occupancy", () =>
        {
            var active = Item() with { CurrentCommitBytes = gib, CurrentPhysicalBytes = 512 * ResourcePolicy.MiB };
            var requested = Item();
            // Requests-model: active hedge is max(0, planning-usage): 0 commit (1.0 > 0.75), 0.25 GiB physical.
            // Need: commit 0.75 + floor; physical 0.75 + 0.25 + floor = 1 GiB + floor.
            var sample = Sample(768 * ResourcePolicy.MiB + policy.CommitFloor, gib + policy.PhysicalFloor);
            Assert(ResourcePolicy.Evaluate(sample, policy, [active], [requested]).Kind == AdmissionKind.Allow, "live usage charged twice");
            // Unknown usage charges the full planning estimate, never the old hard-cap remainder.
            Assert(ResourcePolicy.Evaluate(sample, policy, [active with { CurrentCommitBytes = null }], [requested]).Kind == AdmissionKind.Deny, "unknown commit credited");
            Assert(ResourcePolicy.Evaluate(sample, policy, [active with { CurrentPhysicalBytes = null }], [requested]).Kind == AdmissionKind.Deny, "unknown physical usage credited");
            Assert(ResourcePolicy.Evaluate(sample, policy, [Item()], [requested with { CurrentCommitBytes = ulong.MaxValue, CurrentPhysicalBytes = ulong.MaxValue }]).Kind == AdmissionKind.Deny, "request supplied its own usage credit");
        });
        test("ACC-R04 retired usage and over-cap readings cannot create negative charges", () =>
        {
            var active = Item() with { CurrentCommitBytes = ulong.MaxValue, CurrentPhysicalBytes = ulong.MaxValue };
            Assert(ResourcePolicy.Evaluate(Sample(policy.CommitFloor), policy, [active], [Item()]).Kind == AdmissionKind.Deny, "overflow manufactured capacity");
            Assert(ResourcePolicy.Evaluate(Sample(policy.CommitFloor), policy, [active with { CurrentCommitBytes = 0 }], []).Kind == AdmissionKind.Deny, "historical peak credited after drain");
            var huge = Item() with { JobMemoryLimit = ulong.MaxValue };
            Assert(ResourcePolicy.Evaluate(Sample(32 * gib), policy, [huge], [huge]).Kind == AdmissionKind.Deny, "sum overflow manufactured capacity");
        });
        test("ACC-S3P shared pool remaining commit is billed once across requests and reservations", () =>
        {
            var poolId = Guid.NewGuid();
            var active = Item() with { PoolId = poolId, PoolLimit = 4 * gib, PoolCommitBytes = gib };
            var pending = Item() with { PoolId = poolId, PoolLimit = 4 * gib };
            // Pool hedge = max(0, Σmember planning − pool used), charged once: (3×0.75 − 1) + floor.
            var need = (3 * 768 * ResourcePolicy.MiB - gib) + policy.CommitFloor;
            var sample = Sample(need);
            Assert(ResourcePolicy.Evaluate(sample, policy, [active], [pending, pending with { Id = Guid.NewGuid() }]).Kind == AdmissionKind.Allow, "same pool billed twice");
            Assert(ResourcePolicy.Evaluate(sample with { CommitAvailable = sample.CommitAvailable - 1 }, policy, [active], [pending, pending with { Id = Guid.NewGuid() }]).Kind == AdmissionKind.Deny, "pool planning undercharged");
            // A requested item's own claimed pool usage never reduces the charge; only the monitor's live reading counts.
            var claiming = pending with { PoolCommitBytes = 4 * gib };
            Assert(ResourcePolicy.Evaluate(sample with { CommitAvailable = sample.CommitAvailable - 1 }, policy, [active], [claiming, claiming with { Id = Guid.NewGuid() }]).Kind == AdmissionKind.Deny, "request credited pool usage");
            // Leaf usage must not substitute for the pool reading: unknown pool usage charges full member planning.
            var leafOnly = active with { PoolCommitBytes = null, CurrentCommitBytes = 4 * gib };
            var leafNeed = (2 * 768 * ResourcePolicy.MiB) + policy.CommitFloor;
            Assert(ResourcePolicy.Evaluate(Sample(leafNeed - 1), policy, [leafOnly], [pending]).Kind == AdmissionKind.Deny, "leaf usage substituted for pool charge");
        });
        test("ACC-R05 low commit pressure latches and needs five healthy samples to clear", () =>
        {
            var tracker = new PressureTracker();
            for (var i = 0; i < 3; i++) tracker.Observe(Sample(policy.CommitFloor - 1), policy.PhysicalFloor, policy.CommitFloor);
            Assert(tracker.Pressure, "commit pressure did not latch");
            for (var i = 0; i < 4; i++) tracker.Observe(Sample(8 * gib), policy.PhysicalFloor, policy.CommitFloor);
            Assert(tracker.Pressure, "pressure cleared too early");
            tracker.Observe(Sample(8 * gib), policy.PhysicalFloor, policy.CommitFloor);
            Assert(!tracker.Pressure, "pressure did not recover");
        });
        test("ACC-R09 a busy 16 GiB machine admits one more agent while jobs stay under caps", () =>
        {
            // Measured on the reference machine 2026-11-13: three live Omp panes at these commit/working-set readings,
            // 3.83 GiB commit free, ~2.5-3.3 GiB physical available. Hard Job caps remain the kernel-enforced limit;
            // admission reserves only planning-based growth (Kubernetes requests), never full hard-cap remainders.
            var busy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * gib, 34 * gib);
            var strict = ResourcePolicy.SnapshotFor(new ResourceSettings { Mode = "conservative" }, 16 * gib, 34 * gib);
            MemorySnapshot Live(ulong commitFree, ulong avail) => new(16 * gib, avail, 34 * gib, commitFree, 30, DateTimeOffset.UtcNow);
            var panes = new[]
            {
                Item() with { CurrentCommitBytes = 1820 * ResourcePolicy.MiB, CurrentPhysicalBytes = 780 * ResourcePolicy.MiB },
                Item() with { CurrentCommitBytes = 850 * ResourcePolicy.MiB, CurrentPhysicalBytes = 440 * ResourcePolicy.MiB },
                Item() with { CurrentCommitBytes = 1030 * ResourcePolicy.MiB, CurrentPhysicalBytes = 440 * ResourcePolicy.MiB },
            };
            // Practical mode never blocks on memory readings: the four-pane soft-budget overrun is advisory only.
            var advisory = ResourcePolicy.Evaluate(Live(3800 * ResourcePolicy.MiB, 3300 * ResourcePolicy.MiB), busy, panes, [Item()]);
            Assert(advisory.Kind == AdmissionKind.Allow && advisory.Advisory, "practical mode blocked a fitting agent on memory readings");
            var denied = ResourcePolicy.Evaluate(Live(2000 * ResourcePolicy.MiB, 3300 * ResourcePolicy.MiB), strict, panes, [Item()]);
            Assert(denied.Kind == AdmissionKind.Deny, "conservative commit floor ignored");
            Assert(denied.Reason.Contains("GiB") && denied.Reason.Contains("按资源依次启动"), "denial lacks shortfall and actionable guidance: " + denied.Reason);
            Assert(ResourcePolicy.Evaluate(Live(3800 * ResourcePolicy.MiB, 2200 * ResourcePolicy.MiB), strict, panes, [Item()]).Kind == AdmissionKind.Deny,
                "conservative physical floor ignored");
        });
        test("ACC-R07 tool guard stops before Job or pool hard limits and resumes with headroom", () =>
        {
            var sample = Sample(8 * gib);
            Admission Check(ulong? used, ulong? pool = null, ulong? pooled = null, MemorySnapshot? memory = null) =>
                ToolMemoryPolicy.Check(memory ?? sample, 2 * gib, used, pool, pooled, policy.PhysicalFloor, policy.CommitFloor);
            Assert(Check(gib).Allowed && Check(3 * gib / 2).Allowed, "healthy tool denied");
            Assert(!Check(3 * gib / 2 + 1).Allowed && !Check(2 * gib).Allowed, "hard limit reached before guard stopped tools");
            Assert(!Check(gib, 4 * gib, 3 * gib + 1).Allowed, "shared pool pressure ignored");
            Assert(!Check(null).Allowed && !Check(gib, 0, null).Allowed && !Check(gib, 4 * gib, null).Allowed, "unknown usage allowed tools");
            Assert(!Check(gib, memory: sample with { CommitAvailable = policy.CommitFloor - 1 }).Allowed, "system commit pressure ignored");
            Assert(!Check(gib, memory: sample with { Available = policy.PhysicalFloor - 1 }).Allowed, "physical pressure ignored");
            Assert(!Check(gib, memory: sample with { At = DateTimeOffset.UtcNow.AddSeconds(-4) }).Allowed, "stale usage allowed tools");
            Assert(Check(gib).Allowed, "pressure recovery remained blocked");
        });
        test("ACC-R07 tool admission leaves growth room above both system floors", () =>
        {
            const ulong reserve = 512 * ResourcePolicy.MiB;
            var exact = Sample(policy.CommitFloor + reserve, policy.PhysicalFloor + reserve);
            Admission Check(MemorySnapshot sample, ulong cap = 2 * gib, ulong? physical = null, ulong? commit = null) =>
                ToolMemoryPolicy.Check(sample, cap, gib, null, null, physical ?? policy.PhysicalFloor, commit ?? policy.CommitFloor);
            Assert(Check(exact).Allowed, "exact growth margin denied");
            Assert(!Check(exact with { Available = exact.Available - 1 }).Allowed, "physical growth consumed the floor");
            Assert(!Check(exact with { CommitAvailable = exact.CommitAvailable - 1 }).Allowed, "commit growth consumed the floor");
            Assert(!Check(exact, 4 * gib).Allowed, "larger Job did not retain its larger growth margin");
            Assert(Check(Sample(policy.CommitFloor + gib, policy.PhysicalFloor + gib), 4 * gib).Allowed, "larger margin did not recover");
            Assert(!Check(exact, physical: ulong.MaxValue).Allowed && !Check(exact, commit: ulong.MaxValue).Allowed, "floor arithmetic overflow allowed a tool");
            Assert(Check(exact).Allowed, "healthy tools remained blocked after pressure");
        });
        test("ACC-R07 native guard endpoint denies low Job headroom without terminating members", () =>
        {
            using var job = new Job("Local\\AL-guard-" + Guid.NewGuid().ToString("N"), 2 * gib);
            using var guard = new OmpMemoryGuard(job, null, 1, 1);
            bool Query()
            {
                using var pipe = new NamedPipeClientStream(".", guard.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                using var timeout = new CancellationTokenSource(3000);
                pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
                pipe.WriteByte(1);pipe.Flush();
                using var reader = new StreamReader(pipe);
                var text = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
                using var result = JsonDocument.Parse(text!);
                return result.RootElement.GetProperty("allowed").GetBoolean();
            }
            Assert(Query(), "healthy kernel counters did not allow a tool");
            job.SetMemoryLimit(256 * ResourcePolicy.MiB);
            Assert(!Query(), "low kernel headroom allowed a tool");
            Assert(guard.LastDecision is { Allowed: false }, "denial not exposed to the pane receipt publisher");
            job.SetMemoryLimit(2 * gib);
            Assert(Query(), "recovered kernel headroom did not allow a tool");
            Assert(guard.LastDecision is { Allowed: true }, "recovered tool check did not clear the denial");
        });
        test("ACC-R07 guarded Omp argv remains structured and frozen in the plan digest", () =>
        {
            var args = new[] { "--resume", "session path with spaces", "--", "literal --extension" };
            var command = OmpMemoryGuard.Attach(new("omp.exe", args, "exe"));
            Assert(OmpMemoryGuard.IsAttached(command), "guard marker missing");
            Assert(command.Arguments.Skip(2).SequenceEqual(args), "user arguments changed");
            var shim = OmpMemoryGuard.Attach(new("node.exe", ["C:\\entry path\\omp.js", ..args], "node-shim"));
            Assert(shim.Arguments[0] == "C:\\entry path\\omp.js" && shim.Arguments.Skip(3).SequenceEqual(args), "Node entry or argv shifted");
            var trusted = OmpMemoryGuard.Attach(new("omp.exe", ["--trusted-extension", "C:\\user.js"], "exe"));
            Assert(trusted.Arguments[0] == "--trusted-extension", "mixed mutually exclusive extension flags");
            var literal = OmpMemoryGuard.Attach(new("omp.exe", ["--extension", "user.mjs", "--", "--trusted-extension=literal prompt"], "exe"));
            Assert(literal.Arguments[0] == "--extension", "literal prompt changed extension trust mode");
            var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 0, "guard", temp, new(temp, "identity"), false, command, 2 * gib);
            Assert(ResourcePolicy.PlanDigest([plan]) != ResourcePolicy.PlanDigest([plan with { Command = new("omp.exe", args, "exe") }]), "guard omitted from execution authority");
        });
        test("ACC-R07 protected process environment is private and preserves Unicode and inherited values", () =>
        {
            var previous = Environment.GetEnvironmentVariable("LAUNCHER_MEMORY_INHERITED");
            var pipeBefore = Environment.GetEnvironmentVariable(OmpMemoryGuard.PipeEnvironment);
            try
            {
                Environment.SetEnvironmentVariable("LAUNCHER_MEMORY_INHERITED", "parent-value");
                using var root = new Job("Local\\AL-env-" + Guid.NewGuid().ToString("N"), root: true);
                using var job = new Job("Local\\AL-env-item-" + Guid.NewGuid().ToString("N"), gib);
                var output = Path.Combine(temp, "guard-env.json");
                using var child = new ProtectedProcess(new(Environment.ProcessPath!, ["--guard-environment", output], "exe"), temp, [root, job], createNoWindow: true,
                    environment: new Dictionary<string,string> { [OmpMemoryGuard.PipeEnvironment] = "pane-only", ["LAUNCHER_MEMORY_UNICODE"] = "中文 空格 = 值" });
                child.Resume();
                try { Assert(child.Process.WaitForExit(10000) && child.Process.ExitCode == 0, "environment fixture failed"); }
                finally { if (!child.Process.HasExited) { child.Process.Kill();child.Process.WaitForExit(5000); } }
                using var result = JsonDocument.Parse(File.ReadAllText(output));
                Assert(result.RootElement.GetProperty("Pipe").GetString() == "pane-only" && result.RootElement.GetProperty("Unicode").GetString() == "中文 空格 = 值", "Unicode override corrupted");
                Assert(result.RootElement.GetProperty("Inherited").GetString() == "parent-value", "caller environment dropped");
                Assert(Environment.GetEnvironmentVariable(OmpMemoryGuard.PipeEnvironment) == pipeBefore, "child environment leaked into parent");
            }
            finally { Environment.SetEnvironmentVariable("LAUNCHER_MEMORY_INHERITED", previous); }
        });
        test("ACC-R07 multi-tool 4 GiB preset permits a 2180 MiB allocation refused by 2 GiB", () =>
        {
            using var root = new Job("Local\\AL-capacity-" + Guid.NewGuid().ToString("N"), root: true);
            foreach (var cap in new[] { 2 * gib, 4 * gib })
            {
                using var job = new Job("Local\\AL-capacity-item-" + Guid.NewGuid().ToString("N"), cap);
                using var child = new ProtectedProcess(new(Environment.ProcessPath!, ["--allocate-commit", "2180"], "exe"), temp, [root, job], createNoWindow: true);
                child.Resume();
                try
                {
                    Assert(child.Process.WaitForExit(10000), "capacity fixture timed out");
                    Assert(child.Process.ExitCode == (cap == 2 * gib ? 37 : 11), "allocation did not match the configured cap");
                    Assert(job.QueryMemory()!.LimitBytes == cap, "hard cap changed during allocation");
                    if (cap == 4 * gib) Assert(job.QueryMemory()!.PeakBytes > 2 * gib, "fixture did not cross the historical cliff");
                }
                finally { if (!child.Process.HasExited) { child.Process.Kill();child.Process.WaitForExit(5000); } }
            }
        });
        test("ACC-R04 paged status carries consistent live budgets and rejects mixed credits", () =>
        {
            var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "budget", temp, new(temp, "workspace"), true, new(Environment.ProcessPath!, [], "exe"), 2 * gib);
            var active = new ControlEntry { Plan = plan, State = "Started" };
            var budget = ResourcePolicy.BudgetOf(plan) with { CurrentCommitBytes = gib };
            var first = new Response(true, "ok", [active], Sample(8 * gib), "epoch", More: true, Budgets: [budget]);
            foreach (var mixed in new[] { false, true })
            {
                using var stream = new MemoryStream();
                Ipc.WriteAsync(stream, first, default).GetAwaiter().GetResult();
                Ipc.WriteAsync(stream, first with { More = false, Budgets = [budget with { CurrentCommitBytes = mixed ? 2 * gib : gib }] }, default).GetAwaiter().GetResult();
                stream.Position = 0;
                if (mixed)
                {
                    var rejected = false;
                    try { Ipc.ReadResponseAsync(stream, default).GetAwaiter().GetResult(); }
                    catch (IOException) { rejected = true; }
                    Assert(rejected, "mixed usage pages accepted");
                }
                else Assert(ResourcePolicy.ReservedFor(Ipc.ReadResponseAsync(stream, default).GetAwaiter().GetResult()).Single() == budget, "budget credit lost in paged status");
            }
            Assert(ResourcePolicy.ReservedFor(first with { Budgets = null }).Single().CurrentCommitBytes is null, "old monitor invented a live credit");
            var large = new ControlEntry { Plan = plan with { Name = new string('x', 1000000) }, State = "Started" };
            using var crowded = new MemoryStream();
            Ipc.WriteResponseAsync(crowded, first with { More = false, Entries = [large], Budgets = Enumerable.Range(0, 500).Select(_ => budget with { Id = Guid.NewGuid() }).ToArray() }, default).GetAwaiter().GetResult();
            crowded.Position = 0;
            var fallback = Ipc.ReadResponseAsync(crowded, default).GetAwaiter().GetResult();
            Assert(fallback.Entries!.Single().Plan.Name == large.Plan.Name && fallback.Budgets is null, "optional telemetry displaced an authority record");
        });
        test("ACC-R04 native Job current usage drops after free and exit while peak remains", () =>
        {
            var prefix = Path.Combine(temp, "usage-" + Guid.NewGuid().ToString("N"));
            using var root = new Job("Local\\AL-usage-root-" + Guid.NewGuid().ToString("N"), root: true);
            using var pool = new Job("Local\\AL-usage-pool-" + Guid.NewGuid().ToString("N"), 512 * ResourcePolicy.MiB);
            using var leaf = new Job("Local\\AL-usage-leaf-" + Guid.NewGuid().ToString("N"), 256 * ResourcePolicy.MiB);
            using var child = new ProtectedProcess(new(Environment.ProcessPath!, ["--memory-cycle", prefix], "exe"), temp, [root, pool, leaf], createNoWindow: true);
            try
            {
                child.Resume();
                Wait(() => File.Exists(prefix + ".allocated"), "allocation did not complete");
                var usage = JobUsageReader.Read(leaf);
                var pooled = pool.QueryCurrentCommit();
                Assert(usage.CommitBytes >= 64 * ResourcePolicy.MiB, "Job current commit query unsupported or understated");
                Assert(usage.PrivateWorkingSetBytes >= 64 * ResourcePolicy.MiB, "private working set unsupported or understated");
                Assert(pooled >= usage.CommitBytes, "parent pool omitted descendants");
                var peak = leaf.QueryMemory()!.PeakBytes;
                File.WriteAllText(prefix + ".free", "free");
                Wait(() => File.Exists(prefix + ".freed"), "allocation was not freed");
                var after = leaf.QueryCurrentCommit();
                Assert(after.HasValue && after.Value + 48 * ResourcePolicy.MiB < usage.CommitBytes, "current charge still reports peak after free");
                Assert(leaf.QueryMemory()!.PeakBytes >= peak, "historical peak vanished");
                File.WriteAllText(prefix + ".exit", "exit");
                Assert(child.Process.WaitForExit(10000) && child.Process.ExitCode == 0, "fixture did not exit cleanly");
                Wait(() => leaf.Members().Length == 0, "Job did not drain");
                Assert(JobUsageReader.Read(leaf).CommitBytes == 0 && JobUsageReader.Read(pool).CommitBytes == 0, "empty Job retained live credit");
                Assert(JobUsageReader.Read(leaf).PrivateWorkingSetBytes == 0, "empty Job retained physical credit");
            }
            finally
            {
                if (!child.Process.HasExited) { child.Process.Kill(true); child.Process.WaitForExit(5000); }
            }
        });
    }

    private static void Wait(Func<bool> condition, string error)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > 10000) throw new Exception(error); Thread.Sleep(25); }
    }
    private static void Assert(bool condition, string error) { if (!condition) throw new Exception(error); }
}
