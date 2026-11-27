# 工作台界面重设计 Implementation Plan

> 2026-11-27 修订：用户将并发上限从 8 个改为 6 个。本计划一律以 6 为准。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把主窗口改成「一页起步、启动后变成最多 6 个真实可交互窗格的工作台」，看起来更干净，用起来只做选项目、勾选、启动和看着它们一起跑。

**Architecture:** 先在 `AgentLauncher.Core` 里把窗口状态、网格几何、起步页文案和主题色值写成可单测的纯函数，WPF 只负责绑定和展示。起步页和工作台是同一主窗口的两种互斥状态；工作台复用已有嵌入终端（`TerminalCard` / `TryShowEmbeddedAsync`），用新的 `WorkbenchLayout` 排成网格，不再把静态分屏预览或外部 Windows Terminal 当作启动后的默认界面。不修改 `ResourcePolicy` 阈值，也不改现有的 `PaneLayout`（那是外部 Terminal 分屏算法，最多 16 格，与本设计无关）。

**Tech Stack:** .NET 8、WPF（`src/AgentLauncher.App`）、Core 纯模型（`src/AgentLauncher.Core`）、现有 `tests/AgentLauncher.Tests` 的 `Test`/`Assert` 风格，以及 `INTEGRATION_TESTS` 下的窗口检查。

## Global Constraints

- 规格文件：`docs/superpowers/specs/2026-11-27-workspace-ui-redesign.md`（提交 `51ab5f7`）。规格与本计划冲突时以规格为准。
- 最多 6 个真实 Agent 并发，不以 8 或 16 为窗格目标。
- 不修改资源阈值、CPU 默认、Job 默认或预留下限。界面只改变展示与操作合流，准入仍走现有 `ResourcePolicy`。
- 空窗格、只保活、或串行排队都不算成功。
- 启动按钮只有一颗，文案是「启动 N 个」或「正在启动…」，主表面不再出现「检查资源并启动」「等待资源后启动」并排按钮，也不再把「本机预览不是启动授权」写在主表面。
- 默认深色；浅色只在设置中切换。新配置 `Theme` 默认改为 `"dark"`。已保存为 `"system"` 或 `"light"` 的配置不要自动改写。
- 实现必须在干净的独立工作树和分支上进行。禁止在 `D:\Users\pj\agent-launcher`（`fix/window-placement`，含大量未提交改动）上直接改文件、stash、reset、clean 或提交。
- 不要启动或结束用户正在用的真实 Agent，不要替换正在使用的安装包、全局 Terminal 配置或快捷方式。
- 不要为了让旧界面检查通过而保留左侧列表、中间槽位表单、分屏预览三套重复界面。旧检查要改到新结构。
- 不要修改 `PaneLayout`、外部 Windows Terminal 分屏参数生成，或 Job / 远程协议 / 同步算法。

---

## File Structure

- Create: `src/AgentLauncher.Core/WorkbenchLayout.cs` — 窗口模式、窗格状态、网格几何（1/2/2×2/2×3/2×4、窄屏单列、最小 420×240）。
- Create: `src/AgentLauncher.Core/StartPageSurface.cs` — 起步页和工作台主路径文案（启动按钮、禁用原因、容量、部分启动、第 7 个拒绝）。
- Create: `src/AgentLauncher.Core/ThemeTokens.cs` — 深色/浅色色值常量，供 `ApplyTheme` 和测试共用。
- Create: `tests/AgentLauncher.Tests/WorkbenchLayoutChecks.cs`
- Create: `tests/AgentLauncher.Tests/StartPageSurfaceChecks.cs`
- Create: `tests/AgentLauncher.Tests/ThemeTokensChecks.cs`
- Create: `src/AgentLauncher.App/MainWindow.Workbench.cs` — 工作台显隐、网格摆放、窗格铬条、加窗格、返回起步页、键盘。
- Create: `src/AgentLauncher.App/WorkbenchUiChecks.cs` — 新结构的窗口检查（`#if INTEGRATION_TESTS`）。
- Modify: `src/AgentLauncher.Core/LaunchSurface.cs` — 主按钮文案改为「启动 N 个」/「正在启动…」，地点短语不再写进按钮。
- Modify: `src/AgentLauncher.Core/Models.cs` — 新配置 `Theme` 默认 `"dark"`。
- Modify: `tests/AgentLauncher.Tests/LaunchSurfaceChecks.cs` — 同步按钮文案断言。
- Modify: `tests/AgentLauncher.Tests/Program.cs` — 注册新的模型检查。
- Modify: `src/AgentLauncher.App/App.xaml` — 默认色和圆角。
- Modify: `src/AgentLauncher.App/MainWindow.xaml` — 起步页去掉轨道、预览和折叠区；增加工作台面板。
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs` — `ApplyTheme`、`UpdateLaunch`、`LaunchClick` 接入新状态。
- Modify: `src/AgentLauncher.App/MainWindow.Embedded.cs` — 嵌入终端改由工作台网格承载，不再把标签页当作默认界面。
- Modify: `src/AgentLauncher.App/MainWindow.Simple.cs` — 去掉起步页对第二颗检查按钮和折叠区标题的依赖。
- Modify: `src/AgentLauncher.App/UiChecks.cs`、`SimpleUiChecks.cs` 以及其它仍断言旧主表面的检查 — 改到新结构。
- Modify: `docs/UI-DESIGN.md` — 在文首指向本规格与本计划，说明主路径已改为起步页/工作台。不要在本任务里重写整份旧 UI 设计。

---

### Task 1: 工作树、窗口模式和网格几何

**Files:**
- Create: `src/AgentLauncher.Core/WorkbenchLayout.cs`
- Create: `tests/AgentLauncher.Tests/WorkbenchLayoutChecks.cs`
- Modify: `tests/AgentLauncher.Tests/Program.cs`（在 `PaneLayoutChecks.Run(Test);` 之后增加 `WorkbenchLayoutChecks.Run(Test);`）

**Interfaces:**
- Consumes: 无。不要调用或修改 `PaneLayout`。
- Produces:
  - `public enum MainWindowMode { StartPage, Workbench }`
  - `public enum WorkbenchPaneStatus { Starting, Running, Waiting, Pressure, Exited }`
  - `public static class WorkbenchLayout`
  - `public const int MaxPanes = 6`
  - `public const int MinPaneWidth = 420`
  - `public const int MinPaneHeight = 240`
  - `public const double DefaultGap = 8`
  - `public static (int Columns, int Rows) GridFor(int paneCount)`
  - `public static bool UseSingleColumn(double width, int paneCount, double gap = DefaultGap)`
  - `public static WorkbenchLayoutResult Layout(double width, double height, int paneCount, double gap = DefaultGap)`
  - `public readonly record struct WorkbenchTile(int Index, double X, double Y, double Width, double Height)`
  - `public sealed record WorkbenchLayoutResult(int Columns, int Rows, bool SingleColumn, IReadOnlyList<WorkbenchTile> Tiles)`
  - `public static MainWindowMode AfterLaunchAttempt(int startedCount)`
  - `public static MainWindowMode AfterAllPanesExited()`
  - `public static bool ShowBackToStart(MainWindowMode mode, int runningOrStartingCount)`

- [ ] **Step 1: 建立干净工作树，不要动脏工作区**

在执行阶段使用 `using-git-worktrees`。从已经包含规格的提交建分支和工作树：

```powershell
git fetch --no-tags
git worktree add -b feat/workspace-ui-redesign-20261127 "D:\Users\pj\agent-launcher-workspace-ui-20261127" docs/workspace-ui-redesign-20261127
```

若 `docs/workspace-ui-redesign-20261127` 在本机就是 `51ab5f7`（或其后续仅文档提交），直接以该提交为起点。确认 `D:\Users\pj\agent-launcher` 的未提交改动仍在，且新工作树 `git status` 干净。后续所有编辑都在新工作树里进行。

- [ ] **Step 2: 写失败的网格与状态测试**

创建 `tests/AgentLauncher.Tests/WorkbenchLayoutChecks.cs`：

```csharp
using AgentLauncher.Core;

