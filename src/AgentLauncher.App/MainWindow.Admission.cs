using System.IO;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task<Guid?> AuthorizePlansAsync(LaunchItem[] items)
    {
        await RefreshAsync();
        launchCancellation?.Token.ThrowIfCancellationRequested();
        if (status?.Ok != true || status.Memory == null) throw new IOException(status?.Message ?? "资源读数不可用。");
        if (status.AdmissionBlockedReason is { Length: > 0 } blockedReason) throw new IOException(blockedReason);
        var binding = ReservationBindings(items);
        var reserved = (status.Entries ?? []).Where(e => !Store.IsTerminal(e)).Select(e => ResourcePolicy.BudgetOf(e.Plan)).ToArray();
        var requested = items.Select(ResourcePolicy.BudgetOf).ToArray();
        var decision = ResourcePolicy.Evaluate(status.Memory, binding.Policy, reserved, requested);
        if (decision.Kind == AdmissionKind.Deny) throw new IOException(decision.Reason);
        if (decision.Kind == AdmissionKind.Allow) return null;
        if (!ConfirmResourceWaiver(items, decision, status.Memory)) throw new OperationCanceledException("已取消一次性资源确认。");
        launchCancellation?.Token.ThrowIfCancellationRequested();
        var grant = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), items,
            DraftRevision: binding.Revision, PolicyHash: binding.Hash, PlanDigest: binding.Digest,
            EnabledSlotIds: binding.Slots, Policy: binding.Policy), launchCancellation?.Token ?? default);
        // Allow needs no waiver; the normal reserve still rechecks resource and workspace protections.
        if (grant.Decision == nameof(AdmissionKind.Allow)) return null;
        if (!grant.Ok) throw new IOException(grant.Message);
        if (grant.Decision != nameof(AdmissionKind.RequireWaiver) || grant.WaiverId is not {} id)
            throw new IOException("监控器未签发有效的一次性授权，未提交启动。");
        return id;
    }
}
