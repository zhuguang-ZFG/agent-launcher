using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

// 启动主路径的文案与目标短语。只依赖枚举和创建常量，不读取 mutagen list 全文。
internal static class LaunchSurfaceChecks
{
    public static int Run()
    {
        var failed = 0;
        void Test(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
        }
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }

        Test("UX15 one local place stays 本机", () =>
        {
            Assert(LaunchSurface.LaunchButtonText(2, new string?[] { null, null }, false, false) == "启动 2 个", LaunchSurface.LaunchButtonText(2, new string?[] { null, null }, false, false));
            Assert(LaunchSurface.TargetPhrase(new string?[] { null, null }) == "本机", "single local place");
            Assert(LaunchSurface.TargetTooltip(new string?[] { null, null }).Length == 0, "single place tooltip");
        });
        Test("UX15 one cloud host is named and starting replaces the verb", () =>
        {
            Assert(LaunchSurface.LaunchButtonText(1, new string?[] { "京云" }, false, true) == "启动 1 个", LaunchSurface.LaunchButtonText(1, new string?[] { "京云" }, false, true));
            Assert(LaunchSurface.TargetPhrase(new string?[] { "京云" }) == "京云", "named host");
            Assert(LaunchSurface.LaunchButtonText(1, new string?[] { "京云" }, true, true) == "正在启动…", "busy label");
        });
        Test("UX15 two different places do not collapse to the first host", () =>
        {
            var label = LaunchSurface.LaunchButtonText(2, new string?[] { "京云", "阿里云" }, false, false);
            Assert(label == "启动 2 个", label);
            Assert(LaunchSurface.TargetPhrase(new string?[] { "京云", "阿里云" }) == "多个目标", "many places");
            Assert(!label.Contains("京云") && !label.Contains("阿里云"), "button named one host");
            var mixed = LaunchSurface.LaunchButtonText(3, new string?[] { null, "京云", "京云" }, false, false);
            Assert(mixed == "启动 3 个", mixed);
            Assert(LaunchSurface.TargetPhrase(new string?[] { null, "京云", "京云" }) == "多个目标", "mixed places");
            Assert(LaunchSurface.TargetTooltip(new string?[] { null, "京云" }) == "本次同时包含：本机、京云", LaunchSurface.TargetTooltip(new string?[] { null, "京云" }));
        });
        Test("UX15 none says this run is not peer and is refused", () =>
        {
            var text = LaunchSurface.SyncHonesty(WorkspaceSyncMode.None);
            Assert(text.Contains("这次不是同权"), text);
            Assert(text.Contains("拒收"), text);
            Assert(!text.Contains("仍可启动"), text);
            Assert(text.Contains("实时输入"), text);
            Assert(text.Contains("不是实时一致"), text);
        });
        Test("UX15 mutagen copy uses the create-mode constant and does not claim convergence", () =>
        {
            var text = LaunchSurface.SyncHonesty(WorkspaceSyncMode.Mutagen);
            Assert(text.Contains(MutagenCreateMode.SyncMode), text);
            Assert(MutagenCreateMode.SyncMode == "two-way-safe", MutagenCreateMode.SyncMode);
            Assert(text.Contains("不是已收敛"), text);
            Assert(!text.Contains("全部已收敛"), text);
            Assert(text.Contains("保留双方内容"), text);
        });
        Test("UX15 terminal flush names sync-final and does not say 已收敛", () =>
        {
            var at = DateTimeOffset.Parse("2026-11-18T00:00:00Z");
            var synced = WorkspaceInsights.EvaluateSyncBadge(new SyncHealth(SyncHealthState.NotSynced, "", "gone"), new SyncFinalRecord(1, SyncFinalRecord.Synced, "远端改动已收敛落回本机", at));
            Assert(synced.Label == "已同步", synced.Label);
            Assert(synced.Detail.Contains("sync-final"), synced.Detail);
            Assert(synced.Detail.Contains("只表示该次结束时 flush 成功"), synced.Detail);
            Assert(!synced.Detail.Contains("已收敛"), synced.Detail);
            var problem = WorkspaceInsights.EvaluateSyncBadge(new SyncHealth(SyncHealthState.NotSynced, "", ""), new SyncFinalRecord(1, SyncFinalRecord.Problem, "flush 失败", at));
            Assert(problem.Label == "同步有问题", problem.Label);
            Assert(problem.Detail.Contains("sync-final") && problem.Detail.Contains("flush 失败") && !problem.Detail.Contains("已收敛"), problem.Detail);
        });
        Test("UX15 shared-directory and change-review sentences stay specific", () =>
        {
            Assert(LaunchSurface.SharedDirectoryRisk.Contains("互相覆盖"), LaunchSurface.SharedDirectoryRisk);
            Assert(LaunchSurface.ChangesAreLocalGit.Contains("本机工作目录") && LaunchSurface.ChangesAreLocalGit.Contains("不能证明"), LaunchSurface.ChangesAreLocalGit);
            Assert(LaunchSurface.ReattachEffect.Contains("工作 Shell") && LaunchSurface.ReattachEffect.Contains("未恢复原 Agent") && LaunchSurface.ReattachEffect.Contains("不会自动重连") && LaunchSurface.ReattachEffect.Contains("也不会释放名额"), LaunchSurface.ReattachEffect);
            Assert(LaunchSurface.ReleaseEffect.Contains("不会结束远端进程") && LaunchSurface.ReleaseEffect.Contains("也不会重新挂接"), LaunchSurface.ReleaseEffect);
            Assert(LaunchSurface.LocalReconcileNote.Contains("不是远程名额释放"), LaunchSurface.LocalReconcileNote);
        });
        return failed == 0 ? 0 : 1;
    }
}