namespace AgentLauncher.Tests;

internal static class WorkbenchLayoutChecks
{
    public static void Run(Action<string, Action> Test)
    {
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }

        Test("workbench grid maps 1-6 panes and rejects 7", () =>
        {
            Assert(WorkbenchLayout.GridFor(0) == (0, 0), "empty");
            Assert(WorkbenchLayout.GridFor(1) == (1, 1), "1");
            Assert(WorkbenchLayout.GridFor(2) == (2, 1), "2 wide");
            Assert(WorkbenchLayout.GridFor(3) == (2, 2), "3");
            Assert(WorkbenchLayout.GridFor(4) == (2, 2), "4");
            Assert(WorkbenchLayout.GridFor(5) == (2, 3), "5");
            Assert(WorkbenchLayout.GridFor(6) == (2, 3), "6");
            try { WorkbenchLayout.GridFor(7); throw new Exception("7 should throw"); }
            catch (ArgumentOutOfRangeException) { }
            try { WorkbenchLayout.GridFor(-1); throw new Exception("-1 should throw"); }
            catch (ArgumentOutOfRangeException) { }
        });

        Test("narrow width falls back to a scrolling single column", () =>
        {
            Assert(!WorkbenchLayout.UseSingleColumn(420 * 2 + 8, 4), "wide 4");
            Assert(WorkbenchLayout.UseSingleColumn(420 * 2 + 8 - 1, 4), "just too narrow");
            Assert(WorkbenchLayout.UseSingleColumn(2000, 1), "one pane is a single cell");
            Assert(!WorkbenchLayout.UseSingleColumn(2000, 0), "empty is not a column");
            var narrow = WorkbenchLayout.Layout(500, 400, 3);
            Assert(narrow.SingleColumn && narrow.Columns == 1 && narrow.Rows == 3 && narrow.Tiles.Count == 3, "3 stacked");
            Assert(narrow.Tiles.All(t => t.Width == 500 && t.Height >= WorkbenchLayout.MinPaneHeight), "min height and full width");
            Assert(narrow.Tiles[0].Y == 0 && narrow.Tiles[1].Y == narrow.Tiles[0].Height + 8, "stacked with gap");
            Assert(narrow.Tiles[0].Height + 8 + narrow.Tiles[1].Height + 8 + narrow.Tiles[2].Height > 400, "overflows so the host can scroll");
        });

        Test("wide layout packs 5 panes as 2x3 without stretching the last cell", () =>
        {
            var wide = WorkbenchLayout.Layout(1000, 800, 5);
            Assert(!wide.SingleColumn && wide.Columns == 2 && wide.Rows == 3 && wide.Tiles.Count == 5, "2x3");
            Assert(wide.Tiles[0].X == 0 && wide.Tiles[1].X > wide.Tiles[0].X, "row-major");
            Assert(wide.Tiles[4].X == 0 && wide.Tiles[4].Width == wide.Tiles[0].Width, "last cell stays one column wide");
            Assert(wide.Tiles.All(t => t.Width > 0 && t.Height > 0), "positive sizes");
        });

        Test("two panes are side by side when wide and stacked when narrow", () =>
        {
            var side = WorkbenchLayout.Layout(1200, 600, 2);
            Assert(!side.SingleColumn && side.Tiles.Count == 2 && side.Tiles[0].Y == side.Tiles[1].Y, "side by side");
            var stacked = WorkbenchLayout.Layout(500, 800, 2);
            Assert(stacked.SingleColumn && stacked.Tiles[0].X == stacked.Tiles[1].X && stacked.Tiles[1].Y > stacked.Tiles[0].Y, "stacked");
        });

        Test("zero panes layout is empty and window mode stays on the workbench after exits", () =>
        {
            var empty = WorkbenchLayout.Layout(800, 600, 0);
            Assert(empty.Tiles.Count == 0 && empty.Columns == 0 && empty.Rows == 0 && !empty.SingleColumn, "empty result");
            Assert(WorkbenchLayout.AfterLaunchAttempt(0) == MainWindowMode.StartPage, "launch none");
            Assert(WorkbenchLayout.AfterLaunchAttempt(1) == MainWindowMode.Workbench, "launch one");
            Assert(WorkbenchLayout.AfterAllPanesExited() == MainWindowMode.Workbench, "stay");
            Assert(WorkbenchLayout.ShowBackToStart(MainWindowMode.Workbench, 0), "offer return");
            Assert(!WorkbenchLayout.ShowBackToStart(MainWindowMode.Workbench, 2), "still running");
            Assert(!WorkbenchLayout.ShowBackToStart(MainWindowMode.StartPage, 0), "already there");
        });
    }
}
```

在 `tests/AgentLauncher.Tests/Program.cs` 里 `AgentLauncher.Tests.PaneLayoutChecks.Run(Test);` 正下方加入：

```csharp
AgentLauncher.Tests.WorkbenchLayoutChecks.Run(Test);
```

- [ ] **Step 3: 运行测试，确认因类型不存在而失败**

Run:

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

Expected: 编译失败，或测试失败，信息包含 `WorkbenchLayout` 不存在。还没有实现时不要修别的失败。

- [ ] **Step 4: 写最小实现**

创建 `src/AgentLauncher.Core/WorkbenchLayout.cs`：

```csharp
namespace AgentLauncher.Core;

public enum MainWindowMode
{
    StartPage,
    Workbench
}

public enum WorkbenchPaneStatus
{
    Starting,
    Running,
    Waiting,
    Pressure,
    Exited
}

public readonly record struct WorkbenchTile(int Index, double X, double Y, double Width, double Height);

