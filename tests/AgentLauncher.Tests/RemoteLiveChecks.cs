using System.Diagnostics;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

// 真实 VPS 全链路验收（opt-in，不进 CI）：真实 planner → monitor reserve/begin → Worker pane → 真实 ssh → su agent → pi。
// 用法：--remote-live <address> <sshUser> <agentUser> <remoteDir> <keyPath|-> <argsJson> <timeoutSeconds>
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
}