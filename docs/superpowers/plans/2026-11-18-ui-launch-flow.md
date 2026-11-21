# 启动主路径分组 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development while implementing. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把日常主路径收成「一个项目入口、运行位置和 Agent 分开、运行状态在预览前、挂接与释放名额分开、底栏主次清楚、同步文案不夸大」。

**Architecture:** 文案与目标短语放在 `AgentLauncher.Core.LaunchSurface`，WPF 只负责摆放现有控件。不改 reserve/begin、8 名额、16 槽位、远程不进本机内存/CPU 准入。Mutagen 模式只引用创建常量 `MutagenCreateMode.SyncMode`，不扫描 `mutagen sync list` 全文。

**Tech Stack:** C# 12、WPF、zh-Hans-CN、现有 Fluent 资源。无新 NuGet。

## Global Constraints

- `MaxConcurrentAgents = 8`，含所有非终态，包括 `Disconnected` 与 `Reattaching`。`MaxSlots = 16`。
- 纯远程批次不进入本机内存/CPU 准入。一个本机槽位的资源拒绝不得扩成禁止全部批次。
- 重新挂接二次确认：不释放名额、不自动重连、打开的是新 SSH 窗格。
- 释放名额二次确认：不会结束远端进程、不会重新挂接。本机「核对并释放占用」不是远程释放。
- `None` 写明「这次不是同权」；按 2026-11-19 的 §92 更晚决定，保存与启动会被拒收。
- 现有新建会话是 `two-way-resolved`，界面不声称已收敛。终态「已同步」只表示那次 flush。
- 查看改动只是本机 git 状态，不能证明三台同权。
- 不新增自动 SSH 重连、空闲关闭确认、通知、终端召回、PR9 同步状态机、Grok 配置，不重建桌面预览。
- 共用目录风险在折叠区外面。高级折叠区保持默认折叠，不把原有折叠写成新能力。
- 云端 SSH 终端可以实时输入输出；文件同步不是实时一致。
- 队列按钮与「按资源依次启动 / 等待资源后启动」行为保留。

---

### Task 1: 文案与按钮目标

**Files:**
- Create: `src/AgentLauncher.Core/LaunchSurface.cs`
- Create: `tests/AgentLauncher.Tests/LaunchSurfaceChecks.cs`
- Modify: `tests/AgentLauncher.Tests/Program.cs`（`--launch-surface-checks` 在原生套件之前返回）
- Modify: `src/AgentLauncher.Windows/WorkspaceSync.cs`（create 参数改用 `MutagenCreateMode.SyncMode`）
- Modify: `src/AgentLauncher.Windows/WorkspaceInsights.cs`（终态徽章不再写已收敛）

- [ ] 先写 `LaunchSurfaceChecks`，跑 `--launch-surface-checks`，确认因类型不存在而失败。
- [ ] 再实现 `LaunchSurface`，同一命令转为通过。

覆盖：单一本机、单一主机、两个不同主机必须是「多个目标」且按钮文本不含其中一台的名字；`None` 含「这次不是同权」和「拒收」且不含「仍可启动」；Mutagen 文案含创建常量且含「不是已收敛」、不含「全部已收敛」；终态 flush 文案含 `sync-final` 且不含「已收敛」。

### Task 2: 主卡、状态、底栏

**Files:**
- Modify: `src/AgentLauncher.App/MainWindow.xaml`
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs`
- Modify: `src/AgentLauncher.App/MainWindow.Practical.cs`
- Modify: `src/AgentLauncher.App/MainWindow.QuickActions.cs`
- Modify: `src/AgentLauncher.App/MainWindow.QuickQueue.cs`
- Modify: `src/AgentLauncher.App/MainWindow.RemoteReattach.cs`
- Modify: `src/AgentLauncher.App/MainWindow.RemoteRelease.cs`
- Modify: `src/AgentLauncher.App/MainWindow.Polling.cs`
- Modify: `src/AgentLauncher.App/UiChecks.cs`
- Modify: `src/AgentLauncher.App/UiReviewChecks.cs`
- Modify: `src/AgentLauncher.App/SimpleUiChecks.cs`
- Modify: `src/AgentLauncher.App/QuickActionsUiChecks.cs`

行为：

- 可见的项目变更只留顶栏「选择项目」。侧栏路径框、更换、最近项目下拉保持在树上供既有输入测试，但 `Visibility=Collapsed`。
- 运行位置下拉来自 `RemoteHosts`，加「本机」。Agent 下拉只列当前位置的 CLI，键仍是 `local:<id>` 或 `<hostId>:<id>`，profile 字典不改。
- 主按钮用 `LaunchSurface.LaunchButtonText`。多个不同目标显示「多个目标」，工具提示列出全部名称。
- 非终态行放进预览上方的 `ActiveNow` / `ActiveSessions`。历史和「撤销移除」留在 `Sessions`。窄侧栏隐藏会话区时，挂接与释放仍在主列。
- 两个按钮的 `Content` 与自动化名称保持「核对并确认重新挂接」「核对并确认释放」。旁边用独立文本写效果。
- 共用目录横幅在 `LaunchOptionsExpander` 外。该折叠区与组合区默认仍折叠。
- 队列按钮仍在，文案与启用条件不变；非等待时用安静样式，等待资源时仍升为主按钮。
- 同步说明用 `LaunchSurface.SyncHonesty(host.WorkspaceSyncMode)`。查看改动使用 `ChangesAreLocalGit`。

### Task 3: 文档与验证

- 在 `docs/UI-DESIGN.md` §11 注明：这次只落地诚实短句；已收敛、冲突清单、两条链路仍未实现。
- `docs/IMPLEMENTATION.md` 只记录实际跑过的命令。Linux 上跑不了的 WPF / `scripts/test.ps1` 写成未验证，不填通过。

Windows 验收（CI，本环境不能代替）：

- `--launch-surface-checks` 退出 0。
- `--ui-check` 里 UX14 改为位置下拉绑定主机、Agent 下拉不再混入云候选；UX15 覆盖多个目标、None、two-way-resolved 短句、共用目录横幅、窄窗断线按钮。
- 既有 ACC-U04 折叠、ACC-U06 队列文案、CR05 活动行不被历史裁掉、R28/R33 按钮名称与二次确认仍成立。