public sealed record WorkbenchLayoutResult(int Columns, int Rows, bool SingleColumn, IReadOnlyList<WorkbenchTile> Tiles);

public static class WorkbenchLayout
{
    public const int MaxPanes = 6;
    public const int MinPaneWidth = 420;
    public const int MinPaneHeight = 240;
    public const double DefaultGap = 8;

    public static (int Columns, int Rows) GridFor(int paneCount)
    {
        if (paneCount is < 0 or > MaxPanes) throw new ArgumentOutOfRangeException(nameof(paneCount));
        return paneCount switch
        {
            0 => (0, 0),
            1 => (1, 1),
            2 => (2, 1),
            <= 4 => (2, 2),
            _ => (2, 3)
        };
    }

    public static bool UseSingleColumn(double width, int paneCount, double gap = DefaultGap)
    {
        if (paneCount <= 0) return false;
        if (paneCount == 1) return true;
        return width < MinPaneWidth * 2 + gap;
    }

    public static WorkbenchLayoutResult Layout(double width, double height, int paneCount, double gap = DefaultGap)
    {
        if (paneCount is < 0 or > MaxPanes) throw new ArgumentOutOfRangeException(nameof(paneCount));
        if (paneCount == 0) return new WorkbenchLayoutResult(0, 0, false, Array.Empty<WorkbenchTile>());
        var single = UseSingleColumn(width, paneCount, gap);
        if (single)
        {
            var available = height - gap * (paneCount - 1);
            var even = available / paneCount;
            var tileHeight = even < MinPaneHeight ? MinPaneHeight : even;
            var tiles = new WorkbenchTile[paneCount];
            for (var i = 0; i < paneCount; i++)
                tiles[i] = new WorkbenchTile(i, 0, i * (tileHeight + gap), width, tileHeight);
            return new WorkbenchLayoutResult(1, paneCount, true, tiles);
        }

        var (columns, rows) = GridFor(paneCount);
        var tileWidth = (width - gap * (columns - 1)) / columns;
        var tileHeightWide = (height - gap * (rows - 1)) / rows;
        var tilesWide = new List<WorkbenchTile>(paneCount);
        for (var i = 0; i < paneCount; i++)
        {
            var row = i / columns;
            var column = i % columns;
            tilesWide.Add(new WorkbenchTile(i, column * (tileWidth + gap), row * (tileHeightWide + gap), tileWidth, tileHeightWide));
        }
        return new WorkbenchLayoutResult(columns, rows, false, tilesWide);
    }

    public static MainWindowMode AfterLaunchAttempt(int startedCount) =>
        startedCount >= 1 ? MainWindowMode.Workbench : MainWindowMode.StartPage;

    public static MainWindowMode AfterAllPanesExited() => MainWindowMode.Workbench;

    public static bool ShowBackToStart(MainWindowMode mode, int runningOrStartingCount) =>
        mode == MainWindowMode.Workbench && runningOrStartingCount == 0;
}
```

- [ ] **Step 5: 再跑模型测试，确认新检查通过**

Run:

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

Expected: 编译成功，输出含 `PASS workbench grid maps 1-6 panes and rejects 7` 等 5 条新检查，且最终失败数为 0。

- [ ] **Step 6: Commit**

```powershell
git add src/AgentLauncher.Core/WorkbenchLayout.cs tests/AgentLauncher.Tests/WorkbenchLayoutChecks.cs tests/AgentLauncher.Tests/Program.cs
git commit -m "feat: add workbench grid geometry and window mode transitions."
```

---

### Task 2: 起步页和工作台主路径文案

**Files:**
- Create: `src/AgentLauncher.Core/StartPageSurface.cs`
- Create: `tests/AgentLauncher.Tests/StartPageSurfaceChecks.cs`
- Modify: `src/AgentLauncher.Core/LaunchSurface.cs`（只改 `LaunchButtonText`）
- Modify: `tests/AgentLauncher.Tests/LaunchSurfaceChecks.cs`
- Modify: `tests/AgentLauncher.Tests/Program.cs`（在 Task 1 的注册后增加 `StartPageSurfaceChecks.Run(Test);`）

**Interfaces:**
- Consumes: `WorkbenchLayout.MaxPanes`
- Produces:
  - `public static class StartPageSurface`
  - `public const string CapDenied = "最多同时 6 个"`
  - `public const string StartingButton = "正在启动…"`
  - `public const string BackToStart = "回到起步页"`
  - `public const string RestartPane = "重新启动"`
  - `public const string CheckingCapacity = "正在检查"`
  - `public const string UnknownCapacity = "暂不清楚"`
  - `public static bool CanSelect(int alreadySelected, bool ready)`
  - `public static string LaunchButtonText(int readyCount, bool starting)`
  - `public static string DisabledReason(bool hasProject, int readySelectedCount)`
  - `public static string SelectionLine(int selected)`
  - `public static string CapacityLine(int? remaining)`
  - `public static string PartialLaunchLine(int running, int missing, string reason)`
- `LaunchSurface.LaunchButtonText(int count, IReadOnlyList<string?> hostNames, bool busy, bool previewOnly)` 仍保留参数以免到处改调用点，但按钮正文不再含地点、也不再因 `previewOnly` 写成「检查资源并启动」：`busy` 时返回 `StartPageSurface.StartingButton`，否则返回 `StartPageSurface.LaunchButtonText(count, false)`。`TargetPhrase` / `TargetTooltip` 不变，地点改由状态行使用。

- [ ] **Step 1: 写失败的文案测试**

创建 `tests/AgentLauncher.Tests/StartPageSurfaceChecks.cs`：

```csharp
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
            Assert(StartPageSurface.CapDenied == "最多同时 6 个", "copy");
        });

        Test("disabled and capacity lines stay factual", () =>
        {
            Assert(StartPageSurface.DisabledReason(false, 0) == "还没有选项目", "project");
            Assert(StartPageSurface.DisabledReason(true, 0) == "还没有选 Agent", "agents");
            Assert(StartPageSurface.DisabledReason(true, 2) == "", "ready");
            Assert(StartPageSurface.SelectionLine(3) == "已选 3 / 最多 6", "selection");
            Assert(StartPageSurface.CapacityLine(2) == "按当前测量，大约还可再开 2 个", "remaining");
            Assert(StartPageSurface.CapacityLine(null) == "暂不清楚", "unknown");
            Assert(StartPageSurface.CheckingCapacity == "正在检查", "checking");
            Assert(StartPageSurface.PartialLaunchLine(4, 2, "内存不足") == "当前运行 4 / 最多 6，还差 2，因为内存不足", "partial");
        });
    }
}
```

把 `tests/AgentLauncher.Tests/LaunchSurfaceChecks.cs` 里三处按钮文案断言改成新文案（地点短语检查改用 `TargetPhrase` / `TargetTooltip`，不要再要求按钮里出现「本机」「京云」「检查资源并启动」「正在处理…」）：

```csharp
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
```

其他 `SyncHonesty` 等测试保持不变。在 `Program.cs` 注册 `StartPageSurfaceChecks.Run(Test);`。

- [ ] **Step 2: 运行测试，确认失败**

Run:

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -- --launch-surface-checks
```

