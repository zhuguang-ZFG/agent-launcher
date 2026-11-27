#if INTEGRATION_TESTS
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private sealed record UiCustodyRecord(int Pid, string BootId, DateTimeOffset Started);

    private async Task StopUiFixtureProcessesAsync(string folder, DateTimeOffset fixtureStarted)
    {
        if (!Path.GetFileName(Store.Root).StartsWith("AgentLauncher-tests-", StringComparison.OrdinalIgnoreCase))
            throw new IOException("UI fixture cleanup requires an isolated test root.");

        timer.Stop(); memoryTimer.Stop(); queueEnabled = false; taskTimer.Stop(); queueCancellation?.Cancel();
        await Task.WhenAll(launchTask, cancelTask, quickQueueTask, queuePumpTask, commandCheckTask, saveTask);
        ReviewKillMonitor();

        var recordPath = Store.FilePath("custody.json");
        var record = Store.Read<UiCustodyRecord>(recordPath);
        if (record is null)
        {
            SaveEvidence(null, "not-started");
            return;
        }
        if (record.Pid <= 0 || record.BootId != Store.BootId() || record.Started < fixtureStarted || record.Started > DateTimeOffset.UtcNow)
            throw new IOException("UI fixture custody record does not belong to this run.");

        Process process;
        try { process = Process.GetProcessById(record.Pid); }
        catch (ArgumentException) { SaveEvidence(record.Pid, "already-exited"); return; }
        using (process)
        {
            if (process.HasExited) { SaveEvidence(record.Pid, "already-exited"); return; }
            // Pin the process handle before checking creation time, so PID reuse cannot redirect Kill().
            _ = process.Handle;
            var started = process.StartTime.ToUniversalTime();
            var recordedDelay = record.Started.UtcDateTime - started;
            if (recordedDelay < TimeSpan.Zero || recordedDelay > TimeSpan.FromSeconds(30)
                || !StringComparer.OrdinalIgnoreCase.Equals(process.MainModule?.FileName, Store.WorkerPath))
                throw new IOException("UI fixture custody process identity changed.");

            Job? root=null;
            try { root = new Job(Store.RootJob, openOnly: true); }
            catch (Win32Exception error) when (error.NativeErrorCode == 2)
            {
                // No named root remains after monitor shutdown; custody may be in its idle exit grace.
            }
            using (root)
            {
                // Fixture ledger restoration can remove a finished Git attempt before custody reaps it.
                // Keep its root handle open and prove every descendant has exited before stopping this owner.
                // Production custody deliberately keeps unknown attempts; only this isolated UI teardown overrides it.
                if (root is not null && root.Members().Length != 0) throw new IOException("UI fixture root still contains managed processes; custody was retained.");
                if (Store.Read<UiCustodyRecord>(recordPath) != record || process.StartTime.ToUniversalTime() != started)
                    throw new IOException("UI fixture custody identity changed before cleanup.");
                if (!process.HasExited)
                {
                    process.Kill();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(deadline.Token);
                }
                if (!process.HasExited || root is not null && root.Members().Length != 0)
                    throw new IOException("UI fixture custody cleanup did not finish with an empty root.");
            }
            SaveEvidence(record.Pid, "stopped-after-empty-root");
        }

        void SaveEvidence(int? pid, string result) => Store.Save(Path.Combine(folder, "ui-fixture-cleanup.json"),
            new { Passed = true, Root = Store.Root, Scope = Store.Scope, CustodyPid = pid, Result = result, At = DateTimeOffset.UtcNow });
    }
}
#endif
