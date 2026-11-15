using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class LiveMemorySoak
{
    private sealed record Invocation(string Executable, string[] Arguments, string Root, Dictionary<string, string> Environment);

    public static async Task<int> Child(string path)
    {
        var spec = JsonSerializer.Deserialize<Invocation>(File.ReadAllText(path))!;
        var start = new ProcessStartInfo(spec.Executable) { WorkingDirectory = spec.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        var streaming = spec.Environment.TryGetValue("LAUNCHER_QODER_STREAM_COMMAND", out var workloadCommand);
        start.RedirectStandardInput = streaming;
        foreach (var arg in spec.Arguments) start.ArgumentList.Add(arg);
        foreach (var pair in spec.Environment) start.Environment[pair.Key] = pair.Value;
        using var child = Process.Start(start)!;
        async Task Capture(StreamReader input, string name)
        {
            using var output = File.CreateText(Path.Combine(spec.Root, name));
            var buffer = new char[4096];var saved = 0;
            int count;while ((count = await input.ReadAsync(buffer)) > 0)
            {
                if (saved < 8 * 1024 * 1024) { await output.WriteAsync(buffer.AsMemory(0, count));await output.FlushAsync();saved += count; }
            }
        }
        async Task DriveQoder()
        {
            using var output = File.CreateText(Path.Combine(spec.Root, "stdout.jsonl"));
            var step = 1;var toolCalls = 0;var knownRefusal = false;var saved = 0;
            async Task Send()
            {
                var prompt = $"Run exactly one Bash call. Copy this entire command verbatim, including the final integer argument:\n```\n{workloadCommand} {step}\n```\nSet timeout to 120000; it takes 60 seconds. Wait for completion. Do not run in background or use other tools. If any hook denies the call, report BLOCKED and stop this turn without retry. Otherwise report the tool result and stop this turn. Do not start the next step yourself.";
                await child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = prompt } }));await child.StandardInput.FlushAsync();
            }
            await Send();
            string? line;
            while ((line = await child.StandardOutput.ReadLineAsync()) is not null)
            {
                if (saved < 8 * 1024 * 1024) { await output.WriteLineAsync(line);await output.FlushAsync();saved += line.Length; }
                using var json = JsonDocument.Parse(line);var frame = json.RootElement;
                if (frame.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                    {
                        if (!block.TryGetProperty("type", out var blockType)) continue;
                        if (blockType.GetString() == "tool_use") toolCalls++;
                        if (blockType.GetString() == "tool_result" && block.TryGetProperty("is_error", out var error) && error.GetBoolean() && block.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            var reason = text.GetString()!;
                            knownRefusal = reason.Contains("AgentLauncher.Worker.exe", StringComparison.OrdinalIgnoreCase) && reason.Contains("hook blocking error", StringComparison.Ordinal)
                                && (reason.Contains("整机内存不足以", StringComparison.Ordinal) || reason.Contains("该任务内存余量不足", StringComparison.Ordinal));
                        }
                    }
                if (!frame.TryGetProperty("type", out var type) || type.GetString() != "result") continue;
                if (frame.TryGetProperty("is_error", out var failed) && failed.GetBoolean()) throw new IOException("Qoder API failed during the long session.");
                if (toolCalls != 1) throw new IOException("Expected exactly one tool call in this controlled turn.");
                var journal = Path.Combine(spec.Root, "workload.jsonl");var completed = File.Exists(journal) ? File.ReadLines(journal).Count() : 0;
                if (completed == step && !knownRefusal) step++;
                else if (completed == step - 1 && knownRefusal) await Task.Delay(15000);
                else throw new IOException("Tool outcome was not a known pre-execution refusal; never replay it.");
                if (step > 30) { child.StandardInput.Close();continue; }
                toolCalls = 0;knownRefusal = false;await Send();
            }
            if (step != 31) throw new IOException("Qoder ended before all 30 steps completed.");
        }
        var stdout = streaming ? DriveQoder() : Capture(child.StandardOutput, "stdout.jsonl");var stderr = Capture(child.StandardError, "stderr.txt");
        // Observe a protocol failure immediately rather than waiting on an idle open stdin.
        if (streaming && await Task.WhenAny(stdout, child.WaitForExitAsync()) == stdout && stdout.IsFaulted)
        {
            if (!child.HasExited) { child.Kill(true);await child.WaitForExitAsync(); }
            await stdout;
        }
        await child.WaitForExitAsync();await Task.WhenAll(stdout, stderr);return child.ExitCode;
    }

    public static async Task<int> Run(string agent, string cli, string worker, string node, string repository, string root)
    {
        if (agent is not ("qoder" or "omp") || !Path.IsPathFullyQualified(root) || !root.Contains("AgentLauncher-tests-", StringComparison.Ordinal) || Directory.Exists(root))
            throw new IOException("Use a fresh isolated AgentLauncher-tests- directory and a known agent.");
        Directory.CreateDirectory(root);
        var sample = new Sampler().Sample();
        var physicalFloor = ResourcePolicy.DefaultPhysicalFloor(sample.Total);var commitFloor = ResourcePolicy.DefaultCommitFloor(sample.CommitLimit);
        if (sample.Available < physicalFloor + ResourcePolicy.GiB || sample.CommitAvailable < commitFloor + 4 * ResourcePolicy.GiB)
            throw new IOException("Insufficient real system headroom to start the 4 GiB soak; no workload launched.");
        using var outer = new Job("Local\\AL-live-root-" + Guid.NewGuid().ToString("N"), root: true);
        using var job = new Job("Local\\AL-live-item-" + Guid.NewGuid().ToString("N"), 4 * ResourcePolicy.GiB);
        using var guard = new OmpMemoryGuard(job, null, physicalFloor, commitFloor);
        var workload = Path.Combine(repository, "tests", "fixtures", "memory-workload.mjs");
        var env = new Dictionary<string, string> { [OmpMemoryGuard.PipeEnvironment] = guard.PipeName };
        string[] arguments;
        if (agent == "qoder")
        {
            QoderMemoryGuard.Prepare(worker, root);
            var command = $"\"{node.Replace('\\', '/')}\" \"{workload.Replace('\\', '/')}\" \"{root.Replace('\\', '/')}\"";
            env["LAUNCHER_QODER_STREAM_COMMAND"] = command;
            arguments = ["--plugin-dir", QoderMemoryGuard.DirectoryFor(worker, root), "--setting-sources", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--tools", "Bash", "--allowed-tools", "Bash", "--input-format", "stream-json", "--output-format", "stream-json", "--max-model-request-retries", "1", "-p"];
        }
        else
        {
            var config = Path.Combine(root, "config");Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "config.yml"), "disabledProviders: [ollama, lm-studio, llama.cpp]\ntask:\n  maxConcurrency: 4\nasync:\n  enabled: false\n");
            env["PI_CODING_AGENT_DIR"] = config;env["LAUNCHER_SOAK_ROOT"] = root;env["LAUNCHER_SOAK_NODE"] = node;env["LAUNCHER_SOAK_WORKLOAD"] = workload;
            arguments = ["--no-extensions", "--no-skills", "--no-rules", "--no-context-files", "--no-lsp", "--no-session", "--no-title", "--extension", Path.Combine(repository, "src", "AgentLauncher.Windows", "omp-memory-guard.mjs"), "--extension", Path.Combine(repository, "tests", "fixtures", "omp-memory-soak.mjs"), "--model", "launcher-soak/probe", "--mode", "json", "-p", "Run the sequential memory workload."];
        }
        var specPath = Path.Combine(root, "invocation.json");File.WriteAllText(specPath, JsonSerializer.Serialize(new Invocation(cli, arguments, root, env)));
        var release = Path.Combine(root, "pressure");
        using var holder = new ProtectedProcess(new(Environment.ProcessPath!, ["--live-memory-hold", release], "exe"), root, [outer, job], createNoWindow: true);
        ProtectedProcess? driver = null;var watch = Stopwatch.StartNew();var succeeded = false;string? failure = null;var driverExit = -1;var steps = 0;
        try
        {
            holder.Resume();
            while (!File.Exists(release + ".ready"))
            {
                if (holder.Process.HasExited || watch.Elapsed.TotalSeconds > 10) throw new IOException("Commit pressure allocation failed.");
                await Task.Delay(100);
            }
            driver = new ProtectedProcess(new(Environment.ProcessPath!, ["--live-memory-child", specPath], "exe"), root, [outer, job], createNoWindow: true);
            driver.Resume();Console.WriteLine("RUN " + agent + " " + root);
            using var samples = File.CreateText(Path.Combine(root, "memory.jsonl"));
            var lastStep = -1;
            while (!driver.Process.HasExited)
            {
                sample = new Sampler().Sample();
                var journal = Path.Combine(root, "workload.jsonl");steps = File.Exists(journal) ? File.ReadLines(journal).Count() : 0;
                await samples.WriteLineAsync(JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Seconds = watch.Elapsed.TotalSeconds, Commit = job.QueryCurrentCommit(), sample.Available, sample.CommitAvailable, Steps = steps }));await samples.FlushAsync();
                if (steps != lastStep) { Console.WriteLine($"{agent}: {steps}/30 steps, {watch.Elapsed.TotalSeconds:F0}s, commit {job.QueryCurrentCommit() / ResourcePolicy.MiB} MiB");lastStep = steps; }
                if (watch.Elapsed.TotalSeconds > 2400) throw new IOException("40 minute deadline exceeded.");
                // The production guard prevents NEW tools at its unchanged floor plus
                // growth margin. Existing bounded work may finish and a refused tool may
                // wait for recovery; do not turn that expected pause into a test-side kill.
                if (sample.Available < 256 * ResourcePolicy.MiB || sample.CommitAvailable < 512 * ResourcePolicy.MiB) throw new IOException("Emergency system headroom exhausted; stopping only this test.");
                await Task.Delay(2000);
            }
            driverExit = driver.Process.ExitCode;
            // The final write can land between the last sample and process exit.
            var finalJournal = Path.Combine(root, "workload.jsonl");
            steps = File.Exists(finalJournal) ? File.ReadLines(finalJournal).Count() : 0;
            if (driverExit != 0 || steps != 30 || watch.Elapsed.TotalSeconds < 1800 || !guard.HandshakeConfirmed || job.QueryMemory()!.PeakBytes <= 2 * ResourcePolicy.GiB)
                throw new IOException("Long-task requirements not met; inspect stdout and memory evidence.");
            succeeded = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { failure = e.Message; }
        finally
        {
            File.WriteAllText(release + ".release", "release");
            // These are exclusively created test processes; existing user sessions are never touched.
            if (driver is not null && !driver.Process.HasExited) { driver.Process.Kill(true);driver.Process.WaitForExit(10000); }
            if (!holder.Process.WaitForExit(10000)) { holder.Process.Kill();holder.Process.WaitForExit(5000); }
            driver?.Dispose();
        }
        var drained = job.Members().Length == 0;
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = succeeded && drained, Agent = agent, Seconds = watch.Elapsed.TotalSeconds, Steps = steps, DriverExit = driverExit, guard.HandshakeConfirmed, Memory = job.QueryMemory(), Drained = drained, Failure = failure, PhysicalFloor = physicalFloor, CommitFloor = commitFloor, SyntheticCommitMiB = 2180, AuthenticatedModel = agent == "qoder" }));
        Console.WriteLine((succeeded && drained ? "PASS " : "FAIL ") + agent + ": " + failure + " " + root);
        return succeeded && drained ? 0 : 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
    public static int Hold(string prefix)
    {
        var address = VirtualAlloc(IntPtr.Zero, (UIntPtr)(2180UL * ResourcePolicy.MiB), 0x3000, 4);
        if (address == IntPtr.Zero) return 37;
        try
        {
            File.WriteAllText(prefix + ".ready", "ready");var watch = Stopwatch.StartNew();
            while (!File.Exists(prefix + ".release")) { if (watch.Elapsed.TotalSeconds > 2500) return 38;Thread.Sleep(100); }
            return 0;
        }
        finally { VirtualFree(address, UIntPtr.Zero, 0x8000); }
    }
}