Expected: 第一条因 `StartPageSurface` 不存在失败或编译失败；第二条 `--launch-surface-checks` 中 UX15 按钮文案失败（仍是「启动 2 个 · 本机」或「检查资源并启动 1 个 · 京云」）。

- [ ] **Step 3: 写最小实现**

创建 `src/AgentLauncher.Core/StartPageSurface.cs`：

```csharp
namespace AgentLauncher.Core;

public static class StartPageSurface
{
    public const string CapDenied = "最多同时 6 个";
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
```

把 `LaunchSurface.LaunchButtonText` 改成：

```csharp
public static string LaunchButtonText(int count, IReadOnlyList<string?> hostNames, bool busy, bool previewOnly)
{
    _ = hostNames;
    _ = previewOnly;
    return StartPageSurface.LaunchButtonText(count, busy);
}
```

- [ ] **Step 4: 再跑测试，确认通过**

Run:

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -- --launch-surface-checks
```

Expected: 两条命令失败数都为 0；能看到 `PASS start page button is 启动 N 个 or 正在启动…` 和更新后的 UX15 文案检查。

- [ ] **Step 5: Commit**

```powershell
git add src/AgentLauncher.Core/StartPageSurface.cs src/AgentLauncher.Core/LaunchSurface.cs tests/AgentLauncher.Tests/StartPageSurfaceChecks.cs tests/AgentLauncher.Tests/LaunchSurfaceChecks.cs tests/AgentLauncher.Tests/Program.cs
git commit -m "feat: use a single start-page launch label and factual capacity copy."
```

---

### Task 3: 主题色值与默认深色

**Files:**
- Create: `src/AgentLauncher.Core/ThemeTokens.cs`
- Create: `tests/AgentLauncher.Tests/ThemeTokensChecks.cs`
- Modify: `tests/AgentLauncher.Tests/Program.cs`
- Modify: `src/AgentLauncher.Core/Models.cs`（`public string Theme { get; set; } = "system";` 改为 `= "dark";`，约第 91 行）
- Modify: `src/AgentLauncher.App/App.xaml`（默认 brush 与圆角）
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs` 中的 `ApplyTheme()`（约第 1268–1289 行）
- Modify: `src/AgentLauncher.App/UiChecks.cs` 中写死旧色值的断言（约第 267–268 行：深色 Ink `#ECEFF5`、Page `#14161C`，浅色 Ink `#1F2430`、Page `#F4F6FB`）

**Interfaces:**
- Consumes: 无。
- Produces:
  - `public sealed record ThemePalette(string Page, string Surface, string Header, string Line, string Ink, string Muted, string Accent, string StatusOk, string StatusWait, string StatusBad)`
  - `public static class ThemeTokens`，`public static ThemePalette Dark`，`public static ThemePalette Light`
  - 深色必须正好是规格值：Page `#0E1116`，Surface `#161B22`，Header `#1C2128`，Line `#2B3139`，Ink `#E6EDF3`，Muted `#8B949E`，Accent `#2F81F7`，StatusOk `#3FB950`，StatusWait `#D29922`，StatusBad `#F85149`
  - 浅色必须正好是：Page `#F6F8FA`，Surface `#FFFFFF`，Header `#F0F2F4`，Line `#D0D7DE`，Ink `#1F2328`，Muted `#656D76`，强调色与状态色与深色相同
- `ApplyTheme()` 从 `ThemeTokens` 取色写入已有资源键：`Page`、`Surface`、`Ink`、`Muted`、`Line`、`Accent`、`StatusOk`、`StatusBad`；新增资源键 `HeaderSurface`、`StatusWait`。高对比分支继续跟系统色，并覆盖这两个新键。圆角：卡片 12、按钮 8、窗格 10。

- [ ] **Step 1: 写失败的色值与默认主题测试**

```csharp
using AgentLauncher.Core;

namespace AgentLauncher.Tests;

internal static class ThemeTokensChecks
{
    public static void Run(Action<string, Action> Test)
    {
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }

        Test("dark and light palettes match the approved spec", () =>
        {
            var dark = ThemeTokens.Dark;
            Assert(dark.Page == "#0E1116" && dark.Surface == "#161B22" && dark.Header == "#1C2128", "dark surfaces");
            Assert(dark.Line == "#2B3139" && dark.Ink == "#E6EDF3" && dark.Muted == "#8B949E", "dark text");
            Assert(dark.Accent == "#2F81F7" && dark.StatusOk == "#3FB950" && dark.StatusWait == "#D29922" && dark.StatusBad == "#F85149", "dark accents");
            var light = ThemeTokens.Light;
            Assert(light.Page == "#F6F8FA" && light.Surface == "#FFFFFF" && light.Header == "#F0F2F4", "light surfaces");
            Assert(light.Line == "#D0D7DE" && light.Ink == "#1F2328" && light.Muted == "#656D76", "light text");
            Assert(light.Accent == dark.Accent && light.StatusOk == dark.StatusOk && light.StatusWait == dark.StatusWait && light.StatusBad == dark.StatusBad, "shared accents");
        });

        Test("new configurations default to dark rather than system", () =>
        {
            Assert(new Configuration().Theme == "dark", "default theme");
        });
    }
}
```

注册 `ThemeTokensChecks.Run(Test);`。

- [ ] **Step 2: 运行测试，确认失败**

Run:

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

Expected: 因 `ThemeTokens` 不存在失败，或默认主题仍是 `"system"`。

- [ ] **Step 3: 实现色值、默认主题和 ApplyTheme**

创建 `ThemeTokens.cs`：

```csharp
namespace AgentLauncher.Core;

public sealed record ThemePalette(
    string Page,
    string Surface,
    string Header,
    string Line,
    string Ink,
    string Muted,
    string Accent,
    string StatusOk,
    string StatusWait,
    string StatusBad);

public static class ThemeTokens
{
    public static ThemePalette Dark { get; } = new(
        "#0E1116", "#161B22", "#1C2128", "#2B3139", "#E6EDF3", "#8B949E",
        "#2F81F7", "#3FB950", "#D29922", "#F85149");

    public static ThemePalette Light { get; } = new(
        "#F6F8FA", "#FFFFFF", "#F0F2F4", "#D0D7DE", "#1F2328", "#656D76",
        Dark.Accent, Dark.StatusOk, Dark.StatusWait, Dark.StatusBad);
}
```

把 `Models.cs` 里 `Theme` 默认值改为 `"dark"`。

