using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

internal static class AgentIdentityChecks
{
    public static void Run(Action<string, Action> test, string temp)
    {
        var root = Path.Combine(temp, "command-identity");
        Directory.CreateDirectory(root);
        CommandSpec Node(string script) => new(Environment.ProcessPath!, [script, "--raw", "literal & value"], "node-shim");
        test("CR-N04 explicit Node entry survives capability delivery and resume probes", () =>
        {
            var node=Commands.FindExe("node.exe")??throw new IOException("Node required for explicit-entry regression");
            var script=Path.Combine(root,"explicit-entry.cjs");
            File.WriteAllText(script,"const a=process.argv.slice(2);if(a.includes('--version'))console.log('98.76.54');else if(a.includes('--help'))console.log('Usage: fixture [PROMPT] [SESSION_ID]\\n --print  Non-interactive mode\\n --resume <id>');else process.exit(7);");
            foreach(var agent in new[]{"codex","claude","omp"})
            {
                var profile=new AgentProfile{Executable=node,Arguments=[script,"--model","model name"]};
                var resolved=Commands.Resolve(agent,profile);
                Assert(resolved.Adapter=="node-shim","explicit script lost its identity adapter");
                var capability=AgentProbe.ProbeCapabilityAsync(agent,profile,root,TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
                Assert(capability.Version=="98.76.54","probe ran Node or forwarded user args instead of the entry");
                Assert(ResumeCapability.CheckAsync(agent,profile,root,capability).GetAwaiter().GetResult() is not null,"entry resume help unavailable");
                var task=new TaskCard{AgentId=agent,Profile=profile,Project=root,Name="entry fixture",Description="test prompt",DeliveryMode="automatic"};
                var delivery=TaskDelivery.PrepareAsync(task).GetAwaiter().GetResult();
                Assert(delivery.Arguments[0]==script,"automatic mode displaced the Node entry");
                Assert(agent!="codex"||delivery.Arguments[1]=="exec","Codex command is before the Node entry");
                var before=AgentProbe.Fingerprint(resolved);
                File.AppendAllText(script,"\n// upgrade "+agent);
                Assert(AgentProbe.Fingerprint(Commands.Resolve(agent,profile))!=before,"explicit entry upgrade retained identity");
            }
        });
        test("CR-N04 Node identity detects entry changes with unchanged runtime and argv", () =>
        {
            var script = Path.Combine(root, "agent.js");
            File.WriteAllText(script, "console.log('one')");
            var command = Node(script);
            var before = AgentProbe.Fingerprint(command);
            File.WriteAllText(script, "console.log('two')");
            Assert(before != AgentProbe.Fingerprint(command), "entry upgrade retained the frozen identity");
        });
        test("CR-N04 Node identity includes package metadata and distinguishes entries on one runtime", () =>
        {
            var package = Path.Combine(root, "package");
            var bin = Path.Combine(package, "bin");
            Directory.CreateDirectory(bin);
            var script = Path.Combine(bin, "agent.js");
            var other = Path.Combine(bin, "other.js");
            File.WriteAllText(script, "same entry");
            File.WriteAllText(other, "same entry");
            var manifest = Path.Combine(package, "package.json");
            File.WriteAllText(manifest, "{\"name\":\"fixture\",\"version\":\"1.0.0\"}");
            var before = AgentProbe.Fingerprint(Node(script));
            File.WriteAllText(manifest, "{\"name\":\"fixture\",\"version\":\"2.0.0\"}");
            Assert(before != AgentProbe.Fingerprint(Node(script)), "package version change retained the frozen identity");
            Assert(AgentProbe.Fingerprint(Node(script)) != AgentProbe.Fingerprint(Node(other)), "two packages on one runtime shared an identity");
            File.Delete(manifest);
            Assert(before != AgentProbe.Fingerprint(Node(script)), "missing package metadata retained the frozen identity");
        });
        test("CR-N04 program identity preserves native compatibility and ignores user and resume arguments", () =>
        {
            var direct = new CommandSpec(Environment.ProcessPath!, ["--hold", "1"], "exe");
            Assert(AgentProbe.Fingerprint(direct) == AgentProbe.Fingerprint(direct.Executable), "native identity changed");
            var script = Path.Combine(root, "session.js");
            File.WriteAllText(script, "session entry");
            var command = Node(script);
            Assert(AgentProbe.Fingerprint(command) == AgentProbe.Fingerprint(command with { Arguments = [script, "--resume", "private-session"] }), "session arguments changed program identity");
        });
        test("CR-N04 missing Node entry and unsupported adapter cannot fall back to runtime identity", () =>
        {
            foreach (var command in new[] { Node(Path.Combine(root, "missing.js")), Node("relative.js"), Node("") with { Arguments = [] }, Node("") with { Adapter = "unknown" } })
            {
                var refused = false;
                try { AgentProbe.Fingerprint(command); } catch (Exception e) when (e is IOException or InvalidDataException) { refused = true; }
                Assert(refused, "unverifiable command identity was accepted");
            }
        });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
