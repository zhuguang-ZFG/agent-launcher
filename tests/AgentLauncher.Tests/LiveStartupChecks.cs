using System.Diagnostics;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class LiveStartupChecks
{
    // Opt-in acceptance: consumes the CLI's existing login, uses real system/Job samples,
    // and writes only inside a fresh isolated launcher root. Never run in the normal gate.
    public static async Task<int> RunAsync(string agent)
    {
        var isolated = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT");
        if (agent != "omp" || isolated is null || !Path.IsPathFullyQualified(isolated)
            || !Path.GetFileName(isolated).StartsWith("AgentLauncher-tests-live-startup-", StringComparison.Ordinal)
            || !string.Equals(Path.GetFullPath(isolated), Store.Root, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(Store.Root)) throw new IOException("Requires a fresh isolated integration build/root.");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAIL_AT")))
            throw new IOException("Live startup must not use fault injection or fake samples.");
        Directory.CreateDirectory(Store.Root);
        var cwd = Path.Combine(Store.Root, "work");Directory.CreateDirectory(cwd);
        var marker = Path.Combine(cwd, "startup-proof.txt");
        var token = "live-startup-" + Guid.NewGuid().ToString("N");
        var prompt = $"In this isolated acceptance directory, use your file write tool once to create {marker.Replace('\\', '/')} with exact content {token}. Then use your file read tool to read that file and report its exact content. Do not use shell, delegate, or access other files. If a hook denies a tool, report BLOCKED and stop without retry. Finish after reading the file.";
        var args = new[]{"--no-session", "--tools", "read,write", "--mode", "json", "-p", prompt};
        var profile = new AgentProfile { Arguments = args };
        var command = Commands.Resolve(agent, profile);
        var slot = new Slot { Enabled = true, AgentId = agent, Directory = cwd, Profiles = new() { [agent] = profile } };
        var settings = LongTaskResources.Create(new ResourceSettings());
        var config = new Configuration { Draft = new Draft { Project = cwd, Slots = [slot], Resource = settings } };
        Store.Save(Store.FilePath("configuration.json"), config);
        var item = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "Live startup " + agent, cwd,
            PhysicalDirectory.Workspace(cwd), false, command, settings.JobMemoryLimit,
            PlanningBytes: settings.PlanningBytes, ResourceMode: settings.Mode, SourceSlotId: slot.Id, AgentId: agent);
        Process? monitor = null;Process? runner = null;
        var samples = new List<object>();
        var passed = false;string? failure = null;
        try
        {
            var status = await Ipc.EnsureMonitorAsync();
            monitor = Process.GetProcessById(Store.Read<int>(Store.FilePath("monitor-pid.json")));_ = monitor.Handle;
            samples.Add(new { Stage = "before", status.Memory });
            if (!status.Ok || status.Memory is null) throw new IOException(status.Message);
            var policy = ResourcePolicy.SnapshotFor(settings, status.Memory.Total, status.Memory.CommitLimit);
            var decision = ResourcePolicy.Evaluate(status.Memory, policy, ResourcePolicy.ReservedFor(status), [ResourcePolicy.BudgetOf(item)]);
            Store.Save(Store.FilePath("admission.json"), new { policy, decision, status.Memory });
            if (decision.Kind != AdmissionKind.Allow) throw new IOException(decision.Reason);
            var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [item], DraftRevision: config.Draft.Revision,
                PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([item]), EnabledSlotIds: [slot.Id], Policy: policy));
            if (!reserved.Ok) throw new IOException(reserved.Message);
            var start = new ProcessStartInfo(Store.WorkerPath) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("run");start.ArgumentList.Add(item.Id.ToString());
            runner = Process.Start(start)!;runner.StandardInput.Close();
            var output = runner.StandardOutput.ReadToEndAsync();var error = runner.StandardError.ReadToEndAsync();
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (!runner.HasExited)
            {
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Real agent startup/tool check exceeded three minutes.");
                await Task.Delay(1000);
                var latest = await Ipc.SendAsync(new("status", Guid.NewGuid()));
                samples.Add(new { Stage = "running", latest.Memory, Receipt = Store.Read<Receipt>(Store.ReceiptPath(item.Id)) });
            }
            var text = await output;
            File.WriteAllText(Store.FilePath("stdout.jsonl"), text);
            File.WriteAllText(Store.FilePath("stderr.txt"), await error);
            var receipt = Store.Read<Receipt>(Store.ReceiptPath(item.Id));
            if (runner.ExitCode != 0 || receipt is not { Phase: "Finished", ExitCode: 0 }
                || !File.Exists(marker) || File.ReadAllText(marker).Trim() != token)
                throw new IOException("Real agent did not finish the isolated write; inspect output and receipts.");
            // Require actual read dispatch, not just a successful write or echoed prompt.
            var readTool = "read";
            if (!text.Split('\n').Any(line => HasReadCall(line, readTool)))
                throw new IOException("No real file-read tool call was recorded.");
            passed = true;
        }
        catch (Exception error) { failure = error.Message; }
        finally
        {
            // Reclaim only this test's private Job, never an external agent or shared daemon by name.
            try { using var job = new Job(Store.JobName(item.Id), openOnly: true);if (job.Members().Length > 0) Native.Check(Native.TerminateJobObject(job.Handle, 1)); }
            catch (System.ComponentModel.Win32Exception) { }
            if (runner is not null) { if (!runner.HasExited) await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));runner.Dispose(); }
            if (monitor is not null) { if (!monitor.HasExited) { monitor.Kill();await monitor.WaitForExitAsync(); }monitor.Dispose(); }
            Store.Save(Store.FilePath("live-startup.json"), new { Passed = passed, Failure = failure, Agent = agent, RealMemory = true,
                FakeSample = false, Settings = settings, Samples = samples, Receipt = Store.Read<Receipt>(Store.ReceiptPath(item.Id)) });
        }
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} real {agent} startup/write/read: {failure} Evidence: {Store.Root}");
        return passed ? 0 : 1;
    }

    private static bool HasReadCall(string line, string name)
    {
        try { using var json = JsonDocument.Parse(line);return Find(json.RootElement); }
        catch (JsonException) { return false; }
        bool Find(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("type", out var type) && type.GetString() is "tool_use" or "toolCall"
                    && element.TryGetProperty("name", out var tool) && tool.GetString() == name) return true;
                return element.EnumerateObject().Any(property => Find(property.Value));
            }
            return element.ValueKind == JsonValueKind.Array && element.EnumerateArray().Any(Find);
        }
    }
}
