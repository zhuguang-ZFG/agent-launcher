using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentLauncher.Windows;

internal static class QoderLiveChecks
{
    public static async Task<int> ResumeAsync(string cli, string worker, string root)
    {
        if (!Path.IsPathFullyQualified(root) || !root.Contains("AgentLauncher-tests-", StringComparison.Ordinal) || Directory.Exists(root))
            throw new IOException("Use a fresh isolated AgentLauncher-tests- directory.");
        Directory.CreateDirectory(root);
        var version = await Run(cli, ["--version"], root, null);
        if (version.Code != 0 || version.Output.Trim() != AgentLauncher.Core.AgentPresets.QoderVerifiedVersion) throw new IOException("Unverified Qoder version.");
        QoderMemoryGuard.Prepare(worker, root);
        using var job = new Job("Local\\AL-resume-" + Guid.NewGuid().ToString("N"), 4 * AgentLauncher.Core.ResourcePolicy.GiB);
        using var guard = new OmpMemoryGuard(job, null, 1, 1);
        var id = Guid.NewGuid().ToString();var nonce = "resume-check-" + Guid.NewGuid().ToString("N");
        string[] common = ["--plugin-dir", QoderMemoryGuard.DirectoryFor(worker, root), "--setting-sources", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--tools", "", "--output-format", "stream-json", "--max-model-request-retries", "0"];
        var results = new List<object>();
        foreach (var mode in new[] { "new", "resume", "continue" })
        {
            string[] selector = mode == "new" ? ["--session-id", id] : mode == "resume" ? ["--resume", id] : ["--continue"];
            var prompt = mode == "new" ? $"Remember this exact verification token for our next turn: {nonce}. Reply only with this token. Do not use tools."
                : "Reply only with the exact verification token from the first user message in this conversation. Do not use tools or invent a token.";
            var run = await Run(cli, [..common, ..selector, "-p", prompt], root, guard.PipeName);
            File.WriteAllText(Path.Combine(root, mode + ".jsonl"), run.Output);File.WriteAllText(Path.Combine(root, mode + ".stderr.txt"), run.Error);
            var final = run.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).LastOrDefault(item => item.TryGetProperty("type", out var type) && type.GetString() == "result");
            var passed = run.Code == 0 && final.ValueKind == JsonValueKind.Object && final.TryGetProperty("result", out var text)
                && text.GetString()?.Contains(nonce, StringComparison.Ordinal) == true && final.GetProperty("session_id").GetString() == id
                && (!final.TryGetProperty("is_error", out var error) || !error.GetBoolean()) && guard.HandshakeConfirmed;
            results.Add(new { Mode = mode, run.Code, run.Seconds, Passed = passed });
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = mode == "continue" && passed, SessionId = id, Results = results }));
            if (!passed) throw new IOException("Persisted Qoder " + mode + " failed; evidence: " + root);
        }
        Console.WriteLine("PASS Qoder persisted resume and continue: " + root);return 0;
    }

    // Explicit external acceptance only: Qoder reads its own login. Never copy credentials.
    public static async Task<int> RunAsync(string cli, string worker, string root, bool sessionOnly = false, bool childOnly = false)
    {
        if (!Path.IsPathFullyQualified(root) || !root.Contains("AgentLauncher-tests-", StringComparison.Ordinal) || Directory.Exists(root))
            throw new IOException("Use a fresh isolated AgentLauncher-tests- directory.");
        Directory.CreateDirectory(root);
        var status = await Run(cli, ["status", "-o", "json"], root, null);
        using var account = JsonDocument.Parse(status.Output);
        if (!account.RootElement.GetProperty("logged_in").GetBoolean()) throw new IOException("Sign into Qoder normally first.");
        var version = account.RootElement.GetProperty("version").GetString();
        QoderMemoryGuard.Prepare(worker, root);
        var plugin = QoderMemoryGuard.DirectoryFor(worker, root);
        var pipeName = "AgentLauncher-memory-" + Guid.NewGuid().ToString("N");
        var allow = false;var checks = 0;var hellos = 0;var allowThrough = int.MaxValue;
        using var stop = new CancellationTokenSource();
        var first = Pipe();
        NamedPipeServerStream Pipe() => new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var server = Task.Run(async () =>
        {
            var pipe = first;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(stop.Token);
                        var request = new byte[1];await pipe.ReadExactlyAsync(request, stop.Token);
                        if (request[0] == 2) Interlocked.Increment(ref hellos);else if (request[0] == 1) Interlocked.Increment(ref checks);else throw new IOException("Invalid guard byte");
                        await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { allowed = request[0] == 2 || (allow && checks <= allowThrough), reason = "Acceptance fixture pressure: do not retry; report BLOCKED and stop." }) + "\n"), stop.Token);
                    }
                    catch (IOException) { }
                    finally { pipe.Dispose(); }
                    if (!stop.IsCancellationRequested) pipe = Pipe();
                }
            }
            catch (OperationCanceledException) { }
            finally { pipe.Dispose(); }
        });
        var results = new List<object>();
        async Task Case(string name, bool healthy, bool expected, string? endpoint = null, string[]? extra = null)
        {
            allow = healthy;
            var cwd = Path.Combine(root, name);Directory.CreateDirectory(cwd);
            if (name == "user-deny")
            {
                Directory.CreateDirectory(Path.Combine(cwd, ".qoder"));
                File.Copy(Path.Combine(root, "user-deny.json"), Path.Combine(cwd, ".qoder", "settings.json"));
            }
            var marker = Path.Combine(cwd, "executed.txt");
            var before = checks;var helloBefore = hellos;
            var prompt = $"Acceptance check in an isolated workspace. Use Write exactly once to create {marker.Replace('\\', '/')} with exact content executed. Do not use other tools. If a hook or permission denies the write, do not retry or find alternatives: report BLOCKED and stop. After success report DONE and stop.";
            var childCase = name.StartsWith("child-", StringComparison.Ordinal);
            if (childCase) prompt = $"Use Agent exactly once to delegate to the memory-probe subagent. The subagent must use Write exactly once to create {marker.Replace('\\', '/')} containing executed. Run the subagent in foreground and wait for it. If any hook denies any tool, report BLOCKED and stop without retry. Do not write the file yourself. After the subagent returns, report its result and stop.";
            var args = new[] { "--plugin-dir", plugin, "--setting-sources", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--tools", "Write", "--allowed-tools", "Write", "--output-format", "stream-json", "--max-model-request-retries", "0" }.Concat(extra ?? []).Concat(["-p", prompt]).ToArray();
            var run = await Run(cli, args, cwd, endpoint ?? pipeName);
            File.WriteAllText(Path.Combine(cwd, "stdout.jsonl"), run.Output);File.WriteAllText(Path.Combine(cwd, "stderr.txt"), run.Error);
            var executed = File.Exists(marker);
            var count = checks - before;
            var result = new { Name = name, run.Code, run.Seconds, Checks = count, Hellos = hellos - helloBefore, Executed = executed };
            results.Add(result);File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Version = version, Passed = false, Results = results }));
            Console.WriteLine(JsonSerializer.Serialize(result));
            if (run.Code != 0 || executed != expected || (endpoint is null && count < 1)) throw new IOException(name + " failed; inspect isolated evidence.");
            if (expected && (count != (childCase ? 2 : 1) || File.ReadAllText(marker).Trim() != "executed")) throw new IOException("Recovery did not execute exactly once.");
            if (childCase && count != 2) throw new IOException("Expected the parent task and exactly one child Write to reach the guard.");
        }
        Console.WriteLine("Evidence: " + root);
        try
        {
            if (childOnly)
            {
                var agents = JsonSerializer.Serialize(new Dictionary<string, object> { ["memory-probe"] = new { description = "Isolated acceptance writer", prompt = "Use Write exactly once at the path requested by the parent. On tool denial report BLOCKED without retries. Never use other tools.", tools = new[] { "Write" } } });
                string[] childArguments = ["--tools", "Agent", "Write", "--allowed-tools", "Agent,Write", "--agents", agents];
                allowThrough = checks + 1;
                await Case("child-pressure", true, false, extra: childArguments);
                allowThrough = int.MaxValue;
                await Case("child-recovered", true, true, extra: childArguments);
                File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = true, Version = version, Authenticated = true, Results = results }));
                return 0;
            }
            if (!sessionOnly)
            {
            await Case("pressure", false, false);
            await Case("recovered", true, true);
            await Case("disconnected", true, false, "AgentLauncher-memory-" + Guid.NewGuid().ToString("N"));
            var settings = Path.Combine(root, "user-deny.json");
            File.WriteAllText(settings, JsonSerializer.Serialize(new { hooks = new { PreToolUse = new[] { new { matcher = "Write", hooks = new[] { new { type = "command", command = Environment.ProcessPath, args = new[] { "--qoder-deny-fixture" } } } } } } }));
            await Case("user-deny", true, false, extra: ["--setting-sources", "project"]);
            }
            // A print invocation normally exits after its answer. Streaming input keeps the
            // same real Qoder session alive so refusal/recovery cannot hide a restart.
            var continuousRoot = Path.Combine(root, "same-session");Directory.CreateDirectory(continuousRoot);
            var continuousMarker = Path.Combine(continuousRoot, "executed.txt");
            var continuous = new ProcessStartInfo(cli) { WorkingDirectory = continuousRoot, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var argument in new[] { "--plugin-dir", plugin, "--setting-sources", "", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--no-session-persistence", "--tools", "Write", "--allowed-tools", "Write", "--input-format", "stream-json", "--output-format", "stream-json", "--max-model-request-retries", "1", "-p" }) continuous.ArgumentList.Add(argument);
            continuous.Environment[OmpMemoryGuard.PipeEnvironment] = pipeName;
            allow = false;var continuousBefore = checks;
            using var session = Process.Start(continuous)!;
            var errors = session.StandardError.ReadToEndAsync();
            using var events = File.CreateText(Path.Combine(continuousRoot, "stdout.jsonl"));
            async Task Turn(string prompt)
            {
                await session.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = prompt } }));await session.StandardInput.FlushAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                while (true)
                {
                    var line = await session.StandardOutput.ReadLineAsync(deadline.Token);
                    if (line is null) throw new IOException("Qoder ended the session before its result.");
                    await events.WriteLineAsync(line);await events.FlushAsync();
                    using var json = JsonDocument.Parse(line);
                    if (json.RootElement.TryGetProperty("type", out var type) && type.GetString() == "result")
                    {
                        if (json.RootElement.TryGetProperty("is_error", out var error) && error.GetBoolean()) throw new IOException("Qoder returned an API error; no session recovery success claimed.");
                        return;
                    }
                }
            }
            try
            {
                await Turn($"Use Write exactly once to create {continuousMarker.Replace('\\', '/')} containing executed. On denial report BLOCKED and stop without retries.");
                if (session.HasExited || File.Exists(continuousMarker) || checks - continuousBefore != 1) throw new IOException("Pressure did not leave a blocked live session.");
                allow = true;
                await Turn($"Memory recovered. Use Write exactly once to create {continuousMarker.Replace('\\', '/')} containing executed, then report DONE.");
                if (session.HasExited || !File.Exists(continuousMarker) || checks - continuousBefore != 2) throw new IOException("Same-session recovery did not execute once.");
                session.StandardInput.Close();await session.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                File.WriteAllText(Path.Combine(continuousRoot, "stderr.txt"), await errors);
                if (session.ExitCode != 0) throw new IOException("Recovered session failed to close normally.");
                var sameSession = new { Name = "same-session-recovery", Code = session.ExitCode, Checks = checks - continuousBefore, SameProcess = true, Executed = true };
                results.Add(sameSession);Console.WriteLine(JsonSerializer.Serialize(sameSession));
            }
            finally { if (!session.HasExited) { session.Kill(true);await session.WaitForExitAsync(); } }
            File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = true, Version = version, Authenticated = true, Results = results }));
            return 0;
        }
        finally { stop.Cancel();await server; }
    }

    private static async Task<(int Code, string Output, string Error, double Seconds)> Run(string cli, string[] args, string cwd, string? pipe)
    {
        var start = new ProcessStartInfo(cli) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (pipe is not null) start.Environment[OmpMemoryGuard.PipeEnvironment] = pipe;
        var watch = Stopwatch.StartNew();using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync();var error = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120)); }
        catch { if (!child.HasExited) child.Kill(true);await child.WaitForExitAsync();throw; }
        return (child.ExitCode, await output, await error, watch.Elapsed.TotalSeconds);
    }
}
