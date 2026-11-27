namespace AgentLauncher.Core;

public static class StartPageSurface
{
    public static string CapDenied => "最多同时 " + WorkbenchLayout.MaxPanes + " 个";
    public const string StartingButton = "正在启动…";
    public const string BackToStart = "回到起步页";
    public const string RestartPane = "重新启动";
    public const string CheckingCapacity = "正在检查";
    public const string UnknownCapacity = "暂不清楚";

    public static bool CanSelect(int alreadySelected, bool ready) =>
        ready && alreadySelected < WorkbenchLayout.MaxPanes;

    public static string LaunchButtonText(int readyCount, bool starting) =>
        starting ? StartingButton : "启动 " + readyCount + " 个";

    public static string DisabledReason(bool hasProject, int readySelectedCount)
    {
        if (!hasProject) return "还没有选项目";
        if (readySelectedCount <= 0) return "还没有选 Agent";
        return "";
    }

    public static string SelectionLine(int selected) => "已选 " + selected + " / 最多 " + WorkbenchLayout.MaxPanes;

    public static string CapacityLine(int? remaining) =>
        remaining is null ? UnknownCapacity : "按当前测量，大约还可再开 " + remaining.Value + " 个";

    public static string PartialLaunchLine(int running, int missing, string reason) =>
        "当前运行 " + running + " / 最多 " + WorkbenchLayout.MaxPanes + "，还差 " + missing + "，因为" + reason;
}