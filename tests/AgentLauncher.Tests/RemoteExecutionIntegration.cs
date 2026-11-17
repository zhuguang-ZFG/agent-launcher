using AgentLauncher.Core;
using AgentLauncher.Windows;

// 远程执行集成测试：用真实 reserve/begin + fake SSH 长驻进程验证 Monitor reconciliation
internal static class RemoteExecutionIntegration
{
    public static async Task<int> RunAsync()
    {
        if (!(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "").Contains("AgentLauncher-tests-"))
            throw new InvalidOperationException("Remote execution integration requires an isolated test root.");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") != "1")
            throw new InvalidOperationException("Remote execution integration requires AGENT_LAUNCHER_FAKE_SAMPLE=1.");
        
        // 设置 fake SSH 路径
        var fakeSshPath = Path.Combine(AppContext.BaseDirectory, "ssh.exe");
        if (!File.Exists(fakeSshPath)) throw new InvalidOperationException($"fake SSH not found: {fakeSshPath}");
        var fakeSshDir = Path.GetDirectoryName(fakeSshPath)!;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeSshDir + Path.PathSeparator + originalPath);
        var workerProcesses = new List<System.Diagnostics.Process>();  // 跟踪 Worker 进程用于清理
        
        try
        {
            Directory.CreateDirectory(Store.Root);
            var evidence = new List<object>();
            void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
            async Task Test(string name, Func<Task> action)
            {
                try { await action(); Console.WriteLine("PASS " + name); evidence.Add(new { name, passed = true }); }
                catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); evidence.Add(new { name, passed = false, error = e.ToString() }); }
            }

            var cwd = Path.Combine(Store.Root, "remote-cwd");
            Directory.CreateDirectory(cwd);
            
            // 启动 Monitor（必须先于 reserve/begin）
            var initial = await Ipc.EnsureMonitorAsync();
            Assert(initial.Ok, "Monitor start failed: " + initial.Message);
            
            // 保存配置（reserve 需要 PolicyHash 匹配）
            var authorityConfig = new Configuration { SchemaVersion = 2 };
            authorityConfig.Draft.SchemaVersion = 2;
            authorityConfig.Draft.Revision = 3;
            authorityConfig.Draft.Project = cwd;
            authorityConfig.Draft.DirectoryMode = "isolated";
            authorityConfig.Draft.Slots = [new Slot { Enabled = true, AgentId = "pi" }];
            Store.Save(Store.FilePath("configuration.json"), authorityConfig);
            
            await Test("R23 SSH alive keeps Started, exit transitions to Settled", async () =>
            {
                // 创建远程计划
                var remoteHost = new RemoteHost(
                    Id: Guid.NewGuid(),
                    Name: "测试 VPS",
                    Address: "fake.example.com",
                    SshPort: 22,
                    SshUser: "testuser",
                    AgentUser: "agent",
                    AvailableAgents: ["pi"],
                    SshKeyPath: null
                );
                
                var plan = new LaunchItem(
                    Id: Guid.NewGuid(),
                    BatchId: Guid.NewGuid(),
                    Position: 1,
                    Name: "remote-test",
                    Cwd: cwd,
                    Workspace: PhysicalDirectory.Workspace(cwd),
                    Shared: false,
                    Command: new("pi", ["hold=10"], "remote"),  // 与真实 planner 合同一致：远端 Agent 名 + remote adapter
                    Budget: 0,  // 远程计划不携带本地 Job 预算（ValidateLaunchItem 强制为 0）
                    RemoteExecution: new RemoteExecutionSpec(
                        Host: remoteHost.Address,
                        SshPort: remoteHost.SshPort,
                        SshUser: remoteHost.SshUser,
                        AgentUser: remoteHost.AgentUser,
                        AgentType: "pi",
                        RemoteWorkingDirectory: "/home/agent/work",
                        Arguments: ["hold=10"],
                        MemoryLimitBytes: null,
                        SshKeyPath: null
                    )
                );
                
                // Reserve
                var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                Assert(reserved.Ok, "Remote plan reserve failed: " + reserved.Message);
                Assert(reserved.Entries is { Length: 1 }, "Reserve should return 1 entry");
                
                var entry = reserved.Entries![0];
                Assert(entry.State == "Pending", $"Initial state should be Pending, got: {entry.State}");
                
                // 启动 Worker 进程（pane 模式，真正执行远程 agent；Worker 自己请求 begin 领取授权）
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
                var workerProcess = System.Diagnostics.Process.Start(workerStart)!;
                workerProcess.StandardInput.Close();
                workerProcesses.Add(workerProcess);  // 跟踪用于清理
                Console.WriteLine($"DEBUG: Worker process started (PID: {workerProcess.Id})");
                
                // 等待 Started
                await Task.Delay(1000);
                var status1 = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status1.Ok, "Status failed: " + status1.Message);
                var entry1 = status1.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
                Assert(entry1 is not null, "Entry not found in status");
                Console.WriteLine($"DEBUG: entry1.State = {entry1!.State}"); Console.WriteLine($"DEBUG: entry1.Detail = {entry1.Detail}"); Assert(entry1!.State == "Started", $"State should be Started while SSH alive, got: {entry1.State}");
                
                // 等待 fake SSH 完成（10 秒）
                await Task.Delay(11000);
                var status2 = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status2.Ok, "Status failed: " + status2.Message);
                var entry2 = status2.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
                Assert(entry2 is not null, "Entry not found in status");
                
                // fake SSH 正常退出（exit 0），应该是 Exited/Settled
                Assert(entry2!.State is "Exited" or "Settled", $"State should be Exited/Settled after SSH exit, got: {entry2.State}");
                
                // 等待 Worker 进程退出（确保授权释放，避免影响下一个测试）
                Assert(workerProcess.WaitForExit(5000), "Worker process did not exit after SSH completion");
                Console.WriteLine($"DEBUG: R10 Worker exited with code {workerProcess.ExitCode}");
            });
            
            await Test("R24 SSH 255 transitions to Disconnected", async () =>
            {
                // 创建远程计划（SSH 255 断连）
                var plan = new LaunchItem(
                    Id: Guid.NewGuid(),
                    BatchId: Guid.NewGuid(),
                    Position: 1,
                    Name: "remote-disconnect-test",
                    Cwd: cwd,
                    Workspace: PhysicalDirectory.Workspace(cwd),
                    Shared: false,
                    Command: new("pi", ["hold=2", "disconnect"], "remote"),  // fake SSH 等 2 秒后返回 255
                    Budget: 0,  // 远程计划不携带本地 Job 预算（ValidateLaunchItem 强制为 0）
                    RemoteExecution: new RemoteExecutionSpec(
                        Host: "disconnect.example.com",
                        SshPort: 22,
                        SshUser: "testuser",
                        AgentUser: "agent",
                        AgentType: "pi",
                        RemoteWorkingDirectory: "/home/agent/work",
                        Arguments: ["disconnect"],
                        MemoryLimitBytes: null,
                        SshKeyPath: null
                    )
                );
                
                // Reserve
                var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                Assert(reserved.Ok, "Remote plan reserve failed: " + reserved.Message);
                
                // 启动 Worker 进程（pane 模式，真正执行远程 agent；Worker 自己请求 begin 领取授权）
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
                var workerProcess = System.Diagnostics.Process.Start(workerStart)!;
                workerProcess.StandardInput.Close();
                workerProcesses.Add(workerProcess);  // 跟踪用于清理
                
                // 等待 Started
                await Task.Delay(1000);
                var status1 = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status1.Ok, "Status failed: " + status1.Message);
                var entry1 = status1.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
                Assert(entry1 is not null, "Entry not found in status");
                Console.WriteLine($"DEBUG: entry1.State = {entry1!.State}"); Console.WriteLine($"DEBUG: entry1.Detail = {entry1.Detail}"); Assert(entry1!.State == "Started", $"State should be Started while SSH alive, got: {entry1.State}");
                
                // 等待 SSH 断连（fake SSH 立即返回 255）
                await Task.Delay(3000);
                var status2 = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                Assert(status2.Ok, "Status failed: " + status2.Message);
                var entry2 = status2.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
                Assert(entry2 is not null, "Entry not found in status");
                
                // SSH 255 断连，应该是 Disconnected
                Assert(entry2!.State == "Disconnected", $"State should be Disconnected after SSH 255, got: {entry2.State}");
            });
            
            await Test("R25 crash-before-Started maps to Disconnected", async () =>
            {
                // Table-driven: SpawnIntent, CreatedSuspended, ResumeIntent (all crash-before-Started phases)
                foreach (var phase in new[] { "SpawnIntent", "CreatedSuspended", "ResumeIntent" })
                {
                    // 用独立的 cwd
                    var r25Cwd = Path.Combine(Store.Root, $"remote-crash-{phase}-cwd");
                    Directory.CreateDirectory(r25Cwd);
                    
                    // 创建远程计划
                    var plan = new LaunchItem(
                        Id: Guid.NewGuid(),
                        BatchId: Guid.NewGuid(),
                        Position: 1,
                        Name: $"remote-crash-{phase}-test",
                        Cwd: r25Cwd,
                        Workspace: PhysicalDirectory.Workspace(r25Cwd),
                        Shared: false,
                        Command: new("pi", ["hold=10"], "remote"),
                        Budget: 0,
                        RemoteExecution: new RemoteExecutionSpec(
                            Host: "crash.example.com",
                            SshPort: 22,
                            SshUser: "test",
                            AgentUser: "agent",
                            AgentType: "pi",
                            RemoteWorkingDirectory: "/home/agent/work",
                            Arguments: ["hold=10"],
                            MemoryLimitBytes: null,
                            SshKeyPath: null
                        )
                    );
                    
                    // Reserve
                    var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
                    var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                    Assert(reserved.Ok, $"Reserve failed for {phase}: " + reserved.Message);
                    
                    // 模拟 crash-before-Started：写入 phase receipt（0,0）然后将 entry 状态改为 Creating（模拟 begin 后 crash）
                    Store.Save(Store.ReceiptPath(plan.Id), new Receipt(phase, 0, 0, null, $"准备启动远程进程（{phase}）", DateTimeOffset.UtcNow));
                    
                    // 停止 Monitor，修改 ledger，重启 Monitor（Monitor 会在 Refresh 时覆盖修改）
                    var monitorPid = Store.Read<int>(Store.FilePath("monitor-pid.json"));
                    if (monitorPid > 0)
                    {
                        using var monitorProcess = System.Diagnostics.Process.GetProcessById(monitorPid);
                        monitorProcess.Kill();
                        monitorProcess.WaitForExit(5000);
                    }
                    
                    // 将 entry 状态改为 Creating（reconciliation 只核对非 Pending 状态）
                    var ledger = Store.Read<Ledger>(Store.FilePath("control.json"))!;
                    var ledgerEntry = ledger.Entries.FirstOrDefault(e => e.Plan.Id == plan.Id);
                    Assert(ledgerEntry is not null, $"Entry not found in ledger for {phase}");
                    ledgerEntry!.State = "Creating";
                    ledgerEntry.Digest = Store.Digest(plan);
                    Store.Save(Store.FilePath("control.json"), ledger);
                    
                    // 重启 Monitor（加载修改后的 ledger）
                    var restart = await Ipc.EnsureMonitorAsync();
                    Assert(restart.Ok, $"Monitor restart failed for {phase}: " + restart.Message);
                    
                    // 等待 Monitor 核对（Monitor 启动时会核对所有非终止 entry）
                    await Task.Delay(2000);
                    var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                    Assert(status.Ok, $"Status failed for {phase}: " + status.Message);
                    var entry = status.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
                    Assert(entry is not null, $"Entry not found in status for {phase}");
                    
                    // crash-before-Started 应该映射到 Disconnected（远程进程可能存活），保持非终止
                    Assert(entry!.State == "Disconnected", $"State should be Disconnected after crash-before-Started ({phase}), got: {entry.State}");
                    Assert(entry.Detail.Contains("远程进程可能仍存活"), $"Detail should mention remote process may survive for {phase}, got: {entry.Detail}");
                }
            });
            
            await Test("R26 remote domain bypasses local pressure admission but not local-domain refusal", async () =>
            {
                // 分域回归：保守模式 + 压力锁定时，纯远程 reserve/begin 不受本地资源决策阻挡；
                // 同一监视器上本地计划仍被压力拒绝（豁免不扩散到本地域）。ACC-R07 反射夹具模式。
                await Task.CompletedTask;
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var monitorType = System.Reflection.Assembly.LoadFrom(Store.WorkerPath.Replace(".exe", ".dll", StringComparison.OrdinalIgnoreCase)).GetType("AgentLauncher.Worker.Monitor", true)!;
                object? Invoke(object monitor, string name, params object?[] args)
                {
                    try { return (monitorType.GetMethod(name, flags) ?? throw new MissingMethodException(name)).Invoke(monitor, args); }
                    catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                        throw;
                    }
                }
                void Set(object monitor, string field, object? value) => monitorType.GetField(field, flags)!.SetValue(monitor, value);
                T Get<T>(object monitor, string field) => (T)monitorType.GetField(field, flags)!.GetValue(monitor)!;

                var pressureConfig = new Configuration { SchemaVersion = 2 };
                pressureConfig.Draft.SchemaVersion = 2;
                pressureConfig.Draft.Revision = 3;
                pressureConfig.Draft.Project = cwd;
                pressureConfig.Draft.DirectoryMode = "shared";
                pressureConfig.Draft.Resource = new ResourceSettings { Mode = "conservative" };
                Store.Save(Store.FilePath("configuration.json"), pressureConfig);
                var sample = new MemorySnapshot(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
                var fixture = Activator.CreateInstance(monitorType, true)!;
                try
                {
                    Set(fixture, "ledger", new Ledger());
                    Set(fixture, "ledgerPath", Store.FilePath("remote-pressure-" + Guid.NewGuid().ToString("N") + ".json"));
                    Set(fixture, "configurationPath", Store.FilePath("configuration.json"));
                    Set(fixture, "sample", sample);
                    Set(fixture, "rootCpuRate", ResourcePolicy.DefaultCpuRate);
                    var tracker = Get<PressureTracker>(fixture, "pressureTracker");
                    for (var i = 0; i < 3; i++) tracker.Observe(sample with { CpuPercent = 95 }, ResourcePolicy.DefaultPhysicalFloor(sample.Total));
                    Assert(tracker.Pressure, "pressure fixture did not latch");

                    var r26Cwd = Path.Combine(Store.Root, "remote-pressure-cwd-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(r26Cwd);
                    var remotePlan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "remote-pressure", r26Cwd,
                        PhysicalDirectory.Workspace(r26Cwd), false, new CommandSpec("pi", [], "remote"), 0,
                        RemoteExecution: new RemoteExecutionSpec("pressure.example.com", 22, "root", "agent", "pi", "/home/agent/work", [], null, null));
                    var policy = ResourcePolicy.SnapshotFor(pressureConfig.Draft.Resource, sample.Total, sample.CommitLimit);
                    var reserved = (Response)Invoke(fixture, "Handle", new Request("reserve", Guid.NewGuid(), [remotePlan],
                        Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([remotePlan]), DraftRevision: 3))!;
                    Assert(reserved.Ok, "remote-only reserve was blocked by local pressure: " + reserved.Message);
                    var begun = (Response)Invoke(fixture, "Handle", new Request("begin", Guid.NewGuid(), Attempt: remotePlan.Id))!;
                    Assert(begun.Ok && begun.Entries![0].State == "Creating", "remote begin was blocked by local pressure: " + begun.Message);

                    var localCwd = Path.Combine(Store.Root, "local-pressure-cwd-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(localCwd);
                    var localPlan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "local-pressure", localCwd,
                        PhysicalDirectory.Workspace(localCwd), false, new CommandSpec(Environment.ProcessPath!, ["--hold", "1"], "exe"), ResourcePolicy.DefaultJobMemoryLimit);
                    string? localRefusal = null;
                    try
                    {
                        _ = Invoke(fixture, "Handle", new Request("reserve", Guid.NewGuid(), [localPlan],
                            Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([localPlan]), DraftRevision: 3));
                    }
                    catch (IOException e) { localRefusal = e.Message; }
                    Assert(localRefusal == "资源压力尚未连续恢复，稍后重新检查。", "local plan was not refused by latched pressure: " + (localRefusal ?? "<accepted>"));
                }
                finally
                {
                    ((IDisposable)fixture).Dispose();
                    var restoreConfig = new Configuration { SchemaVersion = 2 };
                    restoreConfig.Draft.SchemaVersion = 2;
                    restoreConfig.Draft.Revision = 3;
                    restoreConfig.Draft.Project = cwd;
                    restoreConfig.Draft.DirectoryMode = "isolated";
                    restoreConfig.Draft.Slots = [new Slot { Enabled = true, AgentId = "pi" }];
                    Store.Save(Store.FilePath("configuration.json"), restoreConfig);
                }
            });

            Store.Save(Path.Combine(Store.Root, "remote-execution-integration-results.json"), evidence);
            return evidence.Any(e => e.GetType().GetProperty("passed")!.GetValue(e) is false) ? 1 : 0;
        }
        finally
        {
            // 清理 Worker 进程（避免残留 pane/SSH 子进程）
            foreach (var workerProcess in workerProcesses)
            {
                try
                {
                    if (!workerProcess.HasExited)
                    {
                        workerProcess.Kill();
                        workerProcess.WaitForExit(5000);
                    }
                    workerProcess.Dispose();
                }
                catch { /* Process may have already exited */ }
            }
            
            // 停止 Monitor（避免残留进程）
            try
            {
                var pid = Store.Read<int>(Store.FilePath("monitor-pid.json"));
                if (pid > 0)
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch { /* Monitor may not be running */ }
            
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