在 `App.xaml` 把默认 `Page` / `Surface` / `Ink` / `Muted` / `Line` / `Accent` / `StatusOk` / `StatusBad` 改成浅色启动瞬间也能对上 `ThemeTokens.Light` 的值（应用随后会按配置再 `ApplyTheme`），并增加：

```xml
<SolidColorBrush x:Key="HeaderSurface" Color="#F0F2F4"/>
<SolidColorBrush x:Key="StatusWait" Color="#D29922"/>
```

把 `Card` 的 `CornerRadius` 设为 `12`；`PrimaryButton` / `QuietButton` 的 `CornerRadius` 设为 `8`（若按钮样式目前用模板，改模板里的 Border 圆角，不要另起一套无视现有控件模板的样式）。

重写 `ApplyTheme()` 的配色循环，使其在非高对比时：

```csharp
var palette = dark ? ThemeTokens.Dark : ThemeTokens.Light;
void Set(string key, string color) =>
    resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
Set("Page", palette.Page);
Set("Surface", palette.Surface);
Set("HeaderSurface", palette.Header);
Set("Line", palette.Line);
Set("Ink", palette.Ink);
Set("Muted", palette.Muted);
Set("Accent", palette.Accent);
Set("StatusOk", palette.StatusOk);
Set("StatusWait", palette.StatusWait);
Set("StatusBad", palette.StatusBad);
Set("ErrorInk", palette.StatusBad);
Set("WarningInk", palette.StatusWait);
```

其余键（`Raised`、`Selected`、`InputLine`、`WarningSurface`、`AccentInk`、`AccentSoft`、`AccentPressed`、`RailSurface`、预览色、Agent 身份色、阴影）可以保留现有明暗配对，但 `Page` / `Surface` / `Ink` / `Muted` / `Line` / `Accent` / 状态色必须来自 `ThemeTokens`。高对比分支给 `HeaderSurface` 和 `StatusWait` 分别赋 `SystemColors.WindowBrush` 与 `SystemColors.WindowTextBrush`。

把 `UiChecks.cs` 里主题断言改成与 `ThemeTokens` 比较，例如：

```csharp
Check("theme resources switch", ThemeTokens.Dark.Ink == ToHex(darkInk) && ThemeTokens.Light.Ink == ToHex(lightInk));
Check("window background follows theme", ThemeTokens.Dark.Page == ToHex(darkPage) && ThemeTokens.Light.Page == ToHex(lightPage));
```

若文件里没有 `ToHex`，就地用 `color.ToString()` 不可靠（WPF 的 `Color.ToString()` 带 `#AARRGGBB`）。写一个局部函数：

```csharp
static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
```

若还有测试断言默认 `Theme=="system"`，改成 `"dark"`；已保存配置加载路径不要改写用户已经存盘的值。

- [ ] **Step 4: 先跑模型测试**

Run:

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

Expected: 失败数为 0，含 `PASS dark and light palettes match the approved spec` 和 `PASS new configurations default to dark rather than system`。

本任务不必跑完整 UI 套件；颜色窗口断言在 Task 4 之后随新主表面一起跑。但要编译 App：

```powershell
dotnet build src/AgentLauncher.App/AgentLauncher.App.csproj -c Release
```

Expected: 0 Warning 可以保留已有警告，但 0 Error。

- [ ] **Step 5: Commit**

```powershell
git add src/AgentLauncher.Core/ThemeTokens.cs src/AgentLauncher.Core/Models.cs src/AgentLauncher.App/App.xaml src/AgentLauncher.App/MainWindow.xaml.cs src/AgentLauncher.App/UiChecks.cs tests/AgentLauncher.Tests/ThemeTokensChecks.cs tests/AgentLauncher.Tests/Program.cs
git commit -m "feat: apply approved dark default and shared theme tokens."
```

---

### Task 4: 起步页主表面

**Files:**
- Modify: `src/AgentLauncher.App/MainWindow.xaml`
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs`（`UpdateLaunch` / 勾选逻辑）
- Modify: `src/AgentLauncher.App/MainWindow.Simple.cs`
- Create: `src/AgentLauncher.App/WorkbenchUiChecks.cs`
- Modify: `src/AgentLauncher.App/UiChecks.cs`（在现有 `RunUiChecksAsync` 流程中调用新检查，约在 `RunSimpleUiChecksAsync` 附近）
- Modify: 任何仍然要求起步页显示 `PreviewCard`、`RefreshLaunchButton` 可见、或主按钮为「检查资源并启动」的检查

**Interfaces:**
- Consumes: `StartPageSurface.*`、`WorkbenchLayout.MaxPanes`、`MainWindowMode`（本任务窗口仍停留在 `StartPage`，先不切工作台）
- Produces: 起步页可见控件约定：
  - `StartPagePanel`（起步页根）
  - `AgentChipPanel`（可勾选卡片容器）
  - `LaunchButton`（AutomationId 仍为 `Launch`）
  - `LaunchHint`（状态行，可含选择数与容量/禁用原因）
  - `RefreshLaunchButton` 从主表面移除或 `Collapsed`（检查改到设置里如需保留）
  - `PreviewCard`、`Preview`、`Rail` 在起步页为 `Collapsed`
  - `LaunchOptionsExpander`、`ConfigurationToolsExpander`、`SessionExpander` 在起步页为 `Collapsed` 且不再占据主表面

- [ ] **Step 1: 先写失败的窗口检查**

创建 `src/AgentLauncher.App/WorkbenchUiChecks.cs`，用与 `SimpleUiChecks.cs` 相同的 `#if INTEGRATION_TESTS` 和 `partial class MainWindow` 模式：

