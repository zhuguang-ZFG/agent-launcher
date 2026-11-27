using AgentLauncher.Core;
using AgentLauncher.Windows;

// 端到端回归：UI/planner（Draft + RemoteHost → WorkspacePreparer）→ monitor admission（reserve/begin）
// → SSH 进程启动与 lifecycle reconciliation。用 INTEGRATION_TESTS 构建 + fake SSH 运行，
// 与 R10-R12（手工构造 LaunchItem）不同，这里使用真实的 planner 输出（Budget=0、无本地资源字段），
// 正是缺失的那一环：过去 reserve/ValidateLaunchItem 会拒绝 Budget=0 的远程计划。
internal static class RemoteE2EIntegration
{
    public static async Task<int> RunAsync()
    {
        var testRoot = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "";
        if (!testRoot.Contains("AgentLauncher-tests-"))
            throw new InvalidOperationException("Remote E2E requires an isolated AGENT_LAUNCHER_TEST_ROOT.");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") != "1")
            throw new InvalidOperationException("Remote E2E requires AGENT_LAUNCHER_FAKE_SAMPLE=1.");

        var fakeSshPath = Path.Combine(AppContext.BaseDirectory, "ssh.exe");
        if (!File.Exists(fakeSshPath)) throw new FileNotFoundException("fake SSH not found", fakeSshPath);
        var fakeSshDir = Path.GetDirectoryName(fakeSshPath)!;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeSshDir + Path.PathSeparator + originalPath);
        // 同步模式 None 自三台同权起在准备层拒收：E2E 改用 Mutagen 模式，由假同步后端代替真实 Mutagen（子 Worker 继承本环境）。
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC", "1");

