using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class QoderMemoryChecks
{
    public static void Run(Action<string, Action> test, string temp)
    {
        const string request = "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"private-value-do-not-log\"}}";
        test("ACC-R11 Qoder plugin preserves literal argv and existing settings and plugins", () =>
        {
            var args = new[] { "--settings", "user settings.json", "--plugin-dir", "existing plugin", "--", "prompt --plugin-dir" };
            foreach (var node in new[] { false, true })
            {
                var original = new CommandSpec("qoder.exe", node ? ["C:\\entry path\\qoder.js", ..args] : args, node ? "node-shim" : "exe");
                var attached = QoderMemoryGuard.Attach(original);
                Assert(QoderMemoryGuard.IsAttached(attached), "guard marker absent");
                Assert(QoderMemoryGuard.Detach(attached).Arguments.SequenceEqual(original.Arguments), "arguments or Node entry changed");
                var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "guard", temp, new(temp, "workspace"), false, attached, 2 * ResourcePolicy.GiB);
                Assert(Store.Digest(plan) != Store.Digest(plan with { Command = original }), "plugin omitted from frozen plan identity");
                var moved = attached.Arguments.ToArray();moved[node ? 2 : 1] = Path.Combine(temp, Path.GetFileName(QoderMemoryGuard.PluginPath));
                Refuses(() => QoderMemoryGuard.IsAttached(attached with { Arguments = moved }), "changed plugin location accepted");
            }
        });
        test("ACC-R11 Node.exe memory guard options follow the entry script", () =>
        {
            var extension = Path.Combine(AppContext.BaseDirectory, OmpMemoryGuard.ExtensionFile);
            foreach (var executable in new[] { @"C:\fake\node.exe", @"C:\fake\NODE.EXE" })
                foreach (var script in new[] { @"C:\fake\cli.js", @"C:\fake\cli.cjs", @"C:\fake\cli.mjs" })
                {
                    var original = new CommandSpec(executable, [script, "--flag"], "exe");
                    var omp = OmpMemoryGuard.Attach(original);
                    Assert(omp.Arguments[0] == script && omp.Arguments[1] == "--extension" && omp.Arguments[2] == extension && omp.Arguments[3] == "--flag", "Omp guard options were not placed after the Node entry script");
                    Assert(OmpMemoryGuard.IsAttached(omp), "Omp marker after the entry script was not recognized");
                    Assert(OmpMemoryGuard.Detach(omp).Arguments.SequenceEqual(original.Arguments), "Omp detach did not restore the Node entry argv");
                    var qoder = QoderMemoryGuard.Attach(original);
                    Assert(qoder.Arguments[0] == script && qoder.Arguments[1] == "--plugin-dir" && qoder.Arguments[2] == QoderMemoryGuard.PluginPath && qoder.Arguments[3] == "--flag", "Qoder guard options were not placed after the Node entry script");
                    Assert(QoderMemoryGuard.IsAttached(qoder), "Qoder marker after the entry script was not recognized");
                    Assert(QoderMemoryGuard.Detach(qoder).Arguments.SequenceEqual(original.Arguments), "Qoder detach did not restore the Node entry argv");
                }
        });
        test("ACC-R11 plain exe memory guard options stay at argv[0]", () =>
        {
            var omp = OmpMemoryGuard.Attach(new CommandSpec(@"C:\fake\omp.exe", ["--flag"], "exe"));
            Assert(omp.Arguments[0] == "--extension" && omp.Arguments[2] == "--flag", "Omp plain exe insertion point changed");
            var qoder = QoderMemoryGuard.Attach(new CommandSpec(@"C:\fake\qoder.exe", ["--flag"], "exe"));
            Assert(qoder.Arguments[0] == "--plugin-dir" && qoder.Arguments[2] == "--flag", "Qoder plain exe insertion point changed");
            Assert(QoderMemoryGuard.Attach(new CommandSpec(@"C:\fake\node.exe", ["--flag"], "exe")).Arguments[0] == "--plugin-dir", "Node.exe without an entry script was treated as script-first");
        });
        test("ACC-R11 Node.exe without an entry script is rejected for guarded agents", () =>
        {
            var directory = Path.Combine(temp, "node-entry");Directory.CreateDirectory(directory);
            var node = Path.Combine(directory, "node.exe");File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), node);
            var script = Path.Combine(directory, "cli.js");File.WriteAllText(script, "// entry");
            const string expected = "Node.exe 手动配置必须以入口脚本（.js/.cjs/.mjs）作为第一个参数，否则无法附加内存保护组件。";
            void Rejected(Action action, string message)
            {
                try { action(); }
                catch (IOException e) { Assert(e.Message == expected, message);return; }
                throw new Exception(message);
            }
            Rejected(() => Commands.Resolve("omp", new() { Executable = node, Arguments = ["--flag"] }), "Omp accepted Node.exe without an entry script");
            Rejected(() => Commands.Resolve("qoder", new() { Executable = node }), "Qoder accepted Node.exe without an entry script");
            var resolved = Commands.Resolve("qoder", new() { Executable = node, Arguments = [script, "--flag"] });
            Assert(resolved.Adapter == "exe" && resolved.Arguments[0] == script && resolved.Arguments[1] == "--plugin-dir" && resolved.Arguments[3] == "--flag", "guarded Node.exe plan did not keep the entry script first");
            Assert(QoderMemoryGuard.IsAttached(resolved) && QoderMemoryGuard.Detach(resolved).Arguments.SequenceEqual(new[] { script, "--flag" }), "guarded Node.exe plan lost its marker round-trip");
        });
        test("ACC-R11 generated Qoder plugin is additive immutable and uses exec argv", () =>
        {
            var storage = Path.Combine(temp, "qoder plugin space & quote '");Directory.CreateDirectory(storage);
            var settings = Path.Combine(storage, "settings.json");File.WriteAllText(settings, "user configuration must stay unchanged");
            QoderMemoryGuard.Prepare(Environment.ProcessPath!, storage);
            var root = QoderMemoryGuard.DirectoryFor(Environment.ProcessPath!, storage);
            var path = Path.Combine(root, "hooks", "hooks.json");var bytes = File.ReadAllText(path);
            using var parsed = JsonDocument.Parse(bytes);
            var hooks = parsed.RootElement.GetProperty("hooks");
            Assert(hooks.EnumerateObject().Count() == 2, "unexpected events");
            foreach (var name in new[] { "SessionStart", "PreToolUse" })
            {
                var hook = hooks.GetProperty(name)[0].GetProperty("hooks")[0];
                Assert(hook.GetProperty("command").GetString() == Environment.ProcessPath, "executable not literal");
                Assert(hook.GetProperty("args")[0].GetString() == "qoder-memory-hook" && !hook.TryGetProperty("async", out _), "hook is asynchronous or uses a shell");
            }
            QoderMemoryGuard.Prepare(Environment.ProcessPath!, storage);
            Assert(File.ReadAllText(path) == bytes && File.ReadAllText(settings) == "user configuration must stay unchanged", "configuration changed");
            File.WriteAllText(path, "{}");
            Refuses(() => QoderMemoryGuard.Prepare(Environment.ProcessPath!, storage), "changed hook silently overwritten");
            Assert(File.ReadAllText(path) == "{}", "changed hook was overwritten");
        });
        test("ACC-R11 Qoder healthy memory leaves ordinary tool permissions unchanged", () =>
        {
            var result = Exchange(request, "{\"allowed\":true,\"reason\":\"healthy\"}\n");
            Assert(result.Request == 1 && result.Output.Trim() == "{}", "healthy result bypassed permissions");
        });
        test("ACC-R11 Qoder pressure denies before tools and recovers without replay", () =>
        {
            var denied = Exchange(request, "{\"allowed\":false,\"reason\":\"pressure\"}\n");
            Assert(IsDenied(denied.Output) && denied.Output.Contains("pressure"), "pressure did not deny");
            Assert(!denied.Output.Contains("private-value"), "tool arguments leaked into diagnostics");
            Assert(Exchange(request, "{\"allowed\":true,\"reason\":\"healthy\"}\n").Output.Trim() == "{}", "recovery remained blocked");
        });
        test("ACC-R11 Qoder malformed oversized closed and missing endpoint responses deny", () =>
        {
            foreach (var response in new[] { "oops\n", "{}\n", "{\"allowed\":true}\n", new string('x',4097), "" })
                Assert(IsDenied(Exchange(request, response).Output), "invalid response allowed a tool");
            Assert(IsDenied(Invoke(request, null)), "missing endpoint allowed a tool");
            Assert(IsDenied(Invoke(request, "other-pipe")), "invalid pipe name allowed a tool");
        });
        test("ACC-R11 Qoder invalid hook events and oversized input deny without disclosure", () =>
        {
            foreach (var input in new[] { "", "{", "{}", "{\"hook_event_name\":\"PostToolUse\"}", "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":null}", new string('x',QoderMemoryHook.MaxInputBytes+1) })
                Assert(IsDenied(Invoke(input, null)), "invalid input allowed a tool");
        });
        test("ACC-R11 Qoder SessionStart handshake never grants tool permissions", () =>
        {
            var result = Exchange("{\"hook_event_name\":\"SessionStart\"}", "{\"allowed\":true,\"reason\":\"loaded\"}\n");
            Assert(result.Request == 2 && !result.Output.Contains("permissionDecision"), "hello granted permissions or queried tool memory");
            using var json = JsonDocument.Parse(result.Output);
            Assert(json.RootElement.GetProperty("systemMessage").GetString()!.Length > 0, "no load disclosure");
        });
        test("ACC-R11 Qoder silent endpoint has a bounded refusal", () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert(IsDenied(Exchange(request, null).Output), "silent endpoint allowed a tool");
            Assert(watch.Elapsed < TimeSpan.FromSeconds(5), "hook wait is unbounded");
        });
        test("ACC-R11 Qoder hook uses real Job pressure and leaves the Job intact", () =>
        {
            using var job = new Job("Local\\AL-qoder-" + Guid.NewGuid().ToString("N"), 2 * ResourcePolicy.GiB);
            using var guard = new OmpMemoryGuard(job, null, 1, 1);
            Assert(Invoke(request, guard.PipeName).Trim() == "{}", "healthy native Job denied");
            job.SetMemoryLimit(256 * ResourcePolicy.MiB);
            Assert(IsDenied(Invoke(request, guard.PipeName)), "low native headroom allowed");
            Assert(job.QueryMemory()?.LimitBytes == 256 * ResourcePolicy.MiB, "hook changed hard limit");
            job.SetMemoryLimit(2 * ResourcePolicy.GiB);
            Assert(Invoke(request, guard.PipeName).Trim() == "{}", "native Job did not recover");
        });
    }

    private static string Invoke(string input, string? pipe)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));using var output = new StringWriter();
        Assert(QoderMemoryHook.RunAsync(stream, output, pipe).GetAwaiter().GetResult() == 0, "hook returned non-blocking error code");
        return output.ToString();
    }

    private static (byte Request, string Output) Exchange(string input, string? response)
    {
        var name = "AgentLauncher-memory-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte request = 0;
        var server = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(deadline.Token);
            var bytes = new byte[1];await pipe.ReadExactlyAsync(bytes, deadline.Token);request = bytes[0];
            if (response is not null)
            {
                // Fragment the frame to exercise the bounded reader.
                try
                {
                    foreach (var part in Encoding.UTF8.GetBytes(response).Chunk(37)) await pipe.WriteAsync(part, deadline.Token);
                }
                catch (IOException) when (response.Length > 4096) { /* The bounded client closes an oversized frame early. */ }
                // Named pipe writes are not buffered by this stream. Flush can race a client
                // that has already consumed the entire response and closed its endpoint.
                pipe.Disconnect();
            }
        });
        var result = Invoke(input, name);
        server.GetAwaiter().GetResult();
        return (request, result);
    }

    private static bool IsDenied(string json)
    {
        using var value = JsonDocument.Parse(json);var hook = value.RootElement.GetProperty("hookSpecificOutput");
        return hook.GetProperty("hookEventName").GetString() == "PreToolUse" && hook.GetProperty("permissionDecision").GetString() == "deny";
    }
    private static void Refuses(Action action, string message) { try { action(); } catch (IOException) { return; } throw new Exception(message); }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
}