```csharp
#if INTEGRATION_TESTS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentLauncher.Core;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunWorkbenchUiChecksAsync(Action<string, bool> check)
    {
        await saveTask;
        await commandCheckTask;
        var previous = config;
        var previousStatus = status;
        var previousReadOnly = readOnly;
        var previousTimer = timer.IsEnabled;
        timer.Stop();
        try
        {
            windowMode = MainWindowMode.StartPage;
            config = new()
            {
                Theme = "dark",
                Draft = new()
                {
                    Project = Store.Root,
                    Slots =
                    [
                        new() { AgentId = "omp", Enabled = true },
                        new() { AgentId = "qoder", Enabled = true }
                    ]
                }
            };
            var sample = new MemorySnapshot(16 * ResourcePolicy.GiB, 12 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB, 24 * ResourcePolicy.GiB, 5, DateTimeOffset.UtcNow);
            status = new(true, "workbench-ui", [], sample);
            readOnly = false;
            ApplyWindowMode();
            ApplyTheme();
            Render();
            await commandCheckTask;
            UpdateLaunch();
            check("start page hides rail preview and extra launch actions",
                StartPagePanel.Visibility == Visibility.Visible
                && Rail.Visibility == Visibility.Collapsed
                && PreviewCard.Visibility == Visibility.Collapsed
                && RefreshLaunchButton.Visibility == Visibility.Collapsed
                && (LaunchOptionsExpander.Visibility == Visibility.Collapsed || !LaunchOptionsExpander.IsVisible));
            check("start page has a single launch button with the approved label",
                LaunchButton.IsVisible
                && Equals(LaunchButton.Content, StartPageSurface.LaunchButtonText(CountReadySelected(), false))
                && !LaunchHint.Text.Contains("预览不是")
                && !LaunchHint.Text.Contains("启动授权"));
            check("agent chips live on the start page", AgentChipPanel.IsVisible && ReviewFind<ToggleButton>(AgentChipPanel).Any());

            var selected = CountReadySelected();
            TrySelectOverCapReadyAgent();
            check("ninth ready agent stays unselected and explains the cap",
                CountReadySelected() == Math.Min(selected, WorkbenchLayout.MaxPanes)
                && (CountReadySelected() < WorkbenchLayout.MaxPanes || LaunchHint.Text.Contains(StartPageSurface.CapDenied)));
        }
        finally
        {
            config = previous;
            status = previousStatus;
            readOnly = previousReadOnly;
            if (previousTimer) timer.Start();
            ApplyWindowMode();
            ApplyTheme();
            Render();
        }
    }
}
#endif
```

在同一文件或 `MainWindow.Workbench.cs` 里先加会编译但不满足断言的占位：`windowMode` 字段、`ApplyWindowMode()`（暂时什么都不做）、`CountReadySelected()`（可用现有已选且命令就绪的计数，若还没有就返回 `config.Draft.Slots.Count(s => s.Enabled)`）、`TrySelectOverCapReadyAgent()`（尝试再勾选一个已就绪槽；没有第 7 个时检查应视为通过 cap 逻辑而不是虚构槽位——若当前测试配置只有 2 个槽，另写一个只测 `StartPageSurface.CanSelect` 已在 Task 2 覆盖的说明不够。窗口检查里要真正造出 6 个已就绪槽再点第 7 个）。把测试配置扩成 7 个已启用槽（可重复 AgentId，但 Id 不同），再调用勾选逻辑。

在 `UiChecks.cs` 的 `RunUiChecksAsync` 中调用 `await RunWorkbenchUiChecksAsync(Check);`。

先跑 UI 检查（会失败）。不要发明新的 UI 入口，使用现有脚本（它会先发布带 `INTEGRATION_TESTS` 的运行时，再启动 `artifacts/test-runtime/AgentLauncher.App.exe --ui-check artifacts/ui`，然后跑 `--embedded-check artifacts/embedded-ui.json`）：

```powershell
powershell -NoProfile -File scripts/test.ps1 -SkipBrowser
```

Expected: 因缺少 `StartPagePanel` / `AgentChipPanel` 或预览仍可见而失败。模型测试若已通过，失败应出在 UI / embedded 检查。

- [ ] **Step 2: 改 XAML 与渲染，使起步页符合规格**

在 `MainWindow.xaml` 用 `StartPagePanel` 包住起步页内容（顶栏、项目短名、Agent 卡片容器 `AgentChipPanel`、底部 `LaunchHint` + 唯一 `LaunchButton`）。

- `Rail`、`PreviewCard`、`Preview` 默认 `Visibility="Collapsed"`。
- 三个折叠区移出主表面：放到设置或「更多」已经打开的窗口/菜单中；起步页不要再渲染它们。如果现有代码仍按名字引用这些控件，保留控件但 `Visibility="Collapsed"`，并在设置页需要时再显示，避免编译中断。
- 删除或折叠 `RefreshLaunchButton`。不要在主按钮旁再放「等待资源后启动」。
- Agent 卡片由代码在 `AgentChipPanel`（`WrapPanel`）里生成：显示短名、本机/远程、就绪或未就绪。未就绪的卡片可见但 `IsEnabled=false`，并带原因。勾选走现有 `slot.Enabled` 或与当前勾选模型等价的字段；在写入前调用 `StartPageSurface.CanSelect`。拒绝第 7 个时把 `StartPageSurface.CapDenied` 写到 `LaunchHint`。
- `UpdateLaunch()` 设置 `LaunchButton.Content = StartPageSurface.LaunchButtonText(readyCount, busy || checkingLaunch || launchCancellation!=null)`。`readyCount==0` 或没有项目时禁用按钮，并用 `StartPageSurface.DisabledReason` 和 `SelectionLine`、`CapacityLine` 组成 `LaunchHint`。容量数字必须来自已有 `status.Memory` / 准入结果：算不出就用 `CheckingCapacity` 或 `CapacityLine(null)`，禁止把空值显示成 0，也禁止在没有测量时说一定能开满 6 个。
- 项目标题继续用短名；完整路径只放 `ToolTip`。可复用已有 `MiddleEllipsis`。
- `ApplyWindowMode()`：`windowMode==StartPage` 时 `StartPagePanel.Visible`、工作台面板 `Collapsed`（若 Task 5 前工作台面板还不存在，先在 XAML 放一个 `WorkbenchPanel` `Collapsed` 空壳）。

`MainWindow.Simple.cs` 里 `UpdateSimpleStatus` 不要再要求 `RefreshLaunchButton` 在起步页可见，也不要把长任务/启动选项文案写回起步页折叠区标题。长任务入口改到设置后，本任务只要保证起步页不再依赖这些控件可见；不要在本任务修改 `ResourcePolicy` 或 Job 上限。

- [ ] **Step 3: 再跑模型测试和 UI 检查**

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

然后再跑 `powershell -NoProfile -File scripts/test.ps1 -SkipBrowser`。把失败的旧检查改到新结构：凡是断言「起步页有分屏预览 / 轨道列表 / 检查资源并启动 / 预览不是授权 / 折叠区默认出现在主表面」的，改为断言它们不在起步页主表面，并确认设置或「更多」仍能到达工作目录、资源设置、远程主机、恢复与历史。不要为了过检查把预览加回来。`SimpleUiChecks` 里 `ACC-U04 advanced configuration starts collapsed` 等项，改为「起步页看不到这些折叠区，设置里可以打开」。

Expected: 模型测试 0 失败；本任务相关的新窗口检查通过；被更新的旧检查通过。

- [ ] **Step 4: Commit**

```powershell
git add src/AgentLauncher.App tests/AgentLauncher.Tests
git commit -m "feat: replace the start page with a single project, chip, and launch surface."
```

---

### Task 5: 启动后进入真实窗格工作台

