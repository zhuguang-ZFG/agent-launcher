namespace AgentLauncher.Core;

// GUI 侧只保存最新的完整快照。世代不连续的增量不允许接在旧画面后面：只接受更新的世代，
// 一旦 Worker 的世代饱和，就只接受同一世代且仍带饱和标记的全量替换。
public sealed class TerminalView
{
    public TerminalSnapshot? Current { get; private set; }
    public int AppliedCount { get; private set; }
    public int RejectedCount { get; private set; }
    public bool Saturated => Current?.GenerationSaturated == true;

    public bool Apply(TerminalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsWellFormed) { RejectedCount++; return false; }
        if (Current is { } current)
        {
            if (current.GenerationSaturated)
            {
                if (!(snapshot.GenerationSaturated && snapshot.Generation == current.Generation)) { RejectedCount++; return false; }
            }
            else if (snapshot.Generation <= current.Generation) { RejectedCount++; return false; }
        }
        Current = snapshot;
        AppliedCount++;
        return true;
    }
}