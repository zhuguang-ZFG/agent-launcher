using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public sealed record TaskRecoveryEvidence(TaskReservationClaim? Claim, ControlEntry? Original, Receipt? Receipt, string Failure)
{
    // A receipt alone cannot establish the ownership or finality of a missing attempt.
    public bool CanReplaceIntent => Failure.Length == 0 && (Claim is null || Original is not null && Store.IsTerminal(Original));
}

/// <summary>Read-only evidence lookup. It never creates a monitor, releases occupancy, or infers completion from absence.</summary>
public static class TaskRecoveryReader
{
    public static Ledger? ReadLedger()
    {
        var path = Store.FilePath("control.json");
        var ledger = Store.Read<Ledger>(path);
        if (ledger is null && File.Exists(path + ".bak")) throw new InvalidDataException("控制记录主文件缺失，仍有备份。需要维护者核对；当前保留所有占用。");
        return ledger is null ? null : LedgerMigration.Accept(ledger);
    }

    public static ControlEntry? ReadAttempt(Guid id, Ledger? ledger = null)
    {
        var retained = ledger?.Entries.FirstOrDefault(entry => entry.Plan.Id == id);
        if (retained is not null)
        {
            if (retained.Digest != Store.Digest(retained.Plan)) throw new InvalidDataException("运行记录身份无法核对，保留未知状态。");
            return retained;
        }
        var outcome = Store.Read<ArchivedAttempt>(Path.Combine(Store.Root, "attempts", id.ToString("N"), "outcome.json"));
        if (outcome is null) return null;
        if (outcome.SchemaVersion != 1 || outcome.AttemptId != id || outcome.Entry?.Plan?.Id != id || !Store.IsTerminal(outcome.Entry)
            || outcome.Digest != outcome.Entry.Digest || outcome.Digest != Store.Digest(outcome.Entry.Plan))
            throw new InvalidDataException("历史记录身份无法核对，保留未知状态。");
        return outcome.Entry;
    }

    public static TaskRecoveryEvidence Inspect(Guid taskId, Guid entryId)
    {
        try
        {
            var ledger = ReadLedger();
            var claim = ledger?.TaskClaims.FirstOrDefault(item => item.EntryId == entryId);
            var original = claim is null ? null : ReadAttempt(claim.AttemptId, ledger);
            if (original is not null && (original.Plan.SourceSlotId != taskId || original.Digest != Store.Digest(original.Plan)))
                return new(claim, null, null, "原尝试与本任务身份不一致，请联系维护者核对。");
            var receipt = claim is null ? null : Store.Read<Receipt>(Store.ReceiptPath(claim.AttemptId));
            return new(claim, original, receipt, "");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(null, null, null, e.Message);
        }
    }
}