**Files:**
- Create: `src/AgentLauncher.App/MainWindow.Workbench.cs`（若 Task 4 已创建则修改）
- Modify: `src/AgentLauncher.App/MainWindow.xaml`（`WorkbenchPanel`、`WorkbenchTopBar`、`WorkbenchHost`）
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs` 的 `LaunchClick`
- Modify: `src/AgentLauncher.App/MainWindow.Embedded.cs`
- Modify: `src/AgentLauncher.App/WorkbenchUiChecks.cs`

**Interfaces:**
- Consumes: `WorkbenchLayout.AfterLaunchAttempt`、`Layout`、`ShowBackToStart`、已有 `TryShowEmbeddedAsync` / `TerminalCard`
- Produces:
  - `WorkbenchPanel`、顶栏显示项目短名、`运行中数 / 最多 6`、资源一句、`AddPaneButton`、设置、条件显示的 `BackToStartButton`（内容为 `StartPageSurface.BackToStart`）
  - `WorkbenchHost` 为可滚动容器，按 `WorkbenchLayout.Layout(host.ActualWidth, host.ActualHeight, paneCount)` 放置格子
  - `ApplyWindowMode()` 在 `Workbench` 时隐藏 `StartPagePanel`、显示 `WorkbenchPanel`
  - 启动成功（`startedCount >= 1`）后 `windowMode = WorkbenchLayout.AfterLaunchAttempt(startedCount)`，即使其余 Agent 仍在启动
  - 一个都没启动则留在起步页，按钮恢复为「启动 N 个」，并显示原因
  - 部分启动时顶栏或提示使用 `StartPageSurface.PartialLaunchLine`

- [ ] **Step 1: 写失败的工作台检查**

在 `RunWorkbenchUiChecksAsync` 增补（可用现有假 `ControlEntry` / 假 host，不要启动用户本机正在用的 Agent）：

```csharp
check("launching at least one agent switches the window into the workbench", startedCount >= 1 && windowMode == MainWindowMode.Workbench && WorkbenchPanel.Visibility == Visibility.Visible && StartPagePanel.Visibility == Visibility.Collapsed);
check("workbench hosts real terminal cards rather than the static preview", ReviewFind<TerminalCard>(WorkbenchHost).Count == startedCount && PreviewCard.Visibility != Visibility.Visible);
```

先构造 `startedCount = 2` 的已启动条目，调用即将实现的 `EnterWorkbench(entries)`，不要走会拉起真实外部 Terminal 的旧路径。若当前 `LaunchClick` 仍只开外部 Terminal，本任务要把它的「启动后界面」改成嵌入工作台；外部 Terminal 若仍保留，只能作为设置中的次要出口，不能在默认启动后抢主界面。

Run 同一套 UI 检查。Expected: 因还不能切到工作台或仍显示预览而失败。

- [ ] **Step 2: 实现切换与网格承载**

`MainWindow.Workbench.cs` 核心逻辑：

```csharp
private MainWindowMode windowMode = MainWindowMode.StartPage;
private readonly List<WorkbenchPaneView> workbenchPanes = new();

private void ApplyWindowMode()
{
    var workbench = windowMode == MainWindowMode.Workbench;
    StartPagePanel.Visibility = workbench ? Visibility.Collapsed : Visibility.Visible;
    WorkbenchPanel.Visibility = workbench ? Visibility.Visible : Visibility.Collapsed;
    if (workbench) RelayoutWorkbench();
}

private void EnterWorkbench(IReadOnlyList<LaunchItem> started)
{
    windowMode = WorkbenchLayout.AfterLaunchAttempt(started.Count);
    ApplyWindowMode();
    if (windowMode != MainWindowMode.Workbench) return;
    foreach (var plan in started) AttachWorkbenchPane(plan);
    RelayoutWorkbench();
}

private void RelayoutWorkbench()
{
    var width = Math.Max(WorkbenchHost.ActualWidth, 1);
    var height = Math.Max(WorkbenchHost.ActualHeight, 1);
    var layout = WorkbenchLayout.Layout(width, height, workbenchPanes.Count);
    WorkbenchHost.Height = layout.SingleColumn
        ? layout.Tiles.Count == 0 ? height : layout.Tiles[^1].Y + layout.Tiles[^1].Height
        : double.NaN;
    for (var i = 0; i < workbenchPanes.Count; i++)
    {
        var tile = layout.Tiles[i];
        var view = workbenchPanes[i];
        Canvas.SetLeft(view.Root, tile.X);
        Canvas.SetTop(view.Root, tile.Y);
        view.Root.Width = tile.Width;
        view.Root.Height = tile.Height;
    }
}
```

`WorkbenchHost` 用 `Canvas` 放在 `ScrollViewer` 里。窗口 `SizeChanged` 时调用 `RelayoutWorkbench()`。

改造 `TryShowEmbeddedAsync`：成功后不要只往 `EmbeddedTabs` 加标签并显示侧栏按钮；把 `TerminalCard` 放进对应工作台格子。可以暂时保留 `EmbeddedTabs` 但默认隐藏。`ShowTerminalsButton` 不再作为主路径。

`LaunchClick` 在现有准入和启动流程之后：按实际开始启动的数量调用 `EnterWorkbench`。资源检查仍在这一次点击里自动做。正在启动时按钮文案为 `StartPageSurface.StartingButton`。不要在这里改阈值。若策略只允许部分启动，进入工作台并显示 `PartialLaunchLine`。

从起步页切到工作台可用 200–250ms 的 `DoubleAnimation` 做透明度淡入，不要加更花的动画。

- [ ] **Step 3: 跑 UI 检查并修失败项**

再跑 `powershell -NoProfile -File scripts/test.ps1 -SkipBrowser`。Expected: 新的切换/真实窗格检查通过；嵌入相关旧检查（`EmbeddedUiChecks`）若仍找 `EmbeddedTabs`，改为在工作台格子上查找 `TerminalCard`，并保持「能从记录重新打开 / 重新连接 host」的行为断言。

- [ ] **Step 4: Commit**

```powershell
git add src/AgentLauncher.App
git commit -m "feat: switch the main window into a live embedded workbench after launch."
```

---

### Task 6: 窗格铬条、停止后留格、加窗格和返回起步页

**Files:**
- Modify: `src/AgentLauncher.App/MainWindow.Workbench.cs`
- Modify: `src/AgentLauncher.App/MainWindow.xaml`
- Modify: `src/AgentLauncher.App/WorkbenchUiChecks.cs`
- Modify: `src/AgentLauncher.App/TerminalCard.cs`（只在必须把标题条和卡片组合在一起时改；能在外层包一层 Border 就不要改终端绘制）

**Interfaces:**
- Consumes: `WorkbenchPaneStatus`、`StartPageSurface.RestartPane`、`StartPageSurface.BackToStart`、`StartPageSurface.CapDenied`、`WorkbenchLayout.MaxPanes`
- Produces: 每格标题条约 28–32px，含序号、短名、本机/远程、状态点、菜单、停止。状态点颜色：`Starting`/`Waiting`/`Pressure` 用 `StatusWait`，`Running` 用 `StatusOk`，`Exited` 用 `StatusBad`。命令与完整路径进菜单。停止后格子留在网格，显示退出原因和「重新启动」。菜单「关闭格子」移出网格并 `RelayoutWorkbench()`。全部结束后不自动回到起步页，显示 `BackToStartButton`。`AddPaneButton` 在未满 6 格时打开短列表并新增启动；满 6 格（含已退出未关闭的格子）时禁用并提示已满。已退出的格子用「重新启动」，不另开重复格。双击标题铺满；再双击或「回到网格」恢复。焦点窗格用 `Accent` 细描边。

- [ ] **Step 1: 写失败检查**

覆盖：停止一格后 `windowMode` 仍是 `Workbench` 且该格有「重新启动」；全部结束后出现「回到起步页」且模式不变；按下后回到起步页；已有 6 格时 `AddPaneButton.IsEnabled==false`；双击铺满后 `WorkbenchHost` 只显示一格，再双击恢复。用假状态，不要杀真实进程；停止测试可以只走 UI 状态机（把该格标为 `Exited`）再另测真正的停止命令接线。

- [ ] **Step 2: 实现铬条与状态机**

为每个 `WorkbenchPaneView` 保存 `Guid Id`、`LaunchItem Plan`、`WorkbenchPaneStatus Status`、`Border Root`、`TerminalCard Card`、标题控件和停止/菜单按钮。`runningOrStartingCount` 计 `Starting` 与 `Running`（以及 `Waiting`/`Pressure`）。`ShowBackToStart` 为真时显示返回按钮，点击：

```csharp
windowMode = MainWindowMode.StartPage;
ApplyWindowMode();
```

已结束会话仍写入现有运行记录，不要丢记录。最大化用字段 `maximizedPaneId`：`RelayoutWorkbench()` 在它非空时只给该格铺满 `WorkbenchHost` 视口。

顶栏「+」列出当前项目可启动且不在网格中运行的 Agent；选择后走与 `LaunchClick` 相同的准入（阈值不变）。若不能启动，留在工作台并说明原因。

- [ ] **Step 3: 跑 UI 检查，确认通过**

Expected: 本任务新增检查通过；全部退出不会自行回到起步页。

- [ ] **Step 4: Commit**

```powershell
git add src/AgentLauncher.App
git commit -m "feat: keep exited workbench panes and add restart, layout, and return actions."
```

---

### Task 7: 键盘、窄屏，以及文档收口

**Files:**
- Modify: `src/AgentLauncher.App/MainWindow.Workbench.cs`
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs` 或 `MainWindow.QuickActions.cs`（现有 `Ctrl+Enter` 启动）
- Modify: `src/AgentLauncher.App/WorkbenchUiChecks.cs`
- Modify: `docs/UI-DESIGN.md`（文首增加指向新规格与本计划的说明，不删除历史章节，只标明主路径已替换）

