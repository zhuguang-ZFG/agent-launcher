using System.IO;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private static readonly string ConcurrencyLimitMessage=$"同时运行的 Agent 窗格最多 {ResourcePolicy.MaxConcurrentAgents} 个；请先结束或撤销部分 Agent 后再启动。";
    // Pinned wording shared by the queue pause, the per-entry hold and the launch hint so the three pressure gates never drift.
    private const string PressureMessage="资源压力尚未连续恢复，稍后重新检查。";

    private int ActiveAgentCount()=>status?.Ok==true&&status.Entries is not null&&!status.More?status.Entries.Where(e=>e.Plan.Kind is null or "agent").Where(e=>!Store.IsTerminal(e)).DistinctBy(e=>e.Plan.Id).Count():0;
    private bool ConcurrencyBlocked(int requestedAgents)=>ActiveAgentCount()+requestedAgents>ResourcePolicy.MaxConcurrentAgents;
    // The pending launch's own mode: both the fresh launch and the retry path build their reservation policy from this same draft setting,
    // so a practical/custom launch is never stopped by a pressure latch the monitor does not apply to it.
    private bool LaunchesConservatively=>string.Equals((config.Draft.Resource??new ResourceSettings()).Mode,"conservative",StringComparison.Ordinal);

    private async Task AuthorizePlansAsync(LaunchItem[] items)
    {
        await RefreshAsync();
        launchCancellation?.Token.ThrowIfCancellationRequested();
        if (status?.Ok != true || status.Memory == null) throw new IOException(status?.Message ?? "资源读数不可用。");
        if (status.AdmissionHardBlockedReason is { Length: > 0 } blockedReason) throw new IOException(blockedReason);
        // Pressure is advisory for practical/custom launches; only a conservative launch keeps today's hard stop here.
        if (status.AdmissionPressure && LaunchesConservatively) throw new IOException(PressureMessage);
        if (ConcurrencyBlocked(items.Count(item => item.Kind is null or "agent"))) throw new IOException(ConcurrencyLimitMessage);
        var binding = ReservationBindings(items);
        var reserved = ResourcePolicy.ReservedFor(status);
        var requested = items.Select(item => ResourcePolicy.BudgetOf(item, item.PoolId is null ? null : binding.Policy.PoolMemoryLimit)).ToArray();
        var decision = ResourcePolicy.Evaluate(status.Memory, binding.Policy, reserved, requested);
        // Conservative denies throw; practical mode allows and only surfaces the advisory reason in the launch hint.
        if (decision.Kind == AdmissionKind.Deny) throw new IOException(decision.Reason);
    }
}
