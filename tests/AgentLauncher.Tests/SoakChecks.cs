using System.Text;
using SoakRunner;

internal static class SoakChecks
{
    public static void Register(Action<string, Action> test, string temp)
    {
        void Assert(bool value) { if (!value) throw new Exception("soak evidence assertion failed"); }
        void Reject(Action action) { try { action(); } catch (Exception e) when (e is UsageException or ExecutionFailureException) { return; } throw new Exception("unsafe input accepted"); }
        test("S0.6 short and calibration rounds never qualify as stability", () =>
        {
            var result = Completed();
            EvidenceChecks.Finalize(result, true, false); Assert(!result.Stability.Eligible);
            result.Rounds[0].ObservedOverlapSeconds = 1799.999;
            EvidenceChecks.Finalize(result, false, false); Assert(!result.Stability.Eligible && result.Stability.CompletedRounds == 2);
            result.Rounds[0].ObservedOverlapSeconds = 1800; result.Rounds[0].Drivers.RemoveAt(0);
            EvidenceChecks.Finalize(result, false, false); Assert(!result.Stability.Eligible);
        });
        test("S0.6 success requires verified cleanup and successful status", () =>
        {
            var result = Completed();
            EvidenceChecks.Finalize(result, false, false); Assert(result.Stability.Eligible);
            result.Cleanup.Verified = false;
            EvidenceChecks.Finalize(result, false, false); Assert(!result.Stability.Eligible);
            result.Cleanup.Verified = true; result.Cleanup.Errors.Add("lost handle");
            EvidenceChecks.Finalize(result, false, false); Assert(!result.Stability.Eligible);
            result.Cleanup.Errors.Clear(); result.Status = "running";
            EvidenceChecks.Finalize(result, false, false); Assert(!result.Stability.Eligible);
        });
        test("S0.6 monotonic activity watchdog rejects stalls and later recovery", () =>
        {
            var guard = new ActivityWatch(); var limit = TimeSpan.FromSeconds(30);
            guard.Observe(0, TimeSpan.Zero, limit); guard.Observe(1, TimeSpan.FromSeconds(1), limit);
            Reject(() => guard.Observe(2, TimeSpan.FromSeconds(32), limit));
            Reject(() => guard.Observe(0, TimeSpan.FromSeconds(2), limit));
            var idle = new ActivityWatch(); idle.Observe(0, TimeSpan.Zero, limit);
            Reject(() => idle.Observe(0, TimeSpan.FromSeconds(31), limit));
        });
        test("S0.6 isolated paths reject equality aliases and existing roots", () =>
        {
            var root = Path.Combine(temp, "AgentLauncher-tests-soak-options");
            Assert(!JsonIo.IsUnder(root, root)); Assert(!JsonIo.IsUnder(root, root + "-other"));
            Assert(!JsonIo.IsUnder(root, Path.Combine(root, "..", "other")));
            Assert(JsonIo.IsUnder(root, Path.Combine(root, "output")));
            Directory.CreateDirectory(root);
            Reject(() => RunnerOptions.Parse(["observe", "--root", root]));
            var junction = Path.Combine(temp, "soak-junction");
            using var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/d", "/c", "mklink", "/J", junction, root } })!;
            Assert(link.WaitForExit(5000) && link.ExitCode == 0);
            Reject(() => EvidenceChecks.RequirePlainPath(Path.Combine(junction, "new-root")));
            // Remove only the link, never traverse its target.
            Directory.Delete(junction);
        });
        test("S0.6 arguments reject other mode options duplicates and incomplete duration", () =>
        {
            Reject(() => RunnerOptions.Parse(["driver", "--admission-only"]));
            Reject(() => RunnerOptions.Parse(["observe", "--state-root", temp]));
            Reject(() => RunnerOptions.Parse(["observe", "--root", temp, "--root", temp]));
            Reject(() => RunnerOptions.Parse(["observe", "--unknown", "1"]));
            Reject(() => RunnerOptions.Parse(["observe", "--admission-only=1"]));
        });
        test("S0.6 stability duration is fixed and calibration is explicitly separate", () =>
        {
            var runtime = Path.Combine(temp, "soak-options-runtime"); Directory.CreateDirectory(runtime);
            foreach (var name in new[] { "AgentLauncher.Worker.exe", "AgentLauncher.Worker.dll", "AgentLauncher.Core.dll", "AgentLauncher.Windows.dll", "SoakRunner.exe", "omp.exe", "node.exe" })
                File.WriteAllText(Path.Combine(runtime, name), "parameter fixture only; never executed");
            string[] options = ["observe", "--root", Path.Combine(temp, "AgentLauncher-tests-fresh-soak"), "--runtime", runtime, "--runner-exe", Path.Combine(runtime, "SoakRunner.exe"), "--omp", Path.Combine(runtime, "omp.exe"), "--omp-version", "18.19.0", "--node", Path.Combine(runtime, "node.exe")];
            var full = RunnerOptions.Parse(options); Assert(full.Rounds == 3 && full.RoundSeconds == 1800 && full.CalibrationSeconds == 0);
            var shortRun = RunnerOptions.Parse([.. options, "--calibration-seconds", "10"]); Assert(shortRun.Rounds == 1 && shortRun.CalibrationSeconds == 10);
            Reject(() => RunnerOptions.Parse([.. options, "--round-seconds", "10"]));
            Reject(() => RunnerOptions.Parse([.. options, "--rounds", "1"]));
            Reject(() => RunnerOptions.Parse([.. options, "--out", options[2]]));
            Reject(() => RunnerOptions.Parse([.. options, "--admission-only", "--rounds", "3"]));
            string[] driver = ["driver", "--omp", Path.Combine(runtime, "omp.exe"), "--omp-version", "18.19.0", "--node", Path.Combine(runtime, "node.exe"), "--state-root", temp, "--agent-dir", Path.Combine(temp,"agent"), "--home", Path.Combine(temp,"home"), "--stop-file", Path.Combine(temp,"stop"), "--result", Path.Combine(temp,"result")];
            Reject(() => RunnerOptions.Parse(driver));
            Assert(RunnerOptions.Parse([.. driver, "--duration-seconds", "10"]).DurationSeconds == 10);
            Reject(() => EvidenceChecks.ValidateRuntime(runtime));
            var protectedResult = Path.Combine(temp, "result"); File.WriteAllText(protectedResult, "keep this record");
            Reject(() => EvidenceChecks.ValidateDriverRoot(RunnerOptions.Parse([.. driver, "--duration-seconds", "10"]), temp));
            Assert(File.ReadAllText(protectedResult) == "keep this record");
        });
        test("S0.6 RPC disconnect fails once and never replays an unknown command", () =>
        {
            foreach (var mode in new[] { "ready-eof", "response-eof" })
            {
                var state = Path.Combine(temp, "rpc-disconnect-" + mode); Directory.CreateDirectory(state);
                var capture = Path.Combine(state, "requests.jsonl");
                var environment = ProcessTools.BuildChildEnvironment(null, Path.Combine(state, "home"), Path.Combine(state, "agent")).Variables;
                environment["AGENT_LAUNCHER_SOAK_RPC_FIXTURE"] = mode;
                environment["AGENT_LAUNCHER_SOAK_RPC_CAPTURE"] = capture;
                var rejected = false;
                try
                {
                    using var rpc = new OmpRpcClient(Environment.ProcessPath!, state, environment, Path.Combine(state, "stderr.log"), TimeSpan.FromSeconds(5));
                    if (mode == "ready-eof") Thread.Sleep(100);
                    rpc.Bash("test-unknown-request", TimeSpan.FromSeconds(5));
                }
                catch (Exception e) when (e is ProtocolException or IOException) { rejected = true; }
                Assert(rejected);
                if (mode == "response-eof") Assert(File.ReadAllLines(capture).Length == 1);
            }
        });
        test("S0.6 RPC framing preserves split UTF8 CRLF and rejects partial EOF", () =>
        {
            var lines = new List<string>(); var errors = new List<Exception>();
            using (var stream = new FragmentedStream(Encoding.UTF8.GetBytes("中文\r\n{\"type\":\"ready\"}\n")))
                OmpRpcClient.ReadBoundedLines(stream, 64, lines.Add, errors.Add);
            Assert(lines.SequenceEqual(new[] { "中文", "{\"type\":\"ready\"}" }) && errors.Count == 0);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"ready\"}")))
                OmpRpcClient.ReadBoundedLines(stream, 64, lines.Add, errors.Add);
            Assert(errors.Count == 1);
            using (var stream = new MemoryStream([0xff, 10])) OmpRpcClient.ReadBoundedLines(stream, 64, lines.Add, errors.Add);
            Assert(errors.Count == 2);
        });
        test("S0.6 RPC oversized frames fail before newline without growing buffers", () =>
        {
            var lines = new List<string>(); var errors = new List<Exception>();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 100) + "\nok\n"));
            OmpRpcClient.ReadBoundedLines(stream, 8, lines.Add, errors.Add);
            Assert(errors.Count == 1 && lines.SequenceEqual(new[] { "ok" }));
        });
        test("S0.6 tool shell arguments quote dollars backticks and apostrophes literally", () =>
        {
            Assert(DriverRunner.Quote("a$HOME`cmd`'b") == "'a$HOME`cmd`'\"'\"'b'");
        });
    }

    private static SoakResult Completed() => new()
    {
        Status = "succeeded", Cleanup = new CleanupEvidence { Verified = true },
        Rounds = Enumerable.Range(1, 3).Select(i => new RoundEvidence { Index = i, Kind = "stability", RequiredSeconds = 1800, ObservedOverlapSeconds = 1800, Passed = true, Drivers = [new(), new()] }).ToList()
    };

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
    }
}