**Interfaces:**
- Consumes: 前序全部公开类型
- Produces: 起步页 Tab 顺序为项目 → Agent 卡片 → 主按钮 → 设置；空格切换当前卡片勾选（在 `CanSelect` 允许时）；Enter 在主按钮可用时启动。工作台 `Ctrl+Tab` / `Ctrl+Shift+Tab` 轮换焦点窗格，焦点格有 Accent 描边。`Ctrl+K` 可保留，但不是完成主路径的必要条件。窄宽（`UseSingleColumn` 为真）时工作台单列滚动，标题和按钮仍可点。

- [ ] **Step 1: 写失败检查**

用 `ResizeForCheckAsync` 把窗口缩到宽度 `< 420 * 2 + 8`，放 3 个假格子，断言 `WorkbenchLayout.Layout` 为单列且 `ScrollViewer` 可滚动、停止按钮 `IsVisible`。再测 `Ctrl+Tab` 把焦点从格 0 移到格 1。起步页测空格勾选与第 7 个拒绝（若 Task 4 已覆盖则不要重复造冲突断言）。

- [ ] **Step 2: 实现键盘与窄屏**

在窗口 `PreviewKeyDown` 中：`windowMode==Workbench` 且 `Ctrl+Tab` 时处理并 `e.Handled=true`，不要让 WPF 把焦点送到隐藏的起步页。窄屏依赖 Task 5 的 `RelayoutWorkbench()`；确认 `ScrollViewer` 在 `SingleColumn` 且内容高度超过视口时可以滚动。

在 `docs/UI-DESIGN.md` 文首加 5–10 行：主窗口主路径以 `docs/superpowers/specs/2026-11-27-workspace-ui-redesign.md` 为准；旧的轨道 + 预览描述不再作为验收依据。

- [ ] **Step 3: 运行验收命令**

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -- --launch-surface-checks
powershell -NoProfile -File scripts/test.ps1 -SkipBrowser
```

Expected: 模型测试和 launch-surface 检查失败数为 0；`test.ps1 -SkipBrowser` 中的原生/模型/集成/UI 检查通过。不要跑会启动真实 6 Agent 或改机器全局设置的套件。若个别与本设计无关的远程 live 检查未在 `-SkipBrowser` 路径里，不要为了「全绿」去放宽阈值或跳过失败而不记载。

对照规格第 8 节逐条勾验收：无预览、无三套重复、无并排两颗启动按钮、1/2/4/6 真实窗格、第 7 个拒绝、部分启动说明、退出留格、全部结束不自动返回、窄屏单列、默认深色与浅色可切换。缺哪条就补检查，不要只在文档里声称完成。

- [ ] **Step 4: Commit**

```powershell
git add src/AgentLauncher.App docs/UI-DESIGN.md
git commit -m "feat: add workbench keyboard focus, narrow stacking, and design-doc pointers."
```

不要 push、不要合并到 `main`、不要改 `D:\Users\pj\agent-launcher` 的未提交工作区。完成后把分支名、关键提交和 `test.ps1 -SkipBrowser` 结果交给用户审阅。

---

## 自审

- 规格第 1–2 节（目标、约束、两种状态、不自动返回）→ Task 1、5、6。
- 规格第 3 节（起步页、一颗按钮、容量与部分启动、去掉预览和折叠区）→ Task 2、4、5。
- 规格第 4 节（工作台顶栏、网格、铬条、加窗格、最大化）→ Task 1、5、6。
- 规格第 5 节（色值、圆角、默认深色、短淡入）→ Task 3、5。
- 规格第 6 节（键盘与焦点、旧检查改新结构）→ Task 4、7。
- 规格第 7 节（不改阈值、诚实容量、次要能力搬家、干净工作树）→ 全局约束与各任务。
- 规格第 8 节验收 → Task 7 Step 3。
- 没有 TBD；`LaunchSurface.LaunchButtonText` 的新语义在 Task 2 与检查中一致；`PaneLayout` 不被复用或改动。