        var workerProcesses = new List<System.Diagnostics.Process>();
        var evidence = new List<object>();
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); evidence.Add(new { name, passed = true }); }
            catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); evidence.Add(new { name, passed = false, error = e.ToString() }); }
        }
        LaunchItem? planned = null;

        try
        {
            Directory.CreateDirectory(Store.Root);
            var cwd = Path.Combine(Store.Root, "remote-e2e-cwd");
            Directory.CreateDirectory(cwd);

            // UI/planner 输入：配置窗口保存的 Draft，含远程主机 + 一个远程槽位。
            var host = new RemoteHost(
                Id: Guid.NewGuid(),
                Name: "E2E VPS",
                Address: "fake.example.com",
                SshPort: 22,
                SshUser: "testuser",
                AgentUser: "agent",
                AvailableAgents: ["omp"],
                SshKeyPath: null,
                WorkspaceSyncMode: WorkspaceSyncMode.Mutagen);  // None 在准备层被拒收（三台同权）；假同步后端代替真实 Mutagen，这里只验启动链
            var configuration = new Configuration { SchemaVersion = 2 };
            configuration.Draft.SchemaVersion = 2;
            configuration.Draft.Revision = 3;
            configuration.Draft.Project = cwd;
            configuration.Draft.DirectoryMode = "shared";
            configuration.RemoteHosts = [host];
            var slot = configuration.Draft.Slots[0];
            slot.Enabled = true;
            slot.AgentId = "omp";
            slot.CustomName = "E2E Remote Omp";
            slot.RemoteHostId = host.Id;
            slot.RemoteDirectory = "/home/agent/work/e2e";
            slot.Profiles["omp"] = new AgentProfile { Executable = Environment.ProcessPath!, Arguments = ["hold=8"] };
            foreach (var other in configuration.Draft.Slots.Skip(1)) other.Enabled = false;

            // 先把权威配置落盘，PolicyHash/DraftRevision 才能与 reserve 绑定一致。
            Store.Save(Store.FilePath("configuration.json"), configuration);
            var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
            var initial = await Ipc.EnsureMonitorAsync();
            Assert(initial.Ok, "Monitor start failed: " + initial.Message);

            await Test("E2E-R01 planner emits remote plan with no local resource fields", async () =>
            {
                var preparer = new WorkspacePreparer(Path.Combine(Store.Root, "worktrees"));
                var plans = await preparer.PrepareAsync(configuration.Draft, _ => { }, _ => Task.FromResult(false),
                    CancellationToken.None, configuration: configuration);
                Assert(plans.Count == 1, $"Expected 1 plan, got {plans.Count}");
                var plan = plans[0];
                Assert(plan.RemoteExecution is not null, "Planner output missing RemoteExecution spec");
                var remote = plan.RemoteExecution!;
                Assert(remote.Host == host.Address && remote.SshUser == host.SshUser && remote.AgentUser == host.AgentUser,
                    "RemoteExecution host identity mismatch");
                Assert(remote.AgentType == "omp" && remote.RemoteWorkingDirectory == slot.RemoteDirectory,
                    "RemoteExecution agent/working directory mismatch");
                Assert(remote.Arguments.Contains("hold=8"), "Planner lost resolved agent arguments");
                Assert(plan.PlanningBytes is null && plan.ResourceMode is null && plan.PoolId is null && plan.Budget == 0,
                    "Remote plan must not carry local resource fields");
                planned = plan;
            });

            await Test("E2E-R02 reserve accepts real planner output (Budget=0 remote plan)", async () =>
            {
                Assert(planned is not null, "E2E-R01 did not produce a plan");
                var plan = planned!;
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan],
                    Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy),
                    PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                Assert(reserved.Ok, "Reserve rejected planner-generated remote plan: " + reserved.Message
                    + " (regression: remote plans must be exempt from local budget validation)");
                Assert(reserved.Entries is { Length: 1 } && reserved.Entries[0].State == "Pending",
                    "Reserve did not return one Pending entry");
            });

            await Test("E2E-R03 remote entry is not charged to local admission budgets", async () =>
            {
                Assert(planned is not null, "E2E-R01 did not produce a plan");
                var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status.Ok, "Status failed: " + status.Message);
                var remoteBudget = status.Budgets?.FirstOrDefault(b => b.Id == planned!.Id);
                // Either representation is acceptable: omitted, or present as an explicitly zero-cost Remote item.
                if (remoteBudget is not null)
                {
                    Assert(remoteBudget.Remote, "Remote plan in status budgets must be tagged Remote");
                    Assert(remoteBudget.PlanningBytes == 0 && remoteBudget.JobMemoryLimit == 0,
                        "Remote plan must carry zero local planning/Job budget");
                }
            });
            await Test("E2E-R04 begin/SSH start keeps Started with remote detail", async () =>
            {
                Assert(planned is not null, "E2E-R01 did not produce a plan");
                var plan = planned!;
                var workerStart = new System.Diagnostics.ProcessStartInfo(Store.WorkerPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                workerStart.ArgumentList.Add("pane");
                workerStart.ArgumentList.Add(plan.Id.ToString());
                workerStart.Environment["AGENT_LAUNCHER_TEST_ROOT"] = Store.Root;
                workerStart.Environment["PATH"] = fakeSshDir + Path.PathSeparator + originalPath;
                var worker = System.Diagnostics.Process.Start(workerStart)!;
                worker.StandardInput.Close();
                workerProcesses.Add(worker);

                await Task.Delay(1500);
                var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status.Ok, "Status failed: " + status.Message);
                var entry = status.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
                Assert(entry is not null, "Remote entry missing from status after worker start");
                Assert(entry!.State == "Started", $"Expected Started while SSH alive, got {entry.State}; detail={entry.Detail}");
                Assert(entry.Detail.Contains("SSH") && entry.Detail.Contains("不受本地资源限制"),
                    "Remote start detail must disclose SSH session and local-resource exemption, got: " + entry.Detail);
            });

            await Test("E2E-R05 SSH exit transitions to a terminal state", async () =>
            {
                Assert(planned is not null, "E2E-R01 did not produce a plan");
                var plan = planned!;
                // planner 参数 hold=8；fake SSH 正常退出（0）→ 终态。
                await Task.Delay(8500);
                var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status.Ok, "Status failed: " + status.Message);
                var entry = status.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
                Assert(entry is not null, "Remote entry missing from status after SSH exit");
                Assert(entry!.State is "Exited" or "Settled",
                    $"Expected terminal state after SSH exit, got {entry.State}; detail={entry.Detail}");
            });

            await Test("E2E-R06 local budget validation is NOT weakened by the remote exemption", async () =>
            {
                // 正控：本地计划（无 RemoteExecution）Budget=0 仍必须被拒绝，防止豁免扩散到本地路径。
                var localCwd = Path.Combine(Store.Root, "remote-e2e-local-cwd");
                Directory.CreateDirectory(localCwd);
                var local = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "local-zero-budget", localCwd,
                    PhysicalDirectory.Workspace(localCwd), false,
                    new(Environment.ProcessPath!, ["--hold", "1"], "exe"), 0, AgentId: "codex");
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [local],
                    Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy),
                    PlanDigest: ResourcePolicy.PlanDigest([local]), DraftRevision: 3));
                Assert(!reserved.Ok, "Local plan with Budget=0 must still be rejected; remote exemption leaked");
            });

            await Test("E2E-R07 reserve rejects remote plan whose arguments carry Windows paths", async () =>
            {
                Assert(planned is not null, "E2E-R01 did not produce a plan");
                var bad = planned! with
                {
                    Id = Guid.NewGuid(),
                    Command = planned.Command with { Arguments = ["--add-dir", "D:\\work"] },
                    RemoteExecution = planned.RemoteExecution! with { Arguments = ["--add-dir", "D:\\work"] }
                };
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [bad],
                    Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy),
                    PlanDigest: ResourcePolicy.PlanDigest([bad]), DraftRevision: 3));
                Assert(!reserved.Ok, "Remote plan with Windows path arguments must be rejected");
                Assert(reserved.Message.Contains("Windows"), "Rejection must name the Windows-path problem, got: " + reserved.Message);
            });

            Store.Save(Path.Combine(Store.Root, "remote-e2e-results.json"), evidence);
            return evidence.Any(e => e.GetType().GetProperty("passed")!.GetValue(e) is false) ? 1 : 0;
        }
        finally
        {
            foreach (var worker in workerProcesses)
            {
                try
                {
                    if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(5000); }
                    worker.Dispose();
                }
                catch { /* already exited */ }
            }
            try
            {
                var pid = Store.Read<int>(Store.FilePath("monitor-pid.json"));
                if (pid > 0)
                {
                    using var monitor = System.Diagnostics.Process.GetProcessById(pid);
                    monitor.Kill(); monitor.WaitForExit(5000);
                }
            }
            catch { /* monitor may not be running */ }
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
