# 分屏实机矩阵验证与证据闭合 Implementation Plan

日期：2026-11-16。基线：main `a6d5a1b`（v0.8 + §65～71 修复轮，UI 296/296，CI 绿）。

## Goal

闭合两项档案遗留的分屏验证缺口：

1. IMPLEMENTATION.md §28/§169：「四分屏焦点仍属于未完整验证项」「所有分屏布局未完整测试」。既有实机证据（§14）只覆盖 N=2 左右/上下；N=3（左 1 右 2）与 N=4（2×2）从未在真实 Windows Terminal 上验证。
2. §69 待评审①：共享池（PoolLimitNotice）恢复注记只有对称实现，无 pane 级实证。

**非目标**：不做 8/16 实机压力（规格明确不为 16 个真实 Agent 做压力测试）；不改动启动门禁/队列/只读保护等契约；几何与焦点代码（`PaneLayout`/`TerminalCommands`）只在实机证据证明缺陷时才修；不新增常驻进程/定时器。

## 验证不变量（规格 §3.3/§3.4）

对 N=3、N=4，真实 Windows Terminal + 真实 Worker + 轻量治具子进程：

- 每个窗格子进程以预期 cwd/参数启动并写出 ready 证据；全部 N 个就绪前无一退出。
- 整批恰好 1 个 WT 窗口（`-w agent-<BatchId>`），N>1 时 `--maximized`。
- 焦点不变量：命令序列末尾 `focus-pane --target 0` → WT 窗口标题等于创建序 0 窗格的 `--title`（`NN  名称 · 目录`，含 `--suppressApplicationTitle`）。
- 几何不变量（列等宽、列内行等高）：截图人工核对入档；自动化只断言窗口存在与标题，不伪造像素级断言（WT pane 非 HWND，无可靠程序化几何源——如实记录该限制）。
- 释放信号后全部子进程 ExitCode 0，回执 `Finished`；证据目录含 result.json 与截图。

## 切片与文件归属（互不重叠）

| 切片 | 归属文件 | 内容 |
| --- | --- | --- |
| A 实机布局验证 | 新 `tests/AgentLauncher.LayoutChecks/`（项目+Program.cs）、新 `scripts/test-terminal-layouts.ps1` | 仿 `AgentLauncher.PaneChecks` 模式：从已发布包加载 Core/Windows dll，隔离 Store 根，走真实监控器 IPC reserve + `Commands.SubmitTerminal` 到真实 WT（注册包发现路径），N=3 与 N=4 两轮；断言上节自动化不变量；截图 best-effort（PrintWindow 若黑帧如实记录）；result.json + 退出码。 |
| B 共享池恢复实证 | 仅 `tests/AgentLauncher.Tests/Integration.cs` | 复用 §69 alloc-pressure python 治具模式：共享池两项各持低于单项上限、合计超池上限的提交量 → 断言池恢复注记出现/回弹撤销/历史锁存保留；测试卫生同 §69（屏障、finally 释放、配置还原）。 |
| C 集成与记录 | 主代理 | 构建、全门禁、task_verify、IMPLEMENTATION.md §72、ACCEPTANCE §28/§169 状态更新、提交。 |

## 约束（两切片共同）

- 新测试均为测试基线新增：只增不削，记入 IMPLEMENTATION 待评审。
- WT 窗口短暂打开属既有惯例（AGENTS.md/scripts/test.ps1 §14 均如此）；窗口必须在结束后关闭（最后子进程退出后 WT 窗口若无标签页自动关闭；否则脚本显式关闭）。
- 失败不得弱化断言；实机缺陷先取证再修，修复单独成轮。
- 两切片并行运行：各自隔离 Store 根；只有切片 A 打开真实 WT 窗口。

## 完成判据

1. `test-terminal-layouts.ps1` 对 N=3/N=4 退出 0，证据目录完整（或如实记录实机缺陷并转入修复轮）。
2. Integration 全绿（含新共享池用例）。
3. `scripts/test.ps1` 全门禁 GATE_EXIT=0；task_verify 结果如实上报（passed/failed/unverified 分列）。
4. IMPLEMENTATION.md §72 记录实测结果与限制；ACCEPTANCE 对应行更新。
