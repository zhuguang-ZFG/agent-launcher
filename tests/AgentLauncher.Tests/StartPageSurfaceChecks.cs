using AgentLauncher.Core;

namespace AgentLauncher.Tests;

internal static class StartPageSurfaceChecks
{
    public static void Run(Action<string, Action> Test)
    {
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }

        Test("start page button is 启动 N 个 or 正在启动…", () =>
        {
            Assert(StartPageSurface.LaunchButtonText(3, false) == "启动 3 个", "idle");
            Assert(StartPageSurface.LaunchButtonText(0, false) == "启动 0 个", "zero still reports the count");
            Assert(StartPageSurface.LaunchButtonText(6, true) == "正在启动…", "starting");
        });

        Test("seventh ready agent cannot be selected", () =>
        {
            Assert(StartPageSurface.CanSelect(0, true), "first");
            Assert(StartPageSurface.CanSelect(5, true), "sixth");
            Assert(!StartPageSurface.CanSelect(6, true), "seventh");
            Assert(!StartPageSurface.CanSelect(0, false), "unready");
            Assert(StartPageSurface.CapDenied == "最多同时 " + WorkbenchLayout.MaxPanes + " 个", "copy");
        });

        Test("disabled and capacity lines stay factual", () =>
        {
            Assert(StartPageSurface.DisabledReason(false, 0) == "还没有选项目", "project");
            Assert(StartPageSurface.DisabledReason(true, 0) == "还没有选 Agent", "agents");
            Assert(StartPageSurface.DisabledReason(true, 2) == "", "ready");
            Assert(StartPageSurface.SelectionLine(3) == "已选 3 / 最多 " + WorkbenchLayout.MaxPanes, "selection");
            Assert(StartPageSurface.CapacityLine(2) == "按当前测量，大约还可再开 2 个", "remaining");
            Assert(StartPageSurface.CapacityLine(null) == "暂不清楚", "unknown");
            Assert(StartPageSurface.CheckingCapacity == "正在检查", "checking");
            Assert(StartPageSurface.PartialLaunchLine(4, 2, "内存不足") == "当前运行 4 / 最多 " + WorkbenchLayout.MaxPanes + "，还差 2，因为内存不足", "partial");
        });
    }
}