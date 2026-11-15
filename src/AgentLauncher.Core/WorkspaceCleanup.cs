namespace AgentLauncher.Core;

// Eligibility for explicit, user-initiated worktree cleanup (2026-11-13).
// Reference: BloopAI/vibe-kanban removes the worktrees it owns (crates/worktree-manager,
// git worktree remove even forced). We stay conservative: only an explicit click, only when no
// live session occupies the directory and git reports the tree completely clean (tracked or
// untracked changes block). Removal uses plain `git worktree remove` — git itself refuses dirty
// trees — and the branch with all committed work is always kept; only the checkout goes away.
public static class WorkspaceCleanup
{
    public readonly record struct Decision(bool Allowed, string Reason);

    public static Decision Evaluate(bool? sessionActive, string? statusPorcelain)
    {
        // Occupancy must come from a fresh, complete monitor status (advisory 2026-11-13): a display cache may be
        // stale or preview-only. Unknown (monitor unreachable / no entry list) always refuses.
        if (sessionActive is null) return new(false, "无法确认是否有会话占用该目录（监控器未提供完整状态），已阻止清理；请确认监控器运行后重试。");
        if (sessionActive.Value) return new(false, "仍有会话占用该目录；结束并核对完成后才能清理。");
        if (statusPorcelain is null) return new(false, "无法确认目录的 Git 状态，暂不清理；可在该目录手动运行 git status 核对。");
        if (statusPorcelain.Length > 0)
            return new(false, "目录里还有未提交、未跟踪或被 .gitignore 忽略的内容（如本地 .env、构建产物），已阻止清理；分支会保留，已提交内容不会丢。请先合并、备份或手动处理后再试。");
        return new(true, "目录干净：将用 git worktree remove 移除该工作目录；分支与已提交内容保留，不会被删除。");
    }
}
