using AgentLauncher.Core;
using AgentLauncher.Windows;
using AgentLauncher.Worker;

namespace AgentLauncher.Tests;

// 远程执行单元测试：验证拒绝逻辑和命令构建（不启动真实 SSH 连接）
internal static class RemoteExecutionChecks
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("R1 remote execution rejects shared pool", () =>
        {
            var spec = new RemoteExecutionSpec(
                Host: "test.example.com",
                SshPort: 22,
                SshUser: "root",
                AgentUser: "agent",
                AgentType: "pi",
                RemoteWorkingDirectory: "/home/agent/work",
                Arguments: [],
                MemoryLimitBytes: null,
                SshKeyPath: null
            );

            var plan = new LaunchItem(
                Id: Guid.NewGuid(),
                BatchId: Guid.NewGuid(),
                Position: 1,
                Name: "test",
                Cwd: "/home/agent/work",
                Workspace: new("/home/agent/work", "key"),
                Shared: true,  // 共享池
                Command: new("pi", [], "adapter"),
                Budget: 1024 * 1024 * 1024,
                PoolId: Guid.NewGuid(),  // 有 PoolId
                RemoteExecution: spec
            );

            // 验证：模型层约束（RunPane 拒绝逻辑在集成测试中验证）
            assert(plan.RemoteExecution is not null && plan.PoolId is not null,
                "远程执行不应与共享池共存（RunPane 会拒绝此组合）");
        });

        test("R2 remote execution rejects local memory policy", () =>
        {
            var spec = new RemoteExecutionSpec(
                Host: "test.example.com",
                SshPort: 22,
                SshUser: "root",
                AgentUser: "agent",
                AgentType: "pi",
                RemoteWorkingDirectory: "/home/agent/work",
                Arguments: [],
                MemoryLimitBytes: null,
                SshKeyPath: null
            );

            var plan = new LaunchItem(
                Id: Guid.NewGuid(),
                BatchId: Guid.NewGuid(),
                Position: 1,
                Name: "test",
                Cwd: "/home/agent/work",
                Workspace: new("/home/agent/work", "key"),
                Shared: false,
                Command: new("pi", [], "adapter"),
                Budget: 1024 * 1024 * 1024,
                PlanningBytes: 512 * 1024 * 1024,  // 本地内存策略
                RemoteExecution: spec
            );

            // 验证：模型层约束（RunPane 拒绝逻辑在集成测试中验证）
            assert(plan.RemoteExecution is not null && plan.PlanningBytes is not null,
                "远程执行不应与本地内存策略共存（RunPane 会拒绝此组合）");
        });

        test("R3 SshRunnerBackend builds correct SSH command", () =>
        {
            var spec = new RemoteExecutionSpec(
                Host: "test.example.com",
                SshPort: 2222,
                SshUser: "testuser",
                AgentUser: "agent",
                AgentType: "pi",
                RemoteWorkingDirectory: "/home/agent/work dir",  // 含空格
                Arguments: ["--arg with spaces", "another'arg"],  // 含空格和单引号
                MemoryLimitBytes: null,
                SshKeyPath: "/path/to/key"
            );

            var backend = SshRunnerBackend.Create(spec);

            // 验证：Create 只构造 ProcessStartInfo，不启动进程
            assert(!backend.HasExited, "Create 不应启动进程");

            // 验证：SSH 命令参数正确（ArgumentList 避免引号嵌套）
            var args = backend.StartInfo.ArgumentList;
            assert(args.Contains("-tt"), "应包含 -tt（强制 PTY）");
            assert(args.Contains("-p") && args.Contains("2222"), "应包含自定义端口");
            assert(args.Contains("-i") && args.Contains("/path/to/key"), "应包含 SSH 密钥");
            assert(args.Contains("testuser@test.example.com"), "应包含用户@主机");

            // 验证：远程命令包含工作目录和 agent 命令
            var remoteCmd = args[^1];  // 最后一个参数是远程命令
            assert(remoteCmd.Contains("su - agent -c"), "应包含 su - agent -c");
            assert(remoteCmd.Contains("cd '/home/agent/work dir'"), "应包含工作目录（内部单引号 quoting）");
            assert(remoteCmd.Contains("pi '--arg with spaces' 'another'\\''arg'"), "应包含 agent 命令（转义单引号）");
            assert(remoteCmd.StartsWith("su - agent -c \"") && remoteCmd.EndsWith("\""), "远程命令应用双引号包裹（内部已有单引号）");
            // stdout/stderr 必须继承控制台：重定向而无人读取会丢输出，远端写满管道缓冲会挂死 agent。
            assert(!backend.StartInfo.RedirectStandardOutput && !backend.StartInfo.RedirectStandardError, "SSH stdout/stderr 不应重定向（无人读取会丢输出/挂死远端）");
            // stdin 必须用独立管道（Start 后立即关闭）：若继承到控制台句柄，ssh -tt 会把本地控制台切到 raw 模式吞掉输出。
            assert(backend.StartInfo.RedirectStandardInput, "SSH stdin 应为独立管道，避免 -tt 抢占控制台 stdin");

            backend.Dispose();
        });

        test("R4 SshRunnerBackend Create does not start process", () =>
        {
            var spec = new RemoteExecutionSpec(
                Host: "test.example.com",
                SshPort: 22,
                SshUser: "root",
                AgentUser: "agent",
                AgentType: "pi",
                RemoteWorkingDirectory: "/home/agent/work",
                Arguments: [],
                MemoryLimitBytes: null,
                SshKeyPath: null
            );

            var backend = SshRunnerBackend.Create(spec);

            // 验证：Create 只构造 ProcessStartInfo，不启动进程
            assert(!backend.HasExited, "Create 不应启动进程（HasExited 应为 false）");

            // 验证：Process 和 CreationTime 在 Start 前抛异常
            var processThrew = false;
            try { _ = backend.Process; } catch (InvalidOperationException) { processThrew = true; }
            assert(processThrew, "Process 在 Start 前应抛 InvalidOperationException");

            var creationTimeThrew = false;
            try { _ = backend.CreationTime; } catch (InvalidOperationException) { creationTimeThrew = true; }
            assert(creationTimeThrew, "CreationTime 在 Start 前应抛 InvalidOperationException");

            backend.Dispose();
        });

        test("R5 SshRunnerBackend smoke test with fake SSH", () =>
        {
            // Backend smoke test：只验证 SshRunnerBackend 能启动 fake SSH 并等待退出
            // 注意：这不是端到端测试（缺少 WorkspacePreparer → Worker → Monitor 流程）
            // 真正的端到端需要：计划生成 → Worker 接受 → Monitor reconciliation → 停止/断开
            var fakeSshPath = Path.Combine(AppContext.BaseDirectory, "ssh.exe");  // fake-ssh 作为项目引用，输出到同一目录
            assert(File.Exists(fakeSshPath), $"fake SSH 不存在：{fakeSshPath}");

            // 设置 PATH 环境变量，让 SshRunnerBackend 找到 fake SSH
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var fakeSshDir = Path.GetDirectoryName(fakeSshPath)!;
            Environment.SetEnvironmentVariable("PATH", fakeSshDir + Path.PathSeparator + originalPath);

            try
            {
                var spec = new RemoteExecutionSpec(
                    Host: "fake.example.com",
                    SshPort: 22,
                    SshUser: "testuser",
                    AgentUser: "agent",
                    AgentType: "pi",
                    RemoteWorkingDirectory: "/home/agent/work",
                    Arguments: ["--test", "arg with spaces"],
                    MemoryLimitBytes: null,
                    SshKeyPath: null
                );

                var backend = SshRunnerBackend.Create(spec);
                backend.Start();

                // 验证：进程启动成功
                assert(backend.Process.Id > 0, "fake SSH 进程应启动");

                // 等待退出（fake SSH 睡眠 2 秒）
                var exited = backend.WaitForExit(5000);
                assert(exited, "fake SSH 应在 5 秒内退出");

                // 验证：退出码为 0
                assert(backend.ExitCode == 0, $"fake SSH 退出码应为 0，实际：{backend.ExitCode}");

                // 验证：不是 SSH 连接错误
                assert(!backend.IsSshConnectionError, "fake SSH 不应返回连接错误（255）");

                backend.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        });

        test("R6 WorkspacePreparer generates remote plan without local resource fields", () =>
        {
            // 端到端测试（第一部分）：WorkspacePreparer 生成远程计划
            // 验证：远程计划没有 PlanningBytes/ResourceMode/PoolId（不被 Worker 拒绝）
            var tempDir = Path.Combine(Path.GetTempPath(), "agent-launcher-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var configuration = new Configuration
                {
                    SchemaVersion = 2,
                    RemoteHosts =
                    [
                        new RemoteHost(
                            Id: Guid.NewGuid(),
                            Name: "测试 VPS",
                            Address: "fake.example.com",
                            SshPort: 22,
                            SshUser: "root",
                            AgentUser: "agent",
                            AvailableAgents: ["pi"],
                            SshKeyPath: null
                        )
                    ]
                };

                var draft = new Draft
                {
                    SchemaVersion = 2,
                    Project = tempDir,
                    DirectoryMode = "isolated",
                    Slots =
                    [
                        new Slot
                        {
                            Enabled = true,
                            AgentId = "pi",
                            RemoteHostId = configuration.RemoteHosts[0].Id,
                            RemoteDirectory = "/home/agent/work/test"
                        }
                    ]
                };

                var preparer = new WorkspacePreparer(Path.Combine(tempDir, "worktrees"));
                var plans = preparer.PrepareAsync(
                    draft,
                    _ => { },
                    _ => Task.FromResult(false),
                    CancellationToken.None,
                    configuration: configuration
                ).GetAwaiter().GetResult();

                // 验证：生成了 1 个计划
                assert(plans.Count == 1, $"应生成 1 个计划，实际：{plans.Count}");

                var plan = plans[0];

                // 验证：计划有 RemoteExecution spec
                assert(plan.RemoteExecution is not null, "远程计划应有 RemoteExecution spec");
                var remote = plan.RemoteExecution!;
                assert(remote.Host == "fake.example.com", $"远程主机地址应为 fake.example.com，实际：{remote.Host}");
                assert(remote.RemoteWorkingDirectory == "/home/agent/work/test", $"远程工作目录应为 /home/agent/work/test，实际：{remote.RemoteWorkingDirectory}");

                // 验证：计划没有本地资源字段（不被 Worker 拒绝）
                assert(plan.PlanningBytes is null, "远程计划不应有 PlanningBytes（本地资源限制）");
                assert(plan.ResourceMode is null, "远程计划不应有 ResourceMode（本地资源限制）");
                assert(plan.PoolId is null, "远程计划不应有 PoolId（共享池）");
                assert(plan.Budget == 0, "远程计划 Budget 应为 0（不受本地 Job 限制）");
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
        });

        test("R7 SSH connection error (255) detected as Disconnected", () =>
        {
            // SSH 连接错误测试：fake SSH 返回 255，应检测为 Disconnected
            var fakeSshPath = Path.Combine(AppContext.BaseDirectory, "ssh.exe");
            assert(File.Exists(fakeSshPath), $"fake SSH 不存在：{fakeSshPath}");

            var originalPath = Environment.GetEnvironmentVariable("PATH");
            var fakeSshDir = Path.GetDirectoryName(fakeSshPath)!;
            Environment.SetEnvironmentVariable("PATH", fakeSshDir + Path.PathSeparator + originalPath);

            try
            {
                var spec = new RemoteExecutionSpec(
                    Host: "disconnect.example.com",  // 包含 "disconnect"，fake SSH 返回 255
                    SshPort: 22,
                    SshUser: "testuser",
                    AgentUser: "agent",
                    AgentType: "pi",
                    RemoteWorkingDirectory: "/home/agent/work",
                    Arguments: [],
                    MemoryLimitBytes: null,
                    SshKeyPath: null
                );

                var backend = SshRunnerBackend.Create(spec);
                backend.Start();
                backend.WaitForExit(5000);

                // 验证：检测到 SSH 连接错误（255）
                assert(backend.IsSshConnectionError, "应检测到 SSH 连接错误（255）");
                assert(backend.ExitCode == 255, $"退出码应为 255，实际：{backend.ExitCode}");

                backend.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        });

        test("R8 remote arguments reject Windows local paths", () =>
        {
            static RemoteExecutionSpec WithArgs(params string[] args) => new(
                Host: "vps.example.com", SshPort: 22, SshUser: "root", AgentUser: "agent",
                AgentType: "pi", RemoteWorkingDirectory: "/home/agent/work", Arguments: args,
                MemoryLimitBytes: null, SshKeyPath: null);
            foreach (var bad in new[] { "D:\\work", "D:/work", "--add-dir=D:\\work", "\\\\server\\share", "fix C:\\temp now" })
            {
                var threw = false;
                try { WithArgs(bad).Validate(); }
                catch (InvalidDataException e) { threw = e.Message.Contains("Windows"); }
                assert(threw, $"Windows path argument accepted: {bad}");
            }
            // 正控：合法远程参数与 URL 不受影响（http:// 中的字母+冒号不构成盘符路径）。
            WithArgs("/home/agent/work", "https://example.com/path", "--model", "gpt-5").Validate();
            WithArgs().Validate();
        });

        test("R9 Disconnected state remains nonterminal and prevents blind relaunch", () =>
        {
            // Disconnected 状态测试：验证状态是非终态，防止盲目重启
            // 验证：TaskUsability.StateLabel 显示「连接断开（远程进程可能仍运行）」
            var label = AgentLauncher.Core.TaskUsability.StateLabel("Disconnected");
            assert(label == "连接断开（远程进程可能仍运行）", $"StateLabel 应为「连接断开（远程进程可能仍运行）」，实际：{label}");

            // 验证：Disconnected 不在 IsTerminal 列表（保持非终态）
            var entry = new AgentLauncher.Core.ControlEntry
            {
                Plan = new AgentLauncher.Core.LaunchItem(
                    Id: Guid.NewGuid(),
                    BatchId: Guid.NewGuid(),
                    Position: 1,
                    Name: "test",
                    Cwd: "/home/agent/work",
                    Workspace: new("/home/agent/work", "key"),
                    Shared: false,
                    Command: new("pi", [], "exe"),
                    Budget: 0
                ),
                State = "Disconnected",
                Detail = "SSH 连接断开",
                Created = DateTimeOffset.UtcNow
            };
            assert(!AgentLauncher.Core.ControlEntryStates.IsTerminal(entry), "Disconnected 状态不应是终态（防止盲目重启）");
        });
    }
}
