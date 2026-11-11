using System.IO;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private readonly Sampler previewSampler = new();
    private bool previewOnly;
    private long snapshotGeneration = -1;
    private Task? resourceRefreshTask;
    private readonly Dictionary<(string Agent, string Path), (DateTimeOffset At, string Path)> commandLabelCache = [];

    private bool HasActiveEntries => status?.Entries?.Any(e => !Store.IsTerminal(e)) == true;
    private bool IsPreviewStale => previewOnly && status?.Memory is {} memory && DateTimeOffset.UtcNow - memory.At > TimeSpan.FromSeconds(ResourcePolicy.SampleTtlSeconds);

    private void UpdatePollingInterval()
    {
        var inProgress = busy || launchFinalizing || launchCancellation != null || !cancelTask.IsCompleted || submittedAttempts.Length > 0 || status?.Entries?.Any(e => e.State is "Pending" or "Creating") == true;
        timer.Interval = TimeSpan.FromSeconds(inProgress || commError != null ? 1 : HasActiveEntries ? 2 : 30);
    }

    private async Task LoadResourceViewAsync()
    {
        try
        {
            var recover = await Task.Run(() =>
            {
                var ledger = Store.Read<Ledger>(Store.FilePath("control.json"));
                if (ledger == null) return false;
                if (ledger.SchemaVersion != 1 || ledger.Entries == null || ledger.Entries.Any(e => e?.Plan == null)) throw new InvalidDataException("控制账本损坏或版本不支持，需核对后再启动。");
                return ledger.Entries.Any(e => !Store.IsTerminal(e));
            });
            if (recover || Ipc.MonitorRunning()) await RefreshAsync();
            else await RefreshLocalPreviewAsync();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            commError = "监控状态未确认 · " + e.Message;
            status = new(false, commError, status?.Entries);
            previewOnly = false;
            ErrorText.Text = commError;
            UpdateLaunch();
        }
    }

    private async Task PollResourcesAsync()
    {
        if (closeRequested || closing || refreshing) return;
        if (busy || launchFinalizing || launchCancellation != null || !cancelTask.IsCompleted || submittedAttempts.Length > 0 || HasActiveEntries || commError != null)
            await RefreshAsync();
        else
            await RefreshLocalPreviewAsync();
    }

    private async Task RefreshLocalPreviewAsync()
    {
        if (refreshing || closeRequested) return;
        refreshing = true;
        var previous=status;var generation=submissionGeneration;
        try
        {
            var memory = await Task.Run(previewSampler.Sample);
            if (!double.IsFinite(memory.CpuPercent))
            {
                await Task.Delay(250);
                memory = await Task.Run(previewSampler.Sample);
            }
            if(generation!=submissionGeneration||!ReferenceEquals(status,previous)||busy||launchCancellation!=null||HasActiveEntries)return;
            // This is display/preflight input only. LaunchAsync always obtains monitor authority before preparing/reserving.
            status = new(true, "本机资源预览；启动前重新核验", status?.Entries ?? [], memory);
            previewOnly = true;
            UpdateLaunch();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            status = new(false, "本机资源采样失败 · " + e.Message, status?.Entries);
            previewOnly = true;
            UpdateLaunch();
        }
        finally { refreshing = false; }
    }

    private async Task RefreshMonitorAsync()
    {
        refreshing = true;
        var generation = submissionGeneration;
        var previous = status;
        var signature = snapshotGeneration == generation && previous?.Ok == true && !previewOnly ? previous.StatusSignature : null;
        try
        {
            var response = await Ipc.EnsureMonitorAsync(signature == null ? null : previous?.Epoch, signature);
            if (generation != submissionGeneration) return;
            if (response.Unchanged)
            {
                if (signature == null || response.More || response.Epoch != previous?.Epoch || response.StatusSignature != signature || previous?.Entries == null)
                    throw new IOException("监控器返回的未变化快照无法核对，未释放启动门禁。");
                status = response with { Entries = previous.Entries };
            }
            else
            {
                status = response;
                if (response.Ok && !response.More && response.Entries is {} entries)
                {
                    snapshotGeneration = generation;
                    if (submittedAttempts.Length > 0)
                    {
                        submittedAttempts = submittedAttempts.Where(id => entries.Any(e => e.Plan.Id == id && e.State == "Pending")).ToArray();
                        if (submittedAttempts.Length == 0) submittedRevision = -1;
                    }
                }
            }
            previewOnly = false;
            if (commError != null && ErrorText.Text == commError) ErrorText.Text = "";
            commError = response.Ok ? null : response.Message;
            if (commError != null) ErrorText.Text = commError;
            RenderSessions();
            UpdateLaunch();
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException)
        {
            if (generation != submissionGeneration) return;
            commError = "监控器不可用 · " + e.Message;
            // A failed poll is not evidence that reservations ended. Keep the last entries and their counts.
            status = new(false, commError, previous?.Entries, Epoch: previous?.Epoch);
            previewOnly = false;
            ErrorText.Text = commError;
            UpdateLaunch();
        }
        finally { refreshing = false; }
    }

    private async Task<string> ResolveCommandLabelAsync(Slot slot)
    {
        var key = (slot.AgentId, slot.Current.Executable);
        if (commandLabelCache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(60)) return cached.Path;
        var command = await Task.Run(() => Commands.Resolve(slot.AgentId, slot.Current));
        if (commandLabelCache.Count >= 128) commandLabelCache.Clear();
        commandLabelCache[key] = (DateTimeOffset.UtcNow, command.Executable);
        return command.Executable;
    }

    private Task CheckFreshCommandsAsync()
    {
        commandLabelCache.Clear();
        return CheckCommandsAsync();
    }
}
