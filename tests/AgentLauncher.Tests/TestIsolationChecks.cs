using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentLauncher.Tests;

internal static class TestIsolationChecks
{
    public static void Register(Action<string, Action> test, string temp)
    {
        var root = Path.Combine(temp, "test-isolation-checks");
        var working = Path.Combine(root, "working");
        Directory.CreateDirectory(working);
        var executable = Path.Combine(AppContext.BaseDirectory, "AgentLauncher.Tests.exe");
#if INTEGRATION_TESTS
        var hooksConfirmed = false;
#endif

        (int Code, string Output) Probe(string? requested, string[]? arguments = null)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = working, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments ?? ["--check-test-isolation"]) start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("AGENT_LAUNCHER_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            if (requested is not null) start.Environment["AGENT_LAUNCHER_TEST_ROOT"] = requested;
            var result = Run(start);
            Assert(!Directory.EnumerateFileSystemEntries(working).Any(), "Isolation check wrote a state file into its working directory");
            return result;
        }

        void Refused(string? requested)
        {
            var result = Probe(requested);
            Assert(result.Code == 78 && result.Output.Contains("REFUSED:"), "Unsafe test invocation was not rejected before entry: " + result.Output);
            if (!string.IsNullOrWhiteSpace(requested) && Path.IsPathFullyQualified(requested))
                Assert(!Directory.Exists(requested), "Refused invocation created its state directory: " + requested);
        }

        test("R17 stateful test entrypoint rejects missing relative and misleading roots before creating state", () =>
        {
            foreach (var requested in new string?[] { null, " ", "AgentLauncher-tests-relative", Path.Combine(root, "ordinary-root"), Path.Combine(root, "prefix-AgentLauncher-tests-not-owned") }) Refused(requested);
        });

        test("R17 isolation check accepts explicit test root only when test hooks are compiled", () =>
        {
            var requested = Path.Combine(root, "AgentLauncher-tests-valid-" + Guid.NewGuid().ToString("N"));
            var result = Probe(requested);
#if INTEGRATION_TESTS
            Assert(result.Code == 0 && !result.Output.Contains("REFUSED:"), "Test build did not honor the explicit isolated root: " + result.Output);
            hooksConfirmed = true;
#else
            Assert(result.Code == 78 && result.Output.Contains("INTEGRATION_TESTS"), "Ordinary build accepted stateful test mode: " + result.Output);
#endif
            Assert(!Directory.Exists(requested), "Read-only isolation check created its accepted root");
        });

        test("R17 production descendant cannot be disguised as an isolated test root", () =>
        {
            var production = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentLauncher");
            var leaf = "AgentLauncher-tests-refuse-" + Guid.NewGuid().ToString("N");
            var requested = Path.Combine(production, leaf);
            Refused(requested);
            if (requested.Length > 2 && requested[1] == ':') Refused(@"\\?\" + requested);
            // Existing production state is inspected only for an alternate spelling. Nothing is created there.
            var shortPath = new StringBuilder(32768);
            var length = GetShortPathName(production, shortPath, (uint)shortPath.Capacity);
            if (length > 0 && length < shortPath.Capacity && !string.Equals(shortPath.ToString(), production, StringComparison.OrdinalIgnoreCase))
                Refused(Path.Combine(shortPath.ToString(), leaf));
        });

        test("R17 junction roots and ancestors are refused without writing through the link", () =>
        {
            var target = Path.Combine(root, "junction-target"); Directory.CreateDirectory(target);
            var marker = Path.Combine(target, "retained.txt"); File.WriteAllText(marker, "untouched fixture");
            var junction = Path.Combine(root, "AgentLauncher-tests-junction");
            var script = Path.Combine(root, "create-junction.ps1");
            File.WriteAllText(script, "param([string]$Link,[string]$Target)\n$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null\n");
            var create = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Link", junction, "-Target", target }) create.ArgumentList.Add(argument);
            var created = Run(create);
            Assert(created.Code == 0, "Could not construct isolated junction fixture: " + created.Output);
            try
            {
                foreach (var requested in new[] { junction, Path.Combine(junction, "AgentLauncher-tests-child") })
                {
                    var result = Probe(requested);
                    Assert(result.Code == 78 && result.Output.Contains("REFUSED:"), "Directory link bypassed test isolation: " + result.Output);
                }
                Assert(Directory.GetFileSystemEntries(target).Length == 1 && File.ReadAllText(marker) == "untouched fixture", "Guard wrote through the refused directory link");
            }
            finally { Directory.Delete(junction); }
        });

#if INTEGRATION_TESTS
        test("R17 native sync repair and terminal fixture dispatch refuse an invalid root before state writes", () =>
        {
            // Only a test build may exercise actual dispatch modes. Its root override is verified
            // above, so even a future missing guard stays inside this test-owned directory.
            Assert(hooksConfirmed, "Refusing to probe stateful dispatch because this Windows assembly did not confirm test-root hooks");
            foreach (var arguments in new string[][] { [], ["--sync-reliability-checks"], ["--repair-checks"], ["--integration"], ["-w", "fixture"], ["--startup-gate"], ["--misspelled-test-mode"] })
            {
                var requested = Path.Combine(root, "invalid-mode-root-" + Guid.NewGuid().ToString("N"));
                var result = Probe(requested, arguments);
                Assert(result.Code == 78 && result.Output.Contains("REFUSED:"), "Stateful dispatch bypassed isolation: " + string.Join(' ', arguments) + " " + result.Output);
                Assert(!Directory.Exists(requested), "Stateful dispatch created files before refusal: " + string.Join(' ', arguments));
            }
        });
#endif
    }

    private static (int Code, string Output) Run(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("Could not start test isolation child");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(true); process.WaitForExit(5000);
            throw new TimeoutException("Test isolation child did not finish before its deadline");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint capacity);
}
