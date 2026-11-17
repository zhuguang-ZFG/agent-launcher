using System.ComponentModel;
using System.Text;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

// Process shell for explicit worktree cleanup. All git calls go through ReviewGit.CaptureAsync
// (pinned non-interactive config, bounded output, timeout). `worktree remove` runs WITHOUT --force:
// git itself refuses a dirty tree, which is the second line of defense after the porcelain check.
public static class WorkspaceCleanupShell
{
    public static async Task<IReadOnlyList<CleanupFile>> InventoryAsync(string git,string target,CancellationToken ct)
    {
        var result=await ReviewGit.CaptureAsync(git,target,["status","--porcelain=v1","-z","--untracked-files=all","--ignored=matching"],ct,30,256*1024);
        RequireComplete(result,"列出阻止清理的文件");
        return CleanupInventory.Parse(Encoding.UTF8.GetString(result.Output));
    }
    // Occupancy must compare physical identity, not raw strings: monitor entries carry Resolve()d worktree roots
    // (final path name + volume:index key) while workspace records keep the original spelling. Overlaps also covers
    // a session anchored in any subdirectory of the checkout. Returns null when occupancy cannot be proven either
    // way (missing entries, or the target cannot be resolved) — callers must treat null as refusal.
    public static bool? OccupiedByLiveSession(IReadOnlyList<ControlEntry>? entries, string target)
    {
        if (entries == null) return null;
        if (!Directory.Exists(target)) return false; // a gone directory is refused by the git-state check instead
        WorkspaceIdentity identity;
        try { identity = PhysicalDirectory.Resolve(target); }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException) { return null; }
        return entries.Any(entry => !Store.IsTerminal(entry) && PhysicalDirectory.Overlaps(entry.Plan.Workspace, identity));
    }

    public static async Task<string> ReadStatusPorcelainAsync(string git, string target, CancellationToken ct)
    {
        // A clean detached HEAD can be the only reference to committed work. Removing the checkout
        // must not discard that reference while promising the user that a branch keeps the commits.
        var branch = await ReviewGit.CaptureAsync(git, target, ["symbolic-ref", "--quiet", "HEAD"], ct, 60, 4096);
        if (branch.Code == 1 && !branch.TimedOut && !branch.Truncated)
            throw new IOException("目录处于游离 HEAD 状态，提交可能尚未保存在分支上；请先创建或切换到保留这些提交的分支，再清理。");
        RequireComplete(branch, "核对工作目录分支");
        var branchName = Encoding.UTF8.GetString(branch.Output).Trim();
        if (!branchName.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new IOException("无法确认工作目录的本地分支，已阻止清理。");
        var reference = await ReviewGit.CaptureAsync(git, target, ["show-ref", "--verify", "--quiet", branchName], ct, 60, 4096);
        RequireComplete(reference, "核对保留提交的分支");

        // Porcelain and non-forced removal can both overlook files marked assume-unchanged or
        // skip-worktree. Refuse those indexes without changing flags or sparse-checkout settings.
        var index = await ReviewGit.CaptureAsync(git, target, ["ls-files", "-v", "-z"], ct, 60, 256 * 1024);
        RequireComplete(index, "核对工作目录索引");
        foreach (var entry in Encoding.UTF8.GetString(index.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry[0] == 'S' || char.IsLower(entry[0]))
                throw new IOException("目录索引含 assume-unchanged 或 skip-worktree 标记，Git 状态可能隐藏本地修改；请先核对文件并处理这些标记（含稀疏检出），再清理。");
        }
        // --ignored=matching: files ignored via .gitignore (.env, local data, build output) also block cleanup;
        // git's own check_clean_worktree does not see them either, so the no-force remove would silently delete them.
        var result = await ReviewGit.CaptureAsync(git, target, ["status", "--porcelain=v1", "--untracked-files=normal", "--ignored=matching"], ct, 60, 256 * 1024);
        RequireComplete(result, "读取目录 Git 状态");
        return Encoding.UTF8.GetString(result.Output);
    }

    public static async Task RemoveAsync(string git, string root, string target, CancellationToken ct)
    {
        // Keep the destructive OS boundary guarded even if a caller inspected the tree earlier.
        // The caller still owns the fresh monitor occupancy check and explicit user confirmation.
        var status = await ReadStatusPorcelainAsync(git, target, ct);
        var decision = WorkspaceCleanup.Evaluate(sessionActive: false, statusPorcelain: status);
        if (!decision.Allowed) throw new IOException(decision.Reason);
        var result = await ReviewGit.CaptureAsync(git, root, ["worktree", "remove", target], ct, 300, 256 * 1024);
        if (result.TimedOut) throw new TimeoutException("移除工作目录超时；目录可能未变化，请核对后重试。");
        if (result.Code != 0) throw new IOException("Git 拒绝移除该目录（未做强制删除）：" + ReviewGit.Clean(result.Error));
        if (Directory.Exists(target)) throw new IOException("Git 报告成功但目录仍存在；未做强制删除，请手动检查。");
    }

    private static void RequireComplete(ProcessCapture result, string action)
    {
        if (result.TimedOut) throw new TimeoutException(action + "超时，已阻止清理。");
        if (result.Truncated) throw new IOException(action + "输出超过核对上限，已阻止清理。");
        if (result.Code != 0) throw new IOException(action + "失败，已阻止清理：" + ReviewGit.Clean(result.Error));
    }
}
