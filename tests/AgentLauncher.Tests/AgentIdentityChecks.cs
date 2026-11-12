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
