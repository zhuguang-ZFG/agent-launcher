using System.Diagnostics;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

// 真实 VPS 全链路验收（opt-in，不进 CI）：真实 planner → monitor reserve/begin → Worker pane → 真实 ssh → su agent → pi。
// 用法：--remote-live <address> <sshUser> <agentUser> <remoteDir> <keyPath|-> <argsJson> <timeoutSeconds>
// R45：--remote-live-memory <address> <sshUser> <agentUser> <remoteDir> <keyPath|-> <scope|oom|nonroot> <memoryLimitMib> <timeoutSeconds>
// 要求：全新隔离 AGENT_LAUNCHER_TEST_ROOT（含 AgentLauncher-tests-）；PATH 上是真实 ssh（不注入 fake）；
// 不要设置 AGENT_LAUNCHER_FAKE_SAMPLE（用真实资源读数做准入）。远程目录需已存在且 agent 用户可写。
internal static class RemoteLiveChecks
{
    public static async Task<int> RunAsync(string address, string sshUser, string agentUser, string remoteDir, string keyPath, string argsJson, string timeoutText)
    {
        var testRoot = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "";
        if (!testRoot.Contains("AgentLauncher-tests-") || Directory.Exists(testRoot))
            throw new IOException("Use a fresh isolated AgentLauncher-tests- directory.");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") == "1")
            throw new IOException("Live acceptance must use real resource samples; unset AGENT_LAUNCHER_FAKE_SAMPLE.");
        // Windows 进程搜索优先应用目录：发布输出里的集成测试假 ssh.exe 会顶替真实 ssh，制造假 PASS。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ssh.exe")))
            throw new IOException("Remove the fake ssh.exe from the runtime directory before live acceptance; it would shadow the real SSH client.");
        if (!int.TryParse(timeoutText, out var timeoutSeconds) || timeoutSeconds is < 10 or > 1800)
            throw new IOException("Timeout must be 10-1800 seconds.");
        var arguments = JsonSerializer.Deserialize<string[]>(argsJson) ?? throw new IOException("argsJson must be a JSON string array.");
        if (arguments.Length == 0) throw new IOException("Provide at least one agent argument.");
        var key = keyPath == "-" ? null : keyPath;
        if (key is not null && !File.Exists(key)) throw new IOException("SSH key not found: " + key);
        Directory.CreateDirectory(Store.Root);

        var host = new RemoteHost(Guid.NewGuid(), "Live VPS", address, 22, sshUser, agentUser, ["pi"], key, WorkspaceSyncMode.Mutagen);
        var configuration = new Configuration { SchemaVersion = 2 };
        configuration.Draft.SchemaVersion = 2;
        configuration.Draft.Revision = 3;
        configuration.Draft.Project = Path.Combine(Store.Root, "local-cwd");
        Directory.CreateDirectory(configuration.Draft.Project);
        configuration.Draft.DirectoryMode = "shared";
        configuration.RemoteHosts = [host];
        var slot = configuration.Draft.Slots[0];
        slot.Enabled = true;
        slot.AgentId = "pi";
        slot.CustomName = "Live Remote Pi";
        slot.RemoteHostId = host.Id;
        slot.RemoteDirectory = remoteDir;
        slot.Profiles["pi"] = new AgentProfile { Executable = "pi", Arguments = arguments };
        foreach (var other in configuration.Draft.Slots.Skip(1)) other.Enabled = false;
        Store.Save(Store.FilePath("configuration.json"), configuration);

        var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
        var initial = await Ipc.EnsureMonitorAsync();
        if (!initial.Ok) throw new IOException("Monitor start failed: " + initial.Message);

        var preparer = new WorkspacePreparer(Path.Combine(Store.Root, "worktrees"));
        var plans = await preparer.PrepareAsync(configuration.Draft, _ => { }, _ => Task.FromResult(false), CancellationToken.None, configuration: configuration);
        if (plans.Count != 1 || plans[0].RemoteExecution is null) throw new IOException("Planner did not emit one remote plan.");
        var plan = plans[0];
        Console.WriteLine("SPEC " + JsonSerializer.Serialize(plan.RemoteExecution));
        var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan],
            Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy),
            PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
        if (!reserved.Ok) throw new IOException("Reserve failed: " + reserved.Message);
        Console.WriteLine("PASS live reserve accepted remote plan " + plan.Id.ToString("N"));

        var workerStart = new ProcessStartInfo(Store.WorkerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        workerStart.ArgumentList.Add("pane");
        workerStart.ArgumentList.Add(plan.Id.ToString());
        workerStart.Environment["AGENT_LAUNCHER_TEST_ROOT"] = Store.Root;
        using var worker = Process.Start(workerStart)!;
        worker.StandardInput.Close();
        var stdout = worker.StandardOutput.ReadToEndAsync();
        var stderr = worker.StandardError.ReadToEndAsync();

        var transitions = new List<object>();
        string? lastState = null;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var sawStarted = false;
        ControlEntry? final = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
            if (!status.Ok) continue;
            var entry = status.Entries?.FirstOrDefault(e => e.Plan.Id == plan.Id);
            if (entry is null) continue;
            if (entry.State != lastState)
            {
                transitions.Add(new { at = DateTimeOffset.UtcNow, state = entry.State, detail = entry.Detail });
                Console.WriteLine($"STATE {entry.State}: {entry.Detail}");
                lastState = entry.State;
            }
            if (entry.State == "Started") sawStarted = true;
            if (Store.IsTerminal(entry)) { final = entry; break; }
        }
        try { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(5000); } } catch { }
        var stdoutText = await stdout; var stderrText = await stderr;
        File.WriteAllText(Path.Combine(Store.Root, "worker-stdout.txt"), stdoutText);
        File.WriteAllText(Path.Combine(Store.Root, "worker-stderr.txt"), stderrText);

        var receiptPath = Store.FilePath(Path.Combine("attempts", plan.Id.ToString("N"), "receipt.json"));
        var receipt = File.Exists(receiptPath) ? File.ReadAllText(receiptPath) : null;
        // 硬性要求验证：远程 agent 写的文件必须经 Mutagen 同步回本机工作区（字节级 nonce 读回）。
        var localCwd = configuration.Draft.Project;
        var nonceMatch = System.Text.RegularExpressions.Regex.Match(string.Join(" ", arguments), @"NONCE_[A-Za-z0-9_]+");
        string? syncProofFile = null; string[] localFiles = [];
        if (Directory.Exists(localCwd))
        {
            localFiles = Directory.GetFiles(localCwd).Select(Path.GetFileName).ToArray()!;
            if (nonceMatch.Success)
                foreach (var f in Directory.GetFiles(localCwd))
                    if (File.ReadAllText(f).Contains(nonceMatch.Value)) { syncProofFile = f; break; }
        }
        var syncOk = nonceMatch.Success && syncProofFile is not null;
        var ok = sawStarted && final is not null && final.State is "Exited" or "Settled" && syncOk;
        var evidence = new
        {
            host = address,
            sshUser,
            agentUser,
            remoteDir,
            arguments,
            planId = plan.Id,
            sawStarted,
            finalState = final?.State,
            finalDetail = final?.Detail,
            transitions,
            receipt,
            localCwd,
            localFiles,
            expectedNonce = nonceMatch.Value,
            syncProofFile,
            syncOk,
            workerStdout = stdoutText,
            workerStderr = stderrText,
            passed = ok
        };
        Store.Save(Path.Combine(Store.Root, "remote-live-results.json"), evidence);
        Console.WriteLine((ok ? "PASS" : "FAIL") + $" live full chain: started={sawStarted}, final={final?.State ?? "timeout"}, syncOk={syncOk}, proof={syncProofFile ?? "none"}");
        try
        {
            var pid = Store.Read<int>(Store.FilePath("monitor-pid.json"));
            if (pid > 0) { using var monitor = Process.GetProcessById(pid); monitor.Kill(); monitor.WaitForExit(5000); }
        }
        catch { }
        return ok ? 0 : 1;
    }

    // R45 活测夹具（确定性提示词，R29 先例参数形状 ["-p","-a","--no-session",prompt]）。
    // 心跳：pi 的 bash 工具前台等待 360 秒 → pi 存活 → 本地 ssh 保持 → 条目 Started；heartbeat.txt 每 5 秒增长是远端活性证明。
    private const string HeartbeatPrompt = "Use your bash tool to run exactly this command with a tool timeout of 420 seconds, and do nothing else before or after: bash -c 'end=$((SECONDS+360)); while [ $SECONDS -lt $end ]; do date +%s >> heartbeat.txt; sleep 5; done'";
    // 超限：700 MiB > 512 MiB scope → 内核在 scope 内 OOM 杀 python3；判据以 memory.events 的 oom_kill 计数为准，不依赖 dmesg 权限。
    private const string OomPrompt = "Use your bash tool to run exactly this command with a tool timeout of 120 seconds, and do nothing else: python3 -c 'b=bytearray(700*1024*1024); print(\"ALLOC_DONE\"); import time; time.sleep(20)'";

    // 纯参数护栏：原生 harness 直接测试，不建隔离根、不触网。边界复用产品编辑器同一条校验（RemoteHost.ValidateMemoryLimitMib）。
    public static void GuardLive(string scenario, string memoryLimitMibText)
    {
        if (scenario is not ("scope" or "oom" or "nonroot")) throw new IOException("scenario must be scope|oom|nonroot");
        if (!int.TryParse(memoryLimitMibText, out var mib)) throw new IOException("memoryLimitMib 必须是整数（MiB）。");
        if (RemoteHost.ValidateMemoryLimitMib(mib) is { } error) throw new IOException(error);
    }

    // 与 RunAsync 完全一致的运行护栏（全新隔离根 / 禁假样本 / 禁假 ssh / 超时边界 / 密钥存在）。
    private static (int timeoutSeconds, string? key) GuardLiveRun(string keyPath, string timeoutText)
    {
        var testRoot = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "";
        if (!testRoot.Contains("AgentLauncher-tests-") || Directory.Exists(testRoot))
            throw new IOException("Use a fresh isolated AgentLauncher-tests- directory.");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") == "1")
            throw new IOException("Live acceptance must use real resource samples; unset AGENT_LAUNCHER_FAKE_SAMPLE.");
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ssh.exe")))
            throw new IOException("Remove the fake ssh.exe from the runtime directory before live acceptance; it would shadow the real SSH client.");
        if (!int.TryParse(timeoutText, out var timeoutSeconds) || timeoutSeconds is < 10 or > 1800)
            throw new IOException("Timeout must be 10-1800 seconds.");
        var key = keyPath == "-" ? null : keyPath;
        if (key is not null && !File.Exists(key)) throw new IOException("SSH key not found: " + key);
        return (timeoutSeconds, key);
    }

    // 独立核验连接：真实 ssh + ArgumentList，UTF-8 无 BOM 读取，超时 Kill 整树并以 exit=-1 标记超时（不冒充远端退出码）。
    public static async Task<(int exit, string stdout, string stderr)> SshAsync(string address, int port, string user, string? key, string remoteCommand, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        if (key is not null) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(key); }
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add(user + "@" + address);
        psi.ArgumentList.Add(remoteCommand);
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds)); }
        catch (TimeoutException)
        {
            timedOut = true;
            try { process.Kill(true); } catch { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        return (timedOut ? -1 : process.ExitCode, await stdout, await stderr);
    }

    // 复用 Core 探针：ProbeShell 命中后 $envf 即 /proc/<pid>/environ，追加一段输出 PID，不复制扫描逻辑、不改 Core。
    public static async Task<string?> FindAgentPidAsync(string address, int port, string user, string? key, Guid attemptId, int timeoutSeconds)
    {
        var shell = RemoteReattach.ProbeShell(attemptId) + "; if [ \"$found\" = 1 ]; then echo PID=$(echo \"$envf\" | cut -d/ -f3); fi";
        var (exit, stdout, _) = await SshAsync(address, port, user, key, shell, timeoutSeconds);
        if (exit != 0) return null;
        foreach (var line in stdout.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("PID=", StringComparison.Ordinal) && text.Length > 4 && text[4..].All(char.IsAsciiDigit)) return text[4..];
        }
        return null;
    }

    private sealed record LiveRun(Configuration Configuration, LaunchItem Plan, Process Worker, Task<string> Stdout, Task<string> Stderr);
    // 与 RunAsync 相同的配置 → monitor → planner → reserve → pane worker 链路（RunAsync 本体保持不动）。
    private static async Task<LiveRun> StartLiveAsync(RemoteHost host, string remoteDir, string[] arguments)
    {
        Directory.CreateDirectory(Store.Root);
        var configuration = new Configuration { SchemaVersion = 2 };
        configuration.Draft.SchemaVersion = 2;
        configuration.Draft.Revision = 3;
        configuration.Draft.Project = Path.Combine(Store.Root, "local-cwd");
        Directory.CreateDirectory(configuration.Draft.Project);
        configuration.Draft.DirectoryMode = "shared";
        configuration.RemoteHosts = [host];
        var slot = configuration.Draft.Slots[0];
        slot.Enabled = true;
        slot.AgentId = "pi";
        slot.CustomName = "Live Remote Pi";
        slot.RemoteHostId = host.Id;
        slot.RemoteDirectory = remoteDir;
        slot.Profiles["pi"] = new AgentProfile { Executable = "pi", Arguments = arguments };
        foreach (var other in configuration.Draft.Slots.Skip(1)) other.Enabled = false;
        Store.Save(Store.FilePath("configuration.json"), configuration);
        var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
        var initial = await Ipc.EnsureMonitorAsync();
        if (!initial.Ok) throw new IOException("Monitor start failed: " + initial.Message);
        var preparer = new WorkspacePreparer(Path.Combine(Store.Root, "worktrees"));
        var plans = await preparer.PrepareAsync(configuration.Draft, _ => { }, _ => Task.FromResult(false), CancellationToken.None, configuration: configuration);
        if (plans.Count != 1 || plans[0].RemoteExecution is null) throw new IOException("Planner did not emit one remote plan.");
        var plan = plans[0];
        Console.WriteLine("SPEC " + JsonSerializer.Serialize(plan.RemoteExecution));
        var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan],
            Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy),
            PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
        if (!reserved.Ok) throw new IOException("Reserve failed: " + reserved.Message);
        Console.WriteLine("PASS live reserve accepted remote plan " + plan.Id.ToString("N"));
        var workerStart = new ProcessStartInfo(Store.WorkerPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false), StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        workerStart.ArgumentList.Add("pane");
        workerStart.ArgumentList.Add(plan.Id.ToString());
        workerStart.Environment["AGENT_LAUNCHER_TEST_ROOT"] = Store.Root;
        var worker = Process.Start(workerStart)!;
        worker.StandardInput.Close();
        return new(configuration, plan, worker, worker.StandardOutput.ReadToEndAsync(), worker.StandardError.ReadToEndAsync());
    }

    private sealed class TransitionLog
    {
        public readonly List<object> Items = [];
        public bool SawStarted;
        private string? last;
        public void Observe(ControlEntry? entry)
        {
            if (entry is null) return;
            if (entry.State == "Started") SawStarted = true;
            if (entry.State == last) return;
            Items.Add(new { at = DateTimeOffset.UtcNow, state = entry.State, detail = entry.Detail });
            Console.WriteLine($"STATE {entry.State}: {entry.Detail}");
            last = entry.State;
        }
    }
    private static async Task<ControlEntry?> EntryAsync(Guid id)
    {
        var status = await Ipc.SendAsync(new("status", Guid.NewGuid()));
        return status.Ok ? status.Entries?.FirstOrDefault(e => e.Plan.Id == id) : null;
    }
    private static async Task<ControlEntry?> WaitEntryAsync(Guid id, DateTime deadline, TransitionLog log, Func<ControlEntry, bool> done)
    {
        ControlEntry? entry = null;
        while (DateTime.UtcNow < deadline)
        {
            entry = await EntryAsync(id);
            log.Observe(entry);
            if (entry is not null && done(entry)) return entry;
            await Task.Delay(500);
        }
        return entry;
    }

    private static string RemoteFile(string remoteDir, string name) => RemoteWorkspaceMappingHelper.ShellDirectory(remoteDir.TrimEnd('/') + "/" + name);
    private static async Task<int> HeartbeatLinesAsync(string address, string user, string? key, string remoteDir)
    {
        var (_, stdout, _) = await SshAsync(address, 22, user, key, "wc -l < " + RemoteFile(remoteDir, "heartbeat.txt") + " 2>/dev/null || echo 0", 20);
        return int.TryParse(stdout.Trim(), out var lines) ? lines : 0;
    }
    // 先等心跳出现（pi 决定执行命令有模型延迟），再按判据做两次间隔 6 秒的采样。
    private static async Task<(int first, int second, bool grew)> HeartbeatGrowthAsync(string address, string user, string? key, string remoteDir, DateTime deadline)
    {
        var first = 0;
        while (DateTime.UtcNow < deadline)
        {
            first = await HeartbeatLinesAsync(address, user, key, remoteDir);
            if (first > 0) break;
            await Task.Delay(1000);
        }
        await Task.Delay(6000);
        var second = await HeartbeatLinesAsync(address, user, key, remoteDir);
        return (first, second, first > 0 && second > first);
    }

    private static async Task<(string output, string? cgroup, string? memoryMax, string events)> ReadCgroupAsync(string address, string user, string? key, string pid)
    {
        var command = "cg=$(sed -n 's/^0:://p' /proc/" + pid + "/cgroup 2>/dev/null); echo CGROUP=$cg; if [ -n \"$cg\" ]; then echo MEMORY_MAX=$(cat \"/sys/fs/cgroup$cg/memory.max\" 2>/dev/null); echo EVENTS_BEGIN; cat \"/sys/fs/cgroup$cg/memory.events\" 2>/dev/null; echo EVENTS_END; fi";
        var (_, stdout, stderr) = await SshAsync(address, 22, user, key, command, 20);
        string? cgroup = null, memoryMax = null; var events = ""; var inEvents = false;
        foreach (var line in stdout.Split('\n'))
        {
            var text = line.Trim();
            if (text == "EVENTS_BEGIN") { inEvents = true; continue; }
            if (text == "EVENTS_END") { inEvents = false; continue; }
            if (inEvents) { events += text + "\n"; continue; }
            if (text.StartsWith("CGROUP=", StringComparison.Ordinal)) cgroup = text[7..];
            else if (text.StartsWith("MEMORY_MAX=", StringComparison.Ordinal)) memoryMax = text[11..];
        }
        return (stdout + (stderr.Length > 0 ? "\n" + stderr : ""), cgroup is { Length: > 0 } ? cgroup : null, memoryMax is { Length: > 0 } ? memoryMax : null, events);
    }
    private static async Task<string> ReadEventsAsync(string address, string user, string? key, string cgroup)
    {
        var (_, stdout, _) = await SshAsync(address, 22, user, key, "cat \"/sys/fs/cgroup" + cgroup + "/memory.events\" 2>/dev/null", 20);
        return stdout;
    }
    private static long OomKillCount(string events)
    {
        foreach (var line in events.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "oom_kill" && long.TryParse(parts[1], out var count)) return count;
        }
        return 0;
    }

    // 收尾只杀本次 attempt token 标记的进程（environ 精确匹配）；trap '' HUP 只挡挂断，TERM 未清再补 KILL。
    private static async Task<(string output, string finalProbe)> KillAttemptTreeAsync(string address, string user, string? key, Guid attemptId)
    {
        var token = RemoteReattach.AttemptToken(attemptId);
        var output = "";
        async Task<string> ProbeAsync()
        {
            var (exit, stdout, stderr) = await SshAsync(address, 22, user, key, RemoteReattach.ProbeShell(attemptId), 20);
            output += stdout + (stderr.Length > 0 ? "\n" + stderr : "") + "\n";
            return RemoteReattach.ClassifyProbe(exit, exit == -1, stdout);
        }
        foreach (var signal in new[] { "", " -9" })
        {
            var kill = "for p in $(grep -rl '" + token + "' /proc/[0-9]*/environ 2>/dev/null | cut -d/ -f3); do kill" + signal + " $p 2>/dev/null; done; echo KILL_SENT";
            var (_, stdout, stderr) = await SshAsync(address, 22, user, key, kill, 30);
            output += stdout + (stderr.Length > 0 ? "\n" + stderr : "") + "\n";
            await Task.Delay(1500);
            var probe = await ProbeAsync();
            if (probe != "alive") return (output, probe);
        }
        return (output, await ProbeAsync());
    }

    private static async Task<(string stdout, string stderr, int? exitCode)> StopWorkerAsync(LiveRun run, int waitSeconds)
    {
        try { await run.Worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(waitSeconds)); }
        catch (TimeoutException) { try { run.Worker.Kill(); run.Worker.WaitForExit(5000); } catch { } }
        var stdout = await run.Stdout; var stderr = await run.Stderr;
        File.WriteAllText(Path.Combine(Store.Root, "worker-stdout.txt"), stdout);
        File.WriteAllText(Path.Combine(Store.Root, "worker-stderr.txt"), stderr);
        int? exit = null; try { exit = run.Worker.ExitCode; } catch { }
        return (stdout, stderr, exit);
    }
    private static void KillMonitor()
    {
        try
        {
            var pid = Store.Read<int>(Store.FilePath("monitor-pid.json"));
            if (pid > 0) { using var monitor = Process.GetProcessById(pid); monitor.Kill(); monitor.WaitForExit(5000); }
        }
        catch { }
    }

    // R45 活测：--remote-live-memory <address> <sshUser> <agentUser> <remoteDir> <keyPath|-> <scope|oom|nonroot> <memoryLimitMib> <timeoutSeconds>
    // nonroot 场景调用方必须传 sshUser==agentUser（非 root 登录触发 systemd 拒绝）。
    public static async Task<int> RunMemoryAsync(string address, string sshUser, string agentUser, string remoteDir, string keyPath, string scenario, string memoryLimitMibText, string timeoutText)
    {
        GuardLive(scenario, memoryLimitMibText);
        var (timeoutSeconds, key) = GuardLiveRun(keyPath, timeoutText);
        var mib = int.Parse(memoryLimitMibText);
        if (scenario == "nonroot" && sshUser != agentUser) throw new IOException("nonroot 场景要求 sshUser 与 agentUser 相同（以非 root 登录触发 systemd 拒绝）。");
        var host = new RemoteHost(Guid.NewGuid(), "Live VPS", address, 22, sshUser, agentUser, ["pi"], key, WorkspaceSyncMode.Mutagen, null, mib);
        string[] arguments = ["-p", "-a", "--no-session", scenario == "oom" ? OomPrompt : HeartbeatPrompt];
        var run = await StartLiveAsync(host, remoteDir, arguments);
        var log = new TransitionLog();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        // 远程创建阶段（CreatedSuspended/ResumeIntent）会短暂映射为 Disconnected；只有 Started 之后的 Disconnected 才提前结束等待。
        await WaitEntryAsync(run.Plan.Id, deadline, log, e => e.State == "Started" || Store.IsTerminal(e) || (log.SawStarted && e.State == "Disconnected"));
        var sawStarted = log.SawStarted;
        string? agentPid = null, cgroup = null, memoryMax = null;
        var eventsBaseline = ""; var eventsFinal = ""; var cgroupOutput = ""; var pgrepOutput = ""; var heartbeatProbe = "";
        long oomKill = 0; var memoryMaxOk = false;
        var heartbeatFirst = 0; var heartbeatSecond = 0; var heartbeatGrew = false;
        if (scenario is "scope" or "oom")
        {
            if (sawStarted)
            {
                while (DateTime.UtcNow < deadline && agentPid is null)
                {
                    agentPid = await FindAgentPidAsync(address, 22, sshUser, key, run.Plan.Id, 20);
                    if (agentPid is null) await Task.Delay(500);
                }
                if (agentPid is not null)
                {
                    var read = await ReadCgroupAsync(address, sshUser, key, agentPid);
                    cgroupOutput = read.output; cgroup = read.cgroup; memoryMax = read.memoryMax; eventsBaseline = read.events;
                    memoryMaxOk = memoryMax == ((long)mib * 1024 * 1024).ToString();
                }
            }
            if (scenario == "scope")
            {
                (heartbeatFirst, heartbeatSecond, heartbeatGrew) = await HeartbeatGrowthAsync(address, sshUser, key, remoteDir, deadline);
            }
            else
            {
                // oom_kill 必须在 scope 仍存在时读到（pi 退出后 transient scope 会被回收），500ms 密集轮询并保留最后一次读数。
                while (DateTime.UtcNow < deadline)
                {
                    if (cgroup is not null) eventsFinal = await ReadEventsAsync(address, sshUser, key, cgroup);
                    oomKill = OomKillCount(eventsFinal);
                    if (oomKill >= 1) break;
                    await Task.Delay(500);
                }
                var (_, pgrep, _) = await SshAsync(address, 22, sshUser, key, "pgrep -f 'bytearr[a]y' || true", 20);
                pgrepOutput = pgrep;
            }
        }
        else
        {
            await WaitEntryAsync(run.Plan.Id, deadline, log, Store.IsTerminal);
            var (_, probeOut, probeErr) = await SshAsync(address, 22, sshUser, key, RemoteReattach.ProbeShell(run.Plan.Id), 20);
            cgroupOutput = probeOut + (probeErr.Length > 0 ? "\n" + probeErr : "");
            // pgrep 用括号技巧避免匹配到核验命令自身的 shell；「无本次 agent」的精确判据是 token 探针 GONE（attempt 级绑定）。
            var (_, pgrep, _) = await SshAsync(address, 22, sshUser, key, "pgrep -f 'pi [-]p' || true", 20);
            pgrepOutput = pgrep;
            var (_, exists, _) = await SshAsync(address, 22, sshUser, key, "test -e " + RemoteFile(remoteDir, "heartbeat.txt") + " && echo HEARTBEAT_EXISTS || echo HEARTBEAT_ABSENT", 20);
            heartbeatProbe = exists.Trim();
        }
        // 收尾（所有 scenario）：杀本次 token 进程树 → 等 pane worker 退出 → Disconnected/Reattaching 且探针 GONE 才操作员确认释放。
        var (killOutput, finalProbe) = await KillAttemptTreeAsync(address, sshUser, key, run.Plan.Id);
        var (stdoutText, stderrText, workerExit) = await StopWorkerAsync(run, 30);
        var receiptPath = Store.ReceiptPath(run.Plan.Id);
        var receipt = File.Exists(receiptPath) ? File.ReadAllText(receiptPath) : null;
        var finalEntry = await EntryAsync(run.Plan.Id);
        log.Observe(finalEntry);
        Response? releaseResponse = null;
        if (finalEntry is not null && finalEntry.State is "Disconnected" or "Reattaching" && finalProbe == "gone")
        {
            releaseResponse = await Ipc.SendAsync(new("release-remote", Guid.NewGuid(), Attempt: run.Plan.Id, Release: new OperatorRelease("Gone", "live fixture cleanup", true)));
            finalEntry = await EntryAsync(run.Plan.Id);
            log.Observe(finalEntry);
        }
        bool ok;
        if (scenario == "scope") ok = sawStarted && memoryMaxOk && heartbeatGrew;
        else if (scenario == "oom") ok = sawStarted && memoryMaxOk && oomKill >= 1;
        else
        {
            // 产品语义：Started 回执在本地 ssh 启动即写（远端命令尚未执行），nonroot 拒绝前可能短暂出现 Started。
            // 「agent 从未运行」以 attempt 级证据判定：token 探针 GONE + pgrep 无 pi + heartbeat.txt 从未创建 + 终态 + systemd 拒绝文案。
            var text = (finalEntry?.Detail ?? "") + "\n" + (receipt ?? "") + "\n" + stderrText;
            var rejection = text.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || text.Contains("Failed to start transient scope unit", StringComparison.OrdinalIgnoreCase);
            ok = finalEntry is not null && Store.IsTerminal(finalEntry) && rejection && finalProbe == "gone" && pgrepOutput.Trim().Length == 0 && heartbeatProbe == "HEARTBEAT_ABSENT";
        }
        var evidence = new
        {
            host = address,
            sshUser,
            agentUser,
            remoteDir,
            scenario,
            memoryLimitMib = mib,
            expectedMemoryMax = (long)mib * 1024 * 1024,
            arguments,
            planId = run.Plan.Id,
            sawStarted,
            finalState = finalEntry?.State,
            finalDetail = finalEntry?.Detail,
            transitions = log.Items,
            receipt,
            agentPid,
            cgroup,
            memoryMax,
            memoryMaxOk,
            memoryEventsBaseline = eventsBaseline,
            memoryEventsFinal = eventsFinal,
            oomKill,
            heartbeatFirst,
            heartbeatSecond,
            heartbeatGrew,
            heartbeatProbe,
            cgroupOutput,
            finalProbe,
            pgrepOutput,
            killOutput,
            workerExitCode = workerExit,
            workerStdout = stdoutText,
            workerStderr = stderrText,
            releaseOk = releaseResponse?.Ok,
            releaseMessage = releaseResponse?.Message,
            passed = ok
        };
        Store.Save(Path.Combine(Store.Root, "remote-live-memory-results.json"), evidence);
        Console.WriteLine((ok ? "PASS" : "FAIL") + $" live memory {scenario}: started={sawStarted}, memoryMax={memoryMax ?? "none"}, oomKill={oomKill}, heartbeatGrew={heartbeatGrew}, final={finalEntry?.State ?? "timeout"}, probe={finalProbe}");
        KillMonitor();
        run.Worker.Dispose();
        return ok ? 0 : 1;
    }
}