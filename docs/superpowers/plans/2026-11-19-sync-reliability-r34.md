# 同步可靠性 R34–R43 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 按 2026-11-18 同步可靠性设计实现每链路七态状态机、只读冲突清单、two-way-safe 新建会话与会话历史护栏，并用三层测试（纯函数 / fake 传播模拟器 / UI）覆盖 R34–R43（R41 的 None 部分按 2026-11-19 更晚的拒收决定执行）。

**Architecture:** 新增纯 Core 文件 `SyncReliability.cs`（无 IO）：mutagen `sync list --long` 文本解析、§4 七态状态机 + 30s 新鲜度、整体标签优先级、§5 冲突清单过滤（≤50 行）、错误文本脱敏。Windows 层只改创建参数（two-way-safe）、探针参数（--long）与会话匹配助手；`FakeSyncSession` 在 `AGENT_LAUNCHER_FAKE_SYNC_SCENARIO` 下路由到新的可脚本化本地三目录模拟器（传播/冲突/离线/flush 失败/调用日志）。App 层徽章改用链路观测 + 上次观测时间，新增只读「同步详情」对话框（整体标签 + 逐链路 + 冲突清单）。本轮不实现 pause/resume 按钮、冲突处理按钮、reset。

**Tech Stack:** C# / .NET 8（Windows x64，`net8.0-windows`），WPF，原生控制台测试 harness（`void Test(string,Action)`），`INTEGRATION_TESTS` 条件编译 + fake-ssh，`System.Text.Json`。无新 NuGet。夹具文本形状取自本机 mutagen 0.18.1 实测与 v0.18.1 源码（`cmd/mutagen/sync/list_monitor_common.go`、`pkg/synchronization/state.go`）。

**Spec:** `docs/superpowers/specs/2026-11-18-sync-reliability-design.md`（权威；全文必读，尤其 §4 状态表、§5 过滤规则、§7 生命周期表、§8 验收表）

**工作树:** `D:\Users\pj\agent-launcher-sync-reliability`，分支 `feat/sync-reliability-r34`，基线 `a108413`。仓库根即 worktree 根（`src/`、`tests/`、`docs/`、`scripts/` 直接在根下，没有 `agent-launcher/` 子目录）。所有命令在此目录运行。**绝不**触碰主工作树 `D:\Users\pj\agent-launcher`（正在跑长稳验收）；不 push、不 merge、不运行 `scripts/run-soak.ps1`。

## Global Constraints

- 新鲜度阈值 **30 秒**（`SyncReliability.FreshnessSeconds = 30`）：成功观测距现在超过 30s，当前状态只能是「未知」，上次标签只作上次观测。
- 传播观察窗 **120 秒**，只覆盖验收夹具：每个新文件 **≤4 KiB**，相对路径不落在忽略规则内。超时即失败，不放宽阈值、不改记已收敛。
- 冲突清单最多 **50 行**（`SyncReliability.MaxConflictRows = 50`），必须写出未显示剩余条数；截断不改变整体「冲突」。
- 徽章自动节流 **≥15 秒**（现有 `TimeSpan.FromSeconds(15)` 不改），由 5s memoryTimer 驱动；单次 `mutagen sync list` 超时 **10 秒**、输出上限 **512 KiB**（现有值不改）；自动 list 同时只跑一个（现有 `syncProbing` 单飞不改）；手动重试每次点击只发一次。
- 诊断只读：不 flush、不 reset、不新建 SSH、不新增自动重连计时器。窗口关闭停止探测（现有 `Closed` 处理不改）。
- 资源边界不动：`ResourcePolicy.MaxConcurrentAgents = 8`、`ResourcePolicy.MaxSlots = 16`；远程批次不进本地内存/CPU 准入；一次 list 不占窗格名额（R43 断言）。
- 同权**新建**会话模式 **two-way-safe**；已存在的 two-way-resolved 会话不批量迁移，解析到该模式时当前状态最高「未收敛」。
- 忽略列表**不扩大**：保持 `--ignore-vcs`（.git）+ `node_modules/` + `bin/` + `obj/`，一个不多一个不少。
- 普通离线/探针失败/暂停：**不 terminate、不 create**；flush 失败保留会话并写 `problem` 回执（C14 现有规则保持）；没有单独确认不执行 reset（本轮根本不实现 reset 入口）。
- **R41 None 偏差（已定决定）**：规格 §6/R41 的「None 继续允许保存和启动」已被 2026-11-19 产品决定（IMPLEMENTATION §92，`WorkspacePreparer.cs:163-164` 与 `MainWindow.xaml.cs:1093` 已实现拒收）取代。R41 的 None 部分改测「保存与启动被拒 + 可读同权文案」；SSHFS 部分照规格（已挂载/挂载失败）。规格是已入库设计，**不改写其历史句子**；偏差记录在 ACCEPTANCE §18 与 IMPLEMENTATION 新节（照 §90 REOPEN 偏差先例）。
- 错误文本展示前脱敏：去地址、用户名、密钥路径、文件正文；保留 `agent-launcher-` 会话名与主机卡片名。冲突清单不附 Address、端口、URL、内容、哈希。
- 中文 UI 文案 UTF-8；新 C# 块四空格缩进、file-scoped namespace；`Nullable=enable`、警告当错误（`Directory.Build.props`）。
- 测试用独立测试根：`AGENT_LAUNCHER_TEST_ROOT` 必须是全新的、含 `AgentLauncher-tests-` 的 TEMP 子目录；worktree 门禁用 `-RuntimeDirectory` 指向本 worktree 的 `artifacts/test-runtime-r34`，避免与主树 `artifacts/test-runtime` 冲突。
- fake 不打开真实 VPS。R44 保持未执行，不进 CI。不宣称 G4、浸泡或新活测。文档不写真实主机名、地址、用户名、密钥（夹具用 `*.example.com` 与 TEST-NET `203.0.113.x`）。
- 验收状态词汇只有：未执行、通过、失败、受阻。ACCEPTANCE 的同步可靠性表实际在 **§18**（任务交接文里写 §17，§17 是远程执行收口；以文件实际为准）。
- 每个 Task 结束跑受影响套件；最终 Task 6 在 worktree 跑完整 `scripts/test.ps1`。下文所有「预期 FAIL/PASS」是实现前/后的形态，不是已测得结果。

## 实测夹具形状（mutagen 0.18.1，本机验证 + v0.18.1 源码核对）

解析器与模拟器输出都按这些真实形状构造：

- 会话块用整行短横线分隔；字段 `Name:`、`Identifier:`、`Alpha:`/`Beta:` 段内缩进 `URL:`、`Connected: Yes/No`。
- **默认 `sync list` 不显示同步模式，冲突只有计数 `Conflicts: N`**；`sync list --long`（`-l`）才有 `Configuration:` 段内 `Synchronization mode: Two Way Safe` / `Two Way Resolved`，以及逐条冲突：
  ```
  Conflicts:
  	(alpha) same.txt (<non-existent> -> File (c9b716f8ee2c8e505e4302af98227ad4cdcf8786))
  	(beta)  same.txt (<non-existent> -> File (112df4c014e10ee40f1ffef20e4e0bf31cf53ded))
  ```
  被排除的冲突尾行：`	...+7 more...`。因此**探针必须用 `--long`**（仍是一次只读 list；这是对规格「mutagen sync list」措辞的实现细化，Task 6 记入 IMPLEMENTATION）。
- Status 全集（`pkg/synchronization/state.go` Description()）：`Disconnected`、`Halted due to one-sided root emptying`、`Halted due to root deletion`、`Halted due to root type change`、`Connecting to alpha`、`Connecting to beta`、`Watching for changes`、`Scanning files`、`Waiting 5 seconds for rescan`、`Reconciling changes`、`Staging files on alpha`、`Staging files on beta`、`Applying changes`、`Saving archive`、`Unknown`；操作员暂停时整行被替换为 `Status: [Paused]`。
- `Transition problems:` 在端点段内（long 形如 `	Transition problems:\n		work: permission denied`；默认形如 `	Transition problems: 1`）。`Last error: <text>` 在 Conflicts 之后、Status 之前。
- 连接失败形状（源码 `printEndpoint` 只输出 `Connected: No`，错误细节在会话级 `Last error:`）：
  ```
  Beta:
  	URL: ssh://agent@jy.example.com:22/home/agent/work
  	Connected: No
  Last error: unable to connect to beta: unable to connect to endpoint: unable to dial agent endpoint: dial tcp 203.0.113.7:22: connectex: No connection could be made because the target machine actively refused it.
  Status: Connecting to beta
  ```
- 根冲突路径被 mutagen 渲染为 `<root>`（`formatPath("")`）；本计划按不安全路径丢弃（不是相对文件路径）。

## 文件结构总览

| 文件 | 责任 | Task |
| --- | --- | --- |
| Create `src/AgentLauncher.Core/SyncReliability.cs` | 纯函数：解析、七态、新鲜度、整体标签、冲突清单、脱敏、None/SSHFS 文案 | 1 |
| Create `tests/AgentLauncher.Tests/SyncReliabilityChecks.cs` | 原生纯函数回归（R34/R36 过滤/R38/R39/R40/R41 文案/R43） | 1,2,4 |
| Modify `tests/AgentLauncher.Tests/Program.cs` | 注册原生检查 + 新集成套件分发 | 1,3 |
| Modify `src/AgentLauncher.Windows/WorkspaceSync.cs` | two-way-safe 创建参数抽取；FakeSyncSession 路由模拟器 | 2,3 |
| Modify `src/AgentLauncher.Windows/WorkspaceInsights.cs` | 探针 --long；`FindSyncSession`；`SplitSessions` 委托 Core | 2 |
| Create `src/AgentLauncher.Windows/FakeSyncSimulator.cs` | `#if INTEGRATION_TESTS` 三目录传播模拟器 + 调用日志 | 3 |
| Create `tests/AgentLauncher.Tests/SyncReliabilityIntegration.cs` | `--sync-reliability-integration`：R35/R36/R37/R38/R42 | 3,4 |
| Modify `src/AgentLauncher.Core/RemoteHostEditor.cs` | `ValidateSyncMode` 共享 None 拒收文案 | 4 |
| Modify `src/AgentLauncher.App/MainWindow.xaml.cs` | EditHost 用共享文案；菜单加「同步详情…」 | 4,5 |
| Modify `src/AgentLauncher.App/MainWindow.Practical.cs` | 链路徽章 + 上次观测 + 新鲜度 + 同步详情对话框 | 5 |
| Modify `src/AgentLauncher.App/UiChecks.cs` | 徽章/详情/冲突清单 UI 回归 | 5 |
| Modify `scripts/test.ps1` | 接入 `--sync-review-checks` 与新集成套件 | 1,3 |
| Modify `docs/ACCEPTANCE.md`、`docs/CONTRACTS.md`、`docs/IMPLEMENTATION.md` | 状态、C14、§93 与偏差记录 | 6 |

---

### Task 1: Core 状态机 + list 解析 + 冲突过滤 + 脱敏（纯函数）

**Files:**
- Create: `src/AgentLauncher.Core/SyncReliability.cs`
- Create: `tests/AgentLauncher.Tests/SyncReliabilityChecks.cs`
- Modify: `tests/AgentLauncher.Tests/Program.cs:263`（`RemoteExecutionChecks.Run` 之后注册）
- Modify: `scripts/test.ps1:5-6`（原生套件后接入 `--sync-review-checks`，使既有 UX12 检查进门禁）

**Interfaces:**
- Consumes: `AgentLauncher.Core.WorkspaceSyncMode`（`RemoteExecutionSpec.cs:46-51`）、`ResourcePolicy.MaxConcurrentAgents/MaxSlots`、`ControlEntryStates.IsTerminal`。
- Produces（后续所有 Task 依赖的精确签名）:
  ```csharp
  namespace AgentLauncher.Core;
  public enum SyncLinkState { SafetyPaused, Conflict, Unknown, Offline, NotConverged, Syncing, Converged }
  public sealed record SyncConflictRow(string RelativePath, string LinkLabel, string AlphaRole, string BetaRole);
  public sealed record SyncConflictList(SyncConflictRow[] Rows, int RemainingCount, int DiscardedCount);
  public sealed record ParsedMutagenSession(
      string Name, string AlphaUrl, string BetaUrl,
      string SyncMode,            // 归一化 "two-way-safe"/"two-way-resolved"/其他原值小写/""（未显示）
      string Status,              // Status 行原值；缺失为 ""
      bool Paused,                // Status == "[Paused]"
      bool BetaConnected,         // Beta 段 Connected: Yes/No；缺失按 true
      bool ConnectionFailure,     // 已识别的连接失败（见实现）
      string LastError,
      bool TransitionProblems,
      string[] SafeConflictPaths, // 去重、保序、仅安全相对路径
      int DiscardedConflictEntries, // 不安全/无法解析而被丢弃的条目数（含默认 list 的计数形）
      int ExcludedConflicts,      // mutagen "...+N more..."
      bool ConflictSectionPresent);
  public sealed record SyncLinkObservation(SyncLinkState State, string Detail, DateTimeOffset? LastObservedAt)
  {
      public string Label => SyncReliability.Label(State);
  }
  public static class SyncReliability
  {
      public const int FreshnessSeconds = 30;
      public const int MaxConflictRows = 50;
      public static string Label(SyncLinkState state);
      public static string[] SplitSessionBlocks(string listOutput);
      public static ParsedMutagenSession? ParseSession(string block);   // 无 Name 行 → null
      public static bool IsSafeRelativePath(string path);
      public static SyncLinkObservation EvaluateLink(ParsedMutagenSession session, DateTimeOffset observedAt, DateTimeOffset now);
      public static SyncLinkObservation FailedProbe(string error, DateTimeOffset? lastSuccessAt, SyncLinkState? lastState, DateTimeOffset now);
      public static string OverallLabel(IReadOnlyList<SyncLinkState> requiredLinks);
      public static SyncConflictList BuildConflictList(IReadOnlyList<(ParsedMutagenSession Session, string LinkLabel, string AlphaRole, string BetaRole)> links);
      public static string SanitizeErrorText(string text);
      public static (string Label, string Detail) NonMutagenSyncBadge(WorkspaceSyncMode mode, bool attemptAlive, bool attemptFailed);
  }
  ```

- [ ] **Step 1: 写失败测试（解析 + 七态 + 整体标签 + 过滤 + 脱敏 + 文案）**

创建 `tests/AgentLauncher.Tests/SyncReliabilityChecks.cs`（完整文件；夹具用 `\t`/`\n` 显式构造，形状与上文实测一致）：

```csharp
using AgentLauncher.Core;

namespace AgentLauncher.Tests;

// 同步可靠性 R34–R43 纯函数回归（规格 2026-11-18 §4/§5/§6/§8）。
// 全部用 canned mutagen sync list --long 文本（形状取自 mutagen 0.18.1 实测），不依赖真实 mutagen/VPS。
internal static class SyncReliabilityChecks
{
    // 与真实输出同形：短横线分隔、段内缩进、Last error 在 Status 之前、Transition problems 在 Beta 段内。
    internal static string Session(
        string name, string mode, string status,
        string betaConnected = "Yes", string conflicts = "", string lastError = "", string betaExtra = "") =>
        "-------------------------------------------------------------------------------\n" +
        $"Name: {name}\n" +
        $"Identifier: sync_fake{name}\n" +
        "Configuration:\n" +
        $"\tSynchronization mode: {mode}\n" +
        "Alpha:\n" +
        "\tURL: D:\\fixture\\hub\n" +
        "\tConnected: Yes\n" +
        "Beta:\n" +
        "\tURL: ssh://agent@jy.example.com:22/home/agent/work\n" +
        $"\tConnected: {betaConnected}\n" +
        betaExtra +
        conflicts +
        (lastError.Length > 0 ? $"Last error: {lastError}\n" : "") +
        $"Status: {status}\n" +
        "-------------------------------------------------------------------------------\n";

    internal static string ConflictLines(params string[] paths)
    {
        var text = "Conflicts:\n";
        foreach (var p in paths)
        {
            text += $"\t(alpha) {p} (<non-existent> -> File (c9b716f8ee2c8e505e4302af98227ad4cdcf8786))\n";
            text += $"\t(beta)  {p} (<non-existent> -> File (112df4c014e10ee40f1ffef20e4e0bf31cf53ded))\n";
        }
        return text;
    }

    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        var now = new DateTimeOffset(2026, 11, 19, 14, 2, 11, TimeSpan.FromHours(8));
        var fresh = now.AddSeconds(-5);
        var stale = now.AddSeconds(-31);

        test("R34 mutagen long list parses mode urls status and two session blocks", () =>
        {
            var jy = Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes");
            var aliyun = Session("agent-launcher-aly0002", "Two Way Safe", "Watching for changes");
            var blocks = SyncReliability.SplitSessionBlocks(jy + aliyun);
            assert(blocks.Length == 2, "两条会话块切分错误");
            var parsed = SyncReliability.ParseSession(blocks[0])!;
            assert(parsed.Name == "agent-launcher-jy0001", "会话名解析错误");
            assert(parsed.SyncMode == "two-way-safe", "同步模式未归一化：" + parsed.SyncMode);
            assert(parsed.AlphaUrl == "D:\\fixture\\hub" && parsed.BetaUrl == "ssh://agent@jy.example.com:22/home/agent/work", "端点 URL 解析错误");
            assert(parsed.Status == "Watching for changes" && parsed.BetaConnected && !parsed.Paused, "状态/连接解析错误");
            assert(SyncReliability.ParseSession(blocks[1])!.Name == "agent-launcher-aly0002", "第二块解析错误");
            // 单会话无分隔线也能解析；无 Name 的垃圾输出 → null（R39 无法解析）。
            assert(SyncReliability.ParseSession(jy)!.Name == "agent-launcher-jy0001", "单块解析失败");
            assert(SyncReliability.ParseSession("garbage without fields") == null, "垃圾输出被解析成会话");
            assert(SyncReliability.ParseSession("") == null, "空输出被解析成会话");
        });

        test("R34 converged requires two-way-safe Watching fresh and both links for overall", () =>
        {
            var ok = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes"))!;
            var obs = SyncReliability.EvaluateLink(ok, fresh, now);
            assert(obs.State == SyncLinkState.Converged && obs.Label == "已收敛", "健康会话不是已收敛：" + obs.Detail);
            assert(obs.LastObservedAt == fresh, "上次观测时间丢失");
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged, SyncLinkState.Converged }) == "全部已收敛", "双链路已收敛整体标签错误");
            // 单条 Watching 不构成全部已收敛（R34）；单链路至多单链路已收敛（§3）。
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged }) == "单链路已收敛", "单链路被写成全部已收敛");
            assert(SyncReliability.OverallLabel(Array.Empty<SyncLinkState>()) == "未同步", "空链路整体标签错误");
            // 一条过期 → 未知 → 整体未知，绝不全部已收敛。
            var expired = SyncReliability.EvaluateLink(ok, stale, now);
            assert(expired.State == SyncLinkState.Unknown, "过期观测不是未知");
            assert(expired.Detail.Contains("已收敛") && expired.Detail.Contains("上次观测"), "过期详情未保留上次标签作上次观测");
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged, expired.State }) == "未知", "过期链路没有把整体拉成未知");
        });

        test("R34/R39 overall priority follows spec section 4 order", () =>
        {
            string Overall(params SyncLinkState[] s) => SyncReliability.OverallLabel(s);
            assert(Overall(SyncLinkState.SafetyPaused, SyncLinkState.Conflict) == "安全暂停", "优先级 1 错误");
            assert(Overall(SyncLinkState.Conflict, SyncLinkState.Unknown) == "冲突", "优先级 2 错误");
            assert(Overall(SyncLinkState.Unknown, SyncLinkState.Offline) == "未知", "优先级 3 错误");
            assert(Overall(SyncLinkState.Offline, SyncLinkState.NotConverged) == "离线", "优先级 4 错误");
            assert(Overall(SyncLinkState.NotConverged, SyncLinkState.Syncing) == "未收敛", "优先级 5 错误");
            assert(Overall(SyncLinkState.Syncing, SyncLinkState.Converged) == "同步中", "优先级 6 错误");
            assert(Overall(SyncLinkState.Converged, SyncLinkState.Converged) == "全部已收敛", "优先级 7 错误");
        });

        test("R39 probe failure and staleness are unknown and keep last observation", () =>
        {
            foreach (var error in new[] { "mutagen sync list 超时（10 秒），已终止；同步状态未知。", "mutagen sync list 失败（会话可能不存在）：exit 1", "未找到 mutagen 或无法运行：命令不存在", "输出为空" })
            {
                var obs = SyncReliability.FailedProbe(error, fresh, SyncLinkState.Converged, now);
                assert(obs.State == SyncLinkState.Unknown && obs.Label == "未知", "探针失败不是未知：" + error);
                assert(obs.LastObservedAt == fresh, "探针失败丢掉了上次观测时间");
                assert(obs.Detail.Contains("14:02:06") || obs.Detail.Contains("上次观测"), "详情未给出上次观测");
                assert(!obs.Detail.Contains("已收敛") || obs.Detail.Contains("上次状态"), "未知被写成当前已收敛");
            }
            var never = SyncReliability.FailedProbe("超时", null, null, now);
            assert(never.State == SyncLinkState.Unknown && never.LastObservedAt == null && never.Detail.Contains("尚无成功观测"), "无历史探针失败详情错误");
            // 恰好 30s 仍新鲜，31s 过期（阈值取自规格 §4）。
            var boundary = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes"))!;
            assert(SyncReliability.EvaluateLink(boundary, now.AddSeconds(-30), now).State == SyncLinkState.Converged, "恰好 30 秒被误判过期");
            assert(SyncReliability.EvaluateLink(boundary, now.AddSeconds(-31), now).State == SyncLinkState.Unknown, "31 秒未判过期");
        });

        test("R40 safety halt reasons and operator pause are safety-paused never converged", () =>
        {
            foreach (var (status, reason) in new[]
            {
                ("Halted due to root deletion", "同步根被删除"),
                ("Halted due to one-sided root emptying", "清空"),
                ("Halted due to root type change", "类型"),
                ("Halted due to something unclassified", "安全暂停"),
                ("[Paused]", "操作员暂停"),
            })
            {
                var parsed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", status))!;
                var obs = SyncReliability.EvaluateLink(parsed, fresh, now);
                assert(obs.State == SyncLinkState.SafetyPaused && obs.Label == "安全暂停", $"{status} 不是安全暂停");
                assert(obs.Detail.Contains(reason), $"{status} 详情未写明原因分类：{obs.Detail}");
            }
            // 安全暂停优先于冲突与离线。
            var haltedWithConflict = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Halted due to root deletion", conflicts: ConflictLines("a.txt")))!;
            assert(SyncReliability.EvaluateLink(haltedWithConflict, fresh, now).State == SyncLinkState.SafetyPaused, "安全暂停没有优先于冲突");
        });

        test("R36 conflict paths are parsed paired filtered and capped at 50 with remainder", () =>
        {
            var parsed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", conflicts: ConflictLines("docs/plan.md", "src/a.txt")))!;
            assert(parsed.SafeConflictPaths.SequenceEqual(new[] { "docs/plan.md", "src/a.txt" }), "冲突路径解析/去重错误：" + string.Join(",", parsed.SafeConflictPaths));
            var obs = SyncReliability.EvaluateLink(parsed, fresh, now);
            assert(obs.State == SyncLinkState.Conflict && obs.Label == "冲突", "有安全冲突行不是冲突");
            var list = SyncReliability.BuildConflictList(new[] { (parsed, "本机—京云", "本机", "京云") });
            assert(list.Rows.Length == 2 && list.Rows[0].RelativePath == "docs/plan.md" && list.Rows[0].LinkLabel == "本机—京云" && list.Rows[0].AlphaRole == "本机" && list.Rows[0].BetaRole == "京云", "冲突行字段错误");
            assert(list.RemainingCount == 0 && list.DiscardedCount == 0, "无截断时剩余计数错误");
            // 60 条 → 50 行 + 剩余 10；整体仍是冲突。
            var many = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines(Enumerable.Range(1, 60).Select(i => $"f{i:D3}.txt").ToArray())))!;
            var capped = SyncReliability.BuildConflictList(new[] { (many, "本机—京云", "本机", "京云") });
            assert(capped.Rows.Length == 50 && capped.RemainingCount == 10, "50 行截断或剩余计数错误");
            assert(SyncReliability.EvaluateLink(many, fresh, now).State == SyncLinkState.Conflict, "截断后不再是冲突");
            // mutagen 排除行 ...+7 more... 计入剩余。
            var excluded = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines("a.txt") + "\t...+7 more...\n"))!;
            assert(excluded.ExcludedConflicts == 7, "排除冲突计数解析错误");
            var excludedList = SyncReliability.BuildConflictList(new[] { (excluded, "本机—京云", "本机", "京云") });
            assert(excludedList.Rows.Length == 1 && excludedList.RemainingCount == 7, "排除冲突未计入剩余");
            assert(SyncReliability.EvaluateLink(excluded, fresh, now).State == SyncLinkState.Conflict, "有安全行+排除行不是冲突");
        });

        test("R36 unsafe conflict paths are dropped and all-unsafe means unknown", () =>
        {
            foreach (var bad in new[] { "", "../escape.txt", "a/../../b.txt", "/etc/passwd", "\\abs\\x.txt", "C:\\windows\\evil.txt", "c:/x.txt", "\\\\server\\share\\x.txt", "<root>" })
                assert(!SyncReliability.IsSafeRelativePath(bad), "不安全路径被接受：" + bad);
            foreach (var good in new[] { "a.txt", "docs/plan.md", "中文 文件.txt", "a/b/../c.txt".Replace("/../", "/") })
                assert(SyncReliability.IsSafeRelativePath(good), "安全路径被拒绝：" + good);
            assert(!SyncReliability.IsSafeRelativePath("a/../b.txt"), ".. 段未被拒绝");
            // 混合：保留安全行，状态冲突，详情注明无法展示的条目。
            var mixed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines("ok.txt", "../escape.txt", "C:\\evil.txt")))!;
            assert(mixed.SafeConflictPaths.SequenceEqual(new[] { "ok.txt" }) && mixed.DiscardedConflictEntries == 2, "混合冲突过滤错误");
            var mixedObs = SyncReliability.EvaluateLink(mixed, fresh, now);
            assert(mixedObs.State == SyncLinkState.Conflict && mixedObs.Detail.Contains("无法展示"), "混合冲突状态/详情错误");
            // 全不安全：一条安全行都没有 → 未知（§4/§5）。
            var allBad = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes",
                conflicts: ConflictLines("../escape.txt", "/etc/passwd")))!;
            assert(allBad.SafeConflictPaths.Length == 0 && allBad.DiscardedConflictEntries == 2, "全不安全冲突计数错误");
            assert(SyncReliability.EvaluateLink(allBad, fresh, now).State == SyncLinkState.Unknown, "全不安全冲突不是未知");
            // 默认 list 的计数形 Conflicts: 3 无法展示路径 → 未知（fail-closed）。
            var countOnly = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", conflicts: "Conflicts: 3\n"))!;
            assert(countOnly.ConflictSectionPresent && countOnly.SafeConflictPaths.Length == 0, "计数形冲突解析错误");
            assert(SyncReliability.EvaluateLink(countOnly, fresh, now).State == SyncLinkState.Unknown, "计数形冲突不是未知");
        });

        test("R38 one offline link blocks overall convergence and healthy link stays converged", () =>
        {
            var offline = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Connecting to beta",
                betaConnected: "No",
                lastError: "unable to connect to beta: unable to connect to endpoint: unable to dial agent endpoint: dial tcp 203.0.113.7:22: connectex: No connection could be made because the target machine actively refused it."))!;
            assert(offline.ConnectionFailure, "连接失败未被识别");
            var offlineObs = SyncReliability.EvaluateLink(offline, fresh, now);
            assert(offlineObs.State == SyncLinkState.Offline && offlineObs.Label == "离线", "连接失败不是离线");
            var healthy = SyncReliability.ParseSession(Session("agent-launcher-aly0002", "Two Way Safe", "Watching for changes"))!;
            assert(SyncReliability.EvaluateLink(healthy, fresh, now).State == SyncLinkState.Converged, "健康链路不是已收敛");
            assert(SyncReliability.OverallLabel(new[] { offlineObs.State, SyncLinkState.Converged }) == "离线", "一离线一健康整体不是离线");
            assert(SyncReliability.OverallLabel(new[] { offlineObs.State, SyncLinkState.Converged }) != "全部已收敛", "离线被写成全部已收敛");
            // Connected: No 但状态 Watching 且无连接错误文本 → 不能确认连接失败 → 未收敛（§4 离线需确认）。
            var ambiguous = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", betaConnected: "No"))!;
            assert(!ambiguous.ConnectionFailure && SyncReliability.EvaluateLink(ambiguous, fresh, now).State == SyncLinkState.NotConverged, "含糊连接状态被误判离线/已收敛");
        });

        test("R34 two-way-resolved caps at not-converged and syncing states are recognized", () =>
        {
            var resolved = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Resolved", "Watching for changes"))!;
            assert(resolved.SyncMode == "two-way-resolved", "resolved 模式归一化错误");
            var resolvedObs = SyncReliability.EvaluateLink(resolved, fresh, now);
            assert(resolvedObs.State == SyncLinkState.NotConverged && resolvedObs.Detail.Contains("alpha"), "resolved 会话进入已收敛或详情未说明 alpha 赢");
            var resolvedSyncing = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Resolved", "Reconciling changes"))!;
            assert(SyncReliability.EvaluateLink(resolvedSyncing, fresh, now).State == SyncLinkState.NotConverged, "resolved 会话超过未收敛上限");
            foreach (var status in new[] { "Scanning files", "Reconciling changes", "Staging files on alpha", "Staging files on beta", "Applying changes", "Waiting 5 seconds for rescan", "Saving archive" })
            {
                var parsed = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", status))!;
                assert(SyncReliability.EvaluateLink(parsed, fresh, now).State == SyncLinkState.Syncing, $"{status} 不是同步中");
            }
            // Transition problems → 未收敛；模式未显示 → 不能确认 two-way-safe → 未收敛（fail-closed）。
            var problems = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes", betaExtra: "\tTransition problems:\n\t\twork: permission denied\n"))!;
            assert(problems.TransitionProblems && SyncReliability.EvaluateLink(problems, fresh, now).State == SyncLinkState.NotConverged, "Transition problems 不是未收敛");
            var noMode = SyncReliability.ParseSession("Name: agent-launcher-jy0001\nStatus: Watching for changes\n")!;
            assert(noMode.SyncMode == "" && SyncReliability.EvaluateLink(noMode, fresh, now).State == SyncLinkState.NotConverged, "未显示模式被当成 two-way-safe");
            // 无法确认为同步中的非 Watching（如 Disconnected 且无连接失败证据）→ 未收敛。
            var disconnected = SyncReliability.ParseSession(Session("agent-launcher-jy0001", "Two Way Safe", "Disconnected"))!;
            assert(SyncReliability.EvaluateLink(disconnected, fresh, now).State is SyncLinkState.NotConverged or SyncLinkState.Offline, "Disconnected 被当成已收敛");
        });

        test("R41 None and SSHFS badges never claim convergence", () =>
        {
            var (noneLabel, noneDetail) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.None, false, false);
            assert(noneLabel == "未同步", "None 标签错误");
            assert(noneDetail.Contains("不是同权") && noneDetail.Contains("拒收"), "None 详情缺少不是同权/拒收文案：" + noneDetail);
            var (mounted, mountDetail) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.SshfsMount, true, false);
            assert(mounted == "已挂载" && mountDetail.Contains("不是已收敛"), "SSHFS 成功文案错误");
            var (failed, failDetail) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.SshfsMount, false, true);
            assert(failed == "挂载失败" && failDetail.Contains("不是已收敛"), "SSHFS 失败文案错误");
            var (idle, _) = SyncReliability.NonMutagenSyncBadge(WorkspaceSyncMode.SshfsMount, false, false);
            assert(idle == "未挂载", "SSHFS 无尝试文案错误");
            // 两者映射进整体时都不能产生全部已收敛。
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged, SyncLinkState.NotConverged }) == "未收敛", "None/SSHFS 链路未阻止全部已收敛");
        });

        test("R43 resource boundaries unchanged and single link never means all converged", () =>
        {
            assert(ResourcePolicy.MaxConcurrentAgents == 8, "8 窗格名额被改动");
            assert(ResourcePolicy.MaxSlots == 16, "16 保存槽位被改动");
            var remote = new ControlEntry { Plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "r", "D:\\fixture", new("D:\\fixture", "k"), false, new("pi", [], "remote"), 0, RemoteExecution: new RemoteExecutionSpec("jy.example.com", 22, "root", "agent", "pi", "/home/agent/work", [], null, null)), State = "Disconnected" };
            assert(!ControlEntryStates.IsTerminal(remote), "远程 Disconnected 被当成终态（会腾出名额）");
            assert(SyncReliability.OverallLabel(new[] { SyncLinkState.Converged }) != "全部已收敛", "单链路被写成全部已收敛");
        });

        test("R34 error text sanitization keeps session name and strips address user key path", () =>
        {
            var raw = "会话 agent-launcher-jy0001 就绪超时：unable to connect to beta: ssh://agent@jy.example.com:22/home/agent/work dial tcp 203.0.113.7:22: connectex refused; key C:\\Users\\operator\\.ssh\\id_ed25519 加载失败; user@host 认证失败; UNC \\\\nas\\share\\key.pem 不可达";
            var clean = SyncReliability.SanitizeErrorText(raw);
            assert(clean.Contains("agent-launcher-jy0001"), "会话名被脱敏掉");
            assert(!clean.Contains("203.0.113.7"), "IP 未脱敏");
            assert(!clean.Contains("jy.example.com"), "主机地址未脱敏");
            assert(!clean.Contains("agent@"), "用户名未脱敏");
            assert(!clean.Contains("id_ed25519") && !clean.Contains("C:\\Users"), "密钥路径未脱敏");
            assert(!clean.Contains("\\\\nas"), "UNC 未脱敏");
            assert(!clean.Contains("/home/agent"), "远程路径未脱敏");
            assert(SyncReliability.SanitizeErrorText("") == "", "空文本脱敏崩溃");
        });
    }
}
```

- [ ] **Step 2: 注册并运行，确认失败**

在 `tests/AgentLauncher.Tests/Program.cs` 第 263 行 `AgentLauncher.Tests.RemoteExecutionChecks.Run(Test,Assert);` 之后加：

```csharp
AgentLauncher.Tests.SyncReliabilityChecks.Run(Test,Assert);
```

Run（PowerShell，worktree 根）: `dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release`
Expected: 编译失败 `CS0246: SyncReliability 未找到`（这就是本步的失败形态）。

- [ ] **Step 3: 实现 Core 纯函数**

创建 `src/AgentLauncher.Core/SyncReliability.cs`（完整文件）：

```csharp
using System.Text.RegularExpressions;

namespace AgentLauncher.Core;

// 同步可靠性（规格 2026-11-18 §4/§5/§6）：每链路七态、30s 新鲜度、整体标签优先级、冲突清单过滤与错误脱敏。
// 纯函数、无 IO：输入是 mutagen sync list --long 的文本与观测时间，输出是可显示的状态与清单。
public enum SyncLinkState { SafetyPaused, Conflict, Unknown, Offline, NotConverged, Syncing, Converged }

/// <summary>只读冲突行（§5）：相对路径 + 受影响会话 + 两端角色。不含内容、哈希、URL、地址、端口。</summary>
public sealed record SyncConflictRow(string RelativePath, string LinkLabel, string AlphaRole, string BetaRole);

/// <summary>冲突清单：最多 50 行；RemainingCount 含被截断的安全行与 mutagen 排除行；DiscardedCount 是无法展示的不安全条目。</summary>
public sealed record SyncConflictList(SyncConflictRow[] Rows, int RemainingCount, int DiscardedCount);

/// <summary>mutagen sync list --long 单会话块的解析结果。SyncMode 为空表示输出未显示模式（不能确认 two-way-safe）。</summary>
public sealed record ParsedMutagenSession(
    string Name, string AlphaUrl, string BetaUrl,
    string SyncMode, string Status, bool Paused, bool BetaConnected, bool ConnectionFailure,
    string LastError, bool TransitionProblems,
    string[] SafeConflictPaths, int DiscardedConflictEntries, int ExcludedConflicts, bool ConflictSectionPresent);

/// <summary>一次链路评估：当前状态 + 可读详情 + 上次成功观测时间（始终单独给出，过期不升为当前）。</summary>
public sealed record SyncLinkObservation(SyncLinkState State, string Detail, DateTimeOffset? LastObservedAt)
{
    public string Label => SyncReliability.Label(State);
}

public static class SyncReliability
{
    /// <summary>§4：成功观测距现在超过 30 秒只能作上次观测。取自 15s 节流 + 10s 超时的余量，不是传播时限。</summary>
    public const int FreshnessSeconds = 30;
    /// <summary>§5：冲突清单最多显示 50 行，剩余条数必须写出。</summary>
    public const int MaxConflictRows = 50;

    private static readonly string[] ConnectionMarkers =
    [
        "unable to connect", "connection refused", "actively refused", "dial tcp", "could not resolve hostname",
        "connection timed out", "timed out", "handshake", "connection reset", "no route to host", "network is unreachable",
    ];
    private static readonly string[] SyncingStatuses =
    [
        "Scanning files", "Reconciling changes", "Staging files on alpha", "Staging files on beta",
        "Applying changes", "Waiting 5 seconds for rescan", "Saving archive",
    ];
    private static readonly Regex ConflictLine = new(@"^\((alpha|beta)\)\s+(.+)\s+\((.* -> .*)\)$", RegexOptions.Compiled);
    private static readonly Regex ExcludedLine = new(@"^\.\.\.\+(\d+) more\.\.\.$", RegexOptions.Compiled);

    public static string Label(SyncLinkState state) => state switch
    {
        SyncLinkState.SafetyPaused => "安全暂停",
        SyncLinkState.Conflict => "冲突",
        SyncLinkState.Unknown => "未知",
        SyncLinkState.Offline => "离线",
        SyncLinkState.NotConverged => "未收敛",
        SyncLinkState.Syncing => "同步中",
        SyncLinkState.Converged => "已收敛",
        _ => "未知",
    };

    /// <summary>整行短横线分隔会话块；单会话可能没有分隔线（与 WorkspaceInsights.SplitSessions 同一规则）。</summary>
    public static string[] SplitSessionBlocks(string listOutput)
    {
        var blocks = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var line in (listOutput ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length >= 10 && line.All(c => c == '-'))
            {
                if (current.Length > 0) blocks.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.AppendLine(line);
        }
        if (current.Length > 0) blocks.Add(current.ToString());
        return blocks.ToArray();
    }

    /// <summary>§5 安全路径过滤：拒绝空、绝对、盘符、UNC、任何含 .. 段；&lt;root&gt; 是根冲突不是相对文件路径，同样丢弃。</summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "<root>") return false;
        if (path.StartsWith('/') || path.StartsWith('\\')) return false;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return false;
        return path.Split('/', '\\').All(segment => segment != "..");
    }

    /// <summary>解析单个会话块。无 Name 行视为无法解析（null）。冲突计数形（默认 list）记为无法展示的条目。</summary>
    public static ParsedMutagenSession? ParseSession(string block)
    {
        var name = Field(block, "Name:");
        if (name.Length == 0) return null;
        string alphaUrl = "", betaUrl = "", section = "", syncMode = "";
        var betaConnected = true;
        var safe = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int discarded = 0, excluded = 0;
        var conflictSection = false;
        var inConflicts = false;
        foreach (var raw in (block ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var indented = raw.StartsWith('\t') || raw.StartsWith(' ');
            if (!indented)
            {
                section = trimmed is "Alpha:" or "Beta:" ? trimmed[..^1] : "";
                inConflicts = false;
            }
            if (trimmed.StartsWith("URL:", StringComparison.Ordinal) && section.Length > 0)
            {
                var url = trimmed[4..].Trim();
                if (section == "Alpha" && alphaUrl.Length == 0) alphaUrl = url;
                if (section == "Beta" && betaUrl.Length == 0) betaUrl = url;
            }
            if (section == "Beta" && trimmed.StartsWith("Connected:", StringComparison.Ordinal))
                betaConnected = trimmed[10..].Trim() == "Yes";
            if (trimmed.StartsWith("Synchronization mode:", StringComparison.Ordinal))
                syncMode = NormalizeMode(trimmed[21..].Trim());
            else if (trimmed.StartsWith("Sync mode:", StringComparison.Ordinal))
                syncMode = NormalizeMode(trimmed[10..].Trim());
            if (trimmed.StartsWith("Conflicts:", StringComparison.Ordinal))
            {
                conflictSection = true;
                var value = trimmed[10..].Trim();
                if (int.TryParse(value, out var count))
                {
                    // 默认 list 只有计数：知道有冲突但无法展示路径 → 全部记为无法展示（fail-closed 未知）。
                    discarded += count;
                    inConflicts = false;
                }
                else inConflicts = value.Length == 0;
                continue;
            }
            if (!inConflicts) continue;
            var excludedMatch = ExcludedLine.Match(trimmed);
            if (excludedMatch.Success) { excluded += int.Parse(excludedMatch.Groups[1].Value); continue; }
            var match = ConflictLine.Match(trimmed);
            if (!match.Success) { discarded++; continue; }
            var path = match.Groups[2].Value.Trim();
            if (!IsSafeRelativePath(path)) { discarded++; continue; }
            if (seen.Add(path)) safe.Add(path);
        }
        var status = Field(block, "Status:");
        var lastError = Field(block, "Last error:");
        var connectionFailure = !betaConnected &&
            (status is "Connecting to beta" or "Disconnected" ||
             ConnectionMarkers.Any(m => lastError.Contains(m, StringComparison.OrdinalIgnoreCase)));
        return new ParsedMutagenSession(
            name, alphaUrl, betaUrl, syncMode, status,
            Paused: status == "[Paused]", BetaConnected: betaConnected, ConnectionFailure: connectionFailure,
            LastError: lastError, TransitionProblems: block.Contains("Transition problems:", StringComparison.Ordinal),
            SafeConflictPaths: safe.ToArray(), DiscardedConflictEntries: discarded, ExcludedConflicts: excluded,
            ConflictSectionPresent: conflictSection);
    }

    private static string NormalizeMode(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return compact switch
        {
            "twowaysafe" => "two-way-safe",
            "twowayresolved" => "two-way-resolved",
            _ => value.ToLowerInvariant(),
        };
    }

    private static string Field(string block, string prefix)
    {
        foreach (var line in (block ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal)) return trimmed[prefix.Length..].Trim();
        }
        return "";
    }

    /// <summary>§4 状态机：只用这一次观测计算当前状态；超过 30s 的观测降为未知并保留上次标签作上次观测。</summary>
    public static SyncLinkObservation EvaluateLink(ParsedMutagenSession session, DateTimeOffset observedAt, DateTimeOffset now)
    {
        var (state, detail) = Classify(session);
        if (now - observedAt > TimeSpan.FromSeconds(FreshnessSeconds))
            return new(SyncLinkState.Unknown,
                $"观测过期（距上次成功观测超过 {FreshnessSeconds} 秒）；上次状态 {Label(state)} 只是上次观测，不是当前状态。",
                observedAt);
        return new(state, detail, observedAt);
    }

    private static (SyncLinkState State, string Detail) Classify(ParsedMutagenSession session)
    {
        if (session.Paused)
            return (SyncLinkState.SafetyPaused, "操作员暂停（mutagen pause）；会话历史保留，不自动 resume。");
        if (session.Status.StartsWith("Halted", StringComparison.Ordinal))
        {
            var reason = session.Status.Contains("root deletion", StringComparison.Ordinal) ? "一端的同步根被删除"
                : session.Status.Contains("one-sided root emptying", StringComparison.Ordinal) ? "只有一端把同步根清空"
                : session.Status.Contains("root type change", StringComparison.Ordinal) ? "同步根被换成另一种类型"
                : "原因无法归类";
            return (SyncLinkState.SafetyPaused, $"Mutagen 安全暂停：{reason}。历史保留，不自动 resume、不自动 reset；安全检测不是原子的，不承诺零丢失。");
        }
        if (session.SafeConflictPaths.Length > 0)
        {
            var hidden = session.DiscardedConflictEntries + session.ExcludedConflicts;
            return (SyncLinkState.Conflict,
                $"{session.SafeConflictPaths.Length} 个双方分歧冲突待处理（清单只读）" +
                (hidden > 0 ? $"；另有 {hidden} 个条目无法展示。" : "。"));
        }
        if (session.ConflictSectionPresent && (session.DiscardedConflictEntries > 0 || session.ExcludedConflicts > 0))
            return (SyncLinkState.Unknown, "冲突段无法完整解析且没有任何安全冲突行；按未知处理，不能当成已收敛。");
        if (session.ConnectionFailure)
            return (SyncLinkState.Offline, "远端端点连接失败（本次 list 成功，beta 未连接）；会话与历史保留，不 terminate、不重建。");
        if (!session.BetaConnected)
            return (SyncLinkState.NotConverged, "beta 端点未连接，但输出无法确认为连接失败；不能叫已收敛。");
        if (session.TransitionProblems)
            return (SyncLinkState.NotConverged, "Mutagen 报告 Transition problems（常见原因：远程目录父目录不属于 agent 用户，无写权）；改动可能无法落回本机。");
        if (session.SyncMode == "two-way-resolved")
            return (SyncLinkState.NotConverged, "会话模式是 two-way-resolved：alpha（本机）赢得全部分歧，包括删除覆盖对方修改；同权下最高未收敛。替换需走破坏性重置的单独确认（本轮未实现）。");
        if (session.Status == "Watching for changes")
            return session.SyncMode == "two-way-safe"
                ? (SyncLinkState.Converged, "two-way-safe 会话 Watching，无转换问题、无冲突、无暂停；该次观测没有未处理问题。不代替字节比较，也不保证观测之后的新写入已到达。")
                : (SyncLinkState.NotConverged, "输出未确认同步模式为 two-way-safe；不能叫已收敛。");
        if (SyncingStatuses.Contains(session.Status))
            return (SyncLinkState.Syncing, $"本轮正在处理（{session.Status}）；无安全暂停、冲突、转换问题或离线。");
        return (SyncLinkState.NotConverged, $"状态「{session.Status}」无法确认为同步中；会话还在，但不能叫已收敛。");
    }

    /// <summary>探针失败（找不到命令/超时/非零退出/输出为空）：当前状态未知，上次观测时间保留。</summary>
    public static SyncLinkObservation FailedProbe(string error, DateTimeOffset? lastSuccessAt, SyncLinkState? lastState, DateTimeOffset now)
    {
        var history = lastSuccessAt is { } at
            ? $"上次观测 {at.ToLocalTime():HH:mm:ss}" + (lastState is { } s ? $"（上次状态 {Label(s)}）" : "") + "；不能作为当前状态。"
            : "尚无成功观测。";
        return new(SyncLinkState.Unknown, SanitizeErrorText(error) + " " + history, lastSuccessAt);
    }

    /// <summary>§4 整体标签：按七条优先级取第一条成立的条件。两条必要链路都已收敛才是全部已收敛；单链路至多单链路已收敛。</summary>
    public static string OverallLabel(IReadOnlyList<SyncLinkState> requiredLinks)
    {
        if (requiredLinks.Count == 0) return "未同步";
        if (requiredLinks.Any(s => s == SyncLinkState.SafetyPaused)) return "安全暂停";
        if (requiredLinks.Any(s => s == SyncLinkState.Conflict)) return "冲突";
        if (requiredLinks.Any(s => s == SyncLinkState.Unknown)) return "未知";
        if (requiredLinks.Any(s => s == SyncLinkState.Offline)) return "离线";
        if (requiredLinks.Any(s => s == SyncLinkState.NotConverged)) return "未收敛";
        if (requiredLinks.Any(s => s == SyncLinkState.Syncing)) return "同步中";
        return requiredLinks.Count >= 2 ? "全部已收敛" : "单链路已收敛";
    }

    /// <summary>§5 冲突清单聚合：最多 50 行；剩余 = 被截断的安全行 + mutagen 排除行；截断不改变整体冲突。</summary>
    public static SyncConflictList BuildConflictList(IReadOnlyList<(ParsedMutagenSession Session, string LinkLabel, string AlphaRole, string BetaRole)> links)
    {
        var rows = new List<SyncConflictRow>();
        var discarded = 0;
        var excluded = 0;
        foreach (var (session, linkLabel, alphaRole, betaRole) in links)
        {
            discarded += session.DiscardedConflictEntries;
            excluded += session.ExcludedConflicts;
            foreach (var path in session.SafeConflictPaths) rows.Add(new(path, linkLabel, alphaRole, betaRole));
        }
        var shown = rows.Take(MaxConflictRows).ToArray();
        return new(shown, rows.Count - shown.Length + excluded, discarded);
    }

    /// <summary>§4 脱敏：去地址、用户名、密钥路径与远程路径；保留 agent-launcher- 会话名（主机卡片名由 UI 从配置补充，不来自错误文本）。</summary>
    public static string SanitizeErrorText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = Regex.Replace(text, @"ssh://[^\s]+", "ssh://[已脱敏]");
        result = Regex.Replace(result, @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+", "[已脱敏]");
        result = Regex.Replace(result, @"\b\d{1,3}(?:\.\d{1,3}){3}\b", "[地址已脱敏]");
        result = Regex.Replace(result, @"\b[A-Za-z]:\\[^\s]*", "[路径已脱敏]");
        result = Regex.Replace(result, @"\\\\[^\s]+", "[路径已脱敏]");
        result = Regex.Replace(result, @"(?:/[\w.-]+){2,}", "[路径已脱敏]");
        return result;
    }

    /// <summary>§6/R41：None 与 SSHFS 都不是已收敛。None 按 2026-11-19 更晚决定（IMPLEMENTATION §92）是拒收文案。
    /// attemptAlive=最近尝试 Started/Reattaching（SSHFS 启动 fail-closed，活着即挂载成功）；attemptFailed=最近尝试 FailedBeforeStart。</summary>
    public static (string Label, string Detail) NonMutagenSyncBadge(WorkspaceSyncMode mode, bool attemptAlive, bool attemptFailed)
    {
        if (mode == WorkspaceSyncMode.None)
            return ("未同步", "该主机同步模式为 none：这次不是同权。三台同权要求 mutagen 或 sshfs，none 的保存与启动已被拒收；远端 agent 的改动不会落回本机。");
        if (attemptAlive)
            return ("已挂载", "SSHFS 挂载成功；文件经挂载点直接读写。这是挂载提示，不是已收敛，更不是三台总线；京云的改动不会经 SSHFS 传到阿里云。");
        if (attemptFailed)
            return ("挂载失败", "SSHFS 挂载失败，该次启动已被阻止（fail-closed）；不是已收敛。请检查 WinFsp/SSHFS-Win 与远程目录。");
        return ("未挂载", "尚无成功的 SSHFS 挂载尝试；不是已收敛。挂载在远程启动时建立。");
    }
}
```

- [ ] **Step 4: 运行原生套件，确认通过**

Run: `dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release`
Expected: 新增 `R34/R36/R38/R39/R40/R41/R43` 各 PASS，既有全部 PASS，`RESULT … 0 failed`，退出码 0。

- [ ] **Step 5: 把既有 --sync-review-checks 接入门禁脚本**

修改 `scripts/test.ps1` 第 5-6 行之后（原生套件与 TerminalModel 之间）插入：

```powershell
& dotnet run --project (Join-Path $root 'tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj') -c Release -- --sync-review-checks
if($LASTEXITCODE -ne 0){throw 'Sync review checks failed'}
```

Run: `powershell -NoProfile -Command "dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -- --sync-review-checks"`
Expected: `RESULT 7 passed, 0 failed`（现有 UX12 检查数），退出码 0。

- [ ] **Step 6: Commit**

```bash
git add src/AgentLauncher.Core/SyncReliability.cs tests/AgentLauncher.Tests/SyncReliabilityChecks.cs tests/AgentLauncher.Tests/Program.cs scripts/test.ps1
git commit -m "Add sync reliability core state machine and mutagen list parsing"
```

---

### Task 2: two-way-safe 创建参数 + 探针 --long + 会话匹配助手 + 生命周期护栏

**Files:**
- Modify: `src/AgentLauncher.Windows/WorkspaceSync.cs:87-100`（create 参数抽取为 `BuildCreateArguments`，模式改 two-way-safe）
- Modify: `src/AgentLauncher.Windows/WorkspaceInsights.cs:125-139`（list 参数 --long）、`:142-164`（匹配抽取）、`:197-207`（SplitSessions 委托 Core）
- Test: `tests/AgentLauncher.Tests/SyncReliabilityChecks.cs`（追加）

**Interfaces:**
- Consumes: Task 1 的 `SyncReliability.SplitSessionBlocks/ParseSession/ParsedMutagenSession`；现有 `WorkspaceSync.MutagenEndpoint(RemoteExecutionSpec,string)`（internal）。
- Produces:
  ```csharp
  // AgentLauncher.Windows.WorkspaceSync（internal，InternalsVisibleTo AgentLauncher.Tests 已存在）
  internal static string[] BuildCreateArguments(WorkspaceSyncSpec spec, RemoteExecutionSpec remote, string sessionName);
  // AgentLauncher.Windows.WorkspaceInsights
  internal static string[] BuildListArguments(string? sessionName);
  public static (string Name, ParsedMutagenSession Session)? FindSyncSession(
      string listOutput, string localPath, string remoteHost, string remotePath, string? sessionName = null);
  ```
  `IWorkspaceSyncSession` 接口**不变**（仍只有 SessionName/WaitReadyAsync/GetRemoteWorkingDirectory/TryDisposeAsync/DisposeAsync）；不新增 Reset/Pause/Resume 成员。

- [ ] **Step 1: 写失败测试**

在 `SyncReliabilityChecks.Run` 末尾（`test("R34 error text sanitization…")` 之后）追加：

```csharp
        test("R42 create arguments use two-way-safe and keep the exact ignore list", () =>
        {
            var spec = new WorkspaceSyncSpec(WorkspaceSyncMode.Mutagen, "D:\\fixture\\hub", "/home/agent/work", null, null);
            var remote = new RemoteExecutionSpec("jy.example.com", 22, "root", "agent", "pi", "/home/agent/work", [], null, null, spec);
            var args = AgentLauncher.Windows.WorkspaceSync.BuildCreateArguments(spec, remote, "agent-launcher-test0001");
            var modeIndex = Array.IndexOf(args, "--sync-mode");
            assert(modeIndex >= 0 && args[modeIndex + 1] == "two-way-safe", "新建会话不是 two-way-safe");
            assert(!args.Contains("two-way-resolved"), "创建参数仍含 two-way-resolved");
            // 忽略列表不扩大：--ignore-vcs + 恰好 node_modules/ bin/ obj/ 三个 --ignore。
            var ignores = args.Where((a, i) => i > 0 && args[i - 1] == "--ignore").ToArray();
            assert(args.Contains("--ignore-vcs") && ignores.SequenceEqual(new[] { "node_modules/", "bin/", "obj/" }), "忽略列表被扩大或缩小：" + string.Join(",", ignores));
            // 本地路径在前（alpha=本机），远程端点在后，名字精确。
            assert(args[^2] == "D:\\fixture\\hub" && args[^1] == "agent@jy.example.com:22:/home/agent/work", "端点顺序或形状错误：" + args[^1]);
            assert(args[0] == "sync" && args[1] == "create" && args[Array.IndexOf(args, "--name") + 1] == "agent-launcher-test0001", "create 命令形状错误");
        });

        test("R43 probe is a single read-only long list without flush reset or ssh", () =>
        {
            var all = AgentLauncher.Windows.WorkspaceInsights.BuildListArguments(null);
            assert(all.SequenceEqual(new[] { "sync", "list", "--long" }), "全量探针参数错误：" + string.Join(" ", all));
            var named = AgentLauncher.Windows.WorkspaceInsights.BuildListArguments("agent-launcher-test0001");
            assert(named.SequenceEqual(new[] { "sync", "list", "--long", "agent-launcher-test0001" }), "命名探针参数错误");
            foreach (var args in new[] { all, named })
                assert(!args.Any(a => a is "flush" or "reset" or "terminate" or "create" or "pause" or "resume"), "诊断参数混入写操作");
        });

        test("R42 sync session interface exposes no unconfirmed reset pause resume", () =>
        {
            var methods = typeof(AgentLauncher.Windows.IWorkspaceSyncSession).GetMethods().Select(m => m.Name).ToArray();
            assert(!methods.Any(n => n is "Reset" or "Pause" or "Resume"), "会话接口暴露了未确认的 reset/pause/resume：" + string.Join(",", methods));
        });

        test("R34 FindSyncSession matches by name then endpoints and parses mode", () =>
        {
            var jy = Session("agent-launcher-jy0001", "Two Way Safe", "Watching for changes");
            var aliyun = Session("agent-launcher-aly0002", "Two Way Safe", "Watching for changes").Replace("jy.example.com", "aly.example.com");
            var output = jy + aliyun;
            var named = AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "jy.example.com", "/home/agent/work", "agent-launcher-jy0001");
            assert(named is { Name: "agent-launcher-jy0001" } && named.Value.Session.SyncMode == "two-way-safe", "命名匹配失败");
            var byEndpoint = AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "aly.example.com", "/home/agent/work");
            assert(byEndpoint is { Name: "agent-launcher-aly0002" }, "端点匹配失败");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "D:\\fixture\\hub", "jy.example.com", "/home/agent/work", "agent-launcher-missing") == null, "不存在的名字被匹配");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession("", "D:\\fixture\\hub", "jy.example.com", "/home/agent/work") == null, "空 list 匹配到会话");
            assert(AgentLauncher.Windows.WorkspaceInsights.FindSyncSession(output, "", "jy.example.com", "/home/agent/work") == null, "缺本地路径未拒绝");
        });
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release`
Expected: 编译失败 `CS0117: WorkspaceSync 未包含 BuildCreateArguments`（及 BuildListArguments/FindSyncSession 同类错误）。

- [ ] **Step 3: 实现 Windows 层改动**

3a. `WorkspaceSync.cs`：把 `MutagenSyncSession.StartAsync` 中第 87-101 行的 `var createArgs = new[] {...};` 替换为调用新助手，并在 `WorkspaceSync` 静态类内（`StartAsync` 之后）新增：

```csharp
        // 创建参数（纯函数，便于回归）：三台同权新建会话用 two-way-safe——双方修改同一路径时保留冲突，
        // 不自动选边（规格 2026-11-18 §5/§7）。已存在的 two-way-resolved 会话不批量迁移；
        // 解析到该模式时当前状态最高未收敛（SyncReliability 状态机）。忽略列表保持现状，不扩大。
        internal static string[] BuildCreateArguments(WorkspaceSyncSpec spec, RemoteExecutionSpec remote, string sessionName) =>
        [
            "sync", "create",
            "--name", sessionName,
            "--sync-mode", "two-way-safe",
            "--ignore-vcs",  // 忽略 .git 目录
            "--ignore", "node_modules/",
            "--ignore", "bin/",
            "--ignore", "obj/",
            "--default-file-mode", "0664",
            "--default-directory-mode", "0775",
            spec.LocalPath,
            MutagenSyncSession.MutagenEndpoint(remote, spec.RemotePath)
        ];
```

`StartAsync` 内改为：

```csharp
        await RunMutagenCommandAsync(WorkspaceSync.BuildCreateArguments(spec, remote, sessionName), cancellation);
```

（删除原 `createArgs` 数组与其中「冲突时保留两个版本」的错误注释；`WaitReadyAsync` 的 2 分钟判据、`TryDisposeAsync` 的 flush 失败保留会话行为**一字不改**。）

3b. `WorkspaceInsights.cs`：

在 `ListMutagenSessionsAsync` 上方新增：

```csharp
    /// <summary>诊断探针参数：一次只读 list。--long 必需——默认输出不含同步模式，冲突只有计数（mutagen 0.18.1 实测）。</summary>
    internal static string[] BuildListArguments(string? sessionName) =>
        sessionName is null ? ["sync", "list", "--long"] : ["sync", "list", "--long", sessionName];
```

`ListMutagenSessionsAsync` 内第 129 行改为：

```csharp
            var args = BuildListArguments(sessionName);
```

（10s 超时、512KiB 上限、错误文案不变。）

把 `EvaluateSync` 的匹配循环抽取为私有 `MatchSessionBlock`，`EvaluateSync` 与新的 `FindSyncSession` 共用。`EvaluateSync` 改为：

```csharp
    public static SyncHealth EvaluateSync(string listOutput,string localPath,string remoteHost,string remotePath,string? sessionName=null)
    {
        if(string.IsNullOrWhiteSpace(localPath)||string.IsNullOrWhiteSpace(remotePath))
            return new(SyncHealthState.NotSynced,"","缺少本地或远程工作目录，无法核对同步会话；请先在「详情」中填写远程工作目录。");
        string local;
        try{local=Path.GetFullPath(localPath).TrimEnd('\\','/');}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return new(SyncHealthState.NotSynced,"","本地工作目录路径无效。");}
        var matched=MatchSessionBlock(listOutput,local,remoteHost,remotePath,sessionName);
        if(matched is not {} m)return new(SyncHealthState.NotSynced,"","未找到匹配的 Mutagen 同步会话（可能尚未启动或已终止）；远端改动不会落回本机。");
        return EvaluateBlock(m.Name,m.Block);
    }

    /// <summary>同步可靠性入口：按名字（优先）或端点匹配会话块并交给 Core 解析。无匹配/无法解析返回 null，由调用方决定回退或未知。</summary>
    public static (string Name,ParsedMutagenSession Session)? FindSyncSession(string listOutput,string localPath,string remoteHost,string remotePath,string? sessionName=null)
    {
        if(string.IsNullOrWhiteSpace(localPath)||string.IsNullOrWhiteSpace(remotePath))return null;
        string local;
        try{local=Path.GetFullPath(localPath).TrimEnd('\\','/');}
        catch(Exception e)when(e is ArgumentException or NotSupportedException or PathTooLongException){return null;}
        if(MatchSessionBlock(listOutput,local,remoteHost,remotePath,sessionName) is not {} m)return null;
        var parsed=SyncReliability.ParseSession(m.Block);
        return parsed is null?null:(m.Name,parsed);
    }

    // 原 EvaluateSync 的匹配规则原样保留：名字精确优先；否则本地路径+主机+远程目录（~/ 尾段容忍）匹配，启动器前缀优先。
    private static (string Name,string Block)? MatchSessionBlock(string listOutput,string local,string remoteHost,string remotePath,string? sessionName)
    {
        var remote=remotePath.TrimEnd('/');
        var remoteTail=remote.StartsWith("~/",StringComparison.Ordinal)?remote[2..]:null;
        string? fallbackName=null,fallbackBlock=null;
        foreach(var block in SplitSessions(listOutput))
        {
            var name=Field(block,"Name:");
            if(sessionName is not null){if(!string.Equals(name,sessionName,StringComparison.Ordinal))continue;}
            if(!block.Contains(local,StringComparison.OrdinalIgnoreCase))continue;
            if(!block.Contains(remoteHost,StringComparison.OrdinalIgnoreCase))continue;
            if(!block.Contains(remote,StringComparison.Ordinal)&&(remoteTail is null||!block.Contains(remoteTail,StringComparison.Ordinal)))continue;
            if(sessionName is not null||name.StartsWith(WorkspaceSync.SessionNamePrefix,StringComparison.Ordinal))return(name,block);
            fallbackName??=name;fallbackBlock??=block;
        }
        return fallbackBlock is not null?(fallbackName!,fallbackBlock):null;
    }
```

`SplitSessions` 改为委托 Core（规则同一，DRY）：

```csharp
    // mutagen sync list 用整行短横线分隔会话；单会话时可能没有分隔线。规则在 Core（SyncReliability）共用。
    private static List<string> SplitSessions(string output)=>SyncReliability.SplitSessionBlocks(output).ToList();
```

- [ ] **Step 4: 运行受影响套件，确认通过**

Run:
```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -- --sync-review-checks
```
Expected: 原生全 PASS（含新增 4 条）；UX12 的 B3 命名匹配/端点匹配回归全 PASS（匹配规则未变）。

再跑远程集成（覆盖 reattach 的 fake-sync 路径与 EvaluateSync 调用方）：
```powershell
dotnet publish tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
dotnet publish src/AgentLauncher.Worker/AgentLauncher.Worker.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-remote-'+[guid]::NewGuid().ToString('N'))
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --remote-execution-integration
```
Expected: 0 失败（R33 等全 PASS），退出码 0。

- [ ] **Step 5: Commit**

```bash
git add src/AgentLauncher.Windows/WorkspaceSync.cs src/AgentLauncher.Windows/WorkspaceInsights.cs tests/AgentLauncher.Tests/SyncReliabilityChecks.cs
git commit -m "Create two-way-safe sync sessions and probe mutagen with long list"
```

---

### Task 3: FakeSyncSimulator 传播模拟器 + R35/R36/R37/R38 集成

**Files:**
- Create: `src/AgentLauncher.Windows/FakeSyncSimulator.cs`（整文件 `#if INTEGRATION_TESTS`）
- Modify: `src/AgentLauncher.Windows/WorkspaceSync.cs:350-366`（FakeSyncSession 路由）
- Create: `tests/AgentLauncher.Tests/SyncReliabilityIntegration.cs`
- Modify: `tests/AgentLauncher.Tests/Program.cs:43` 附近（分发 `--sync-reliability-integration`）
- Modify: `scripts/test.ps1:26-28`（套件数组与参数映射）

**Interfaces:**
- Consumes: Task 1 `SyncReliability.ParseSession/EvaluateLink/OverallLabel/BuildConflictList`；Task 2 `WorkspaceInsights.FindSyncSession`；现有 `WorkspaceSync.StartAsync`、`IWorkspaceSyncSession`、`Store.SyncSessionPath/SyncFinalPath`、`ResourcePolicy.SnapshotFor/PolicyHash/PlanDigest`、`Ipc.EnsureMonitorAsync/SendAsync`、fake-ssh（`hold=<秒>`）。
- Produces:
  ```csharp
  // AgentLauncher.Windows，#if INTEGRATION_TESTS，internal（InternalsVisibleTo AgentLauncher.Tests）
  internal sealed record FakeSyncLink
  {
      public string SessionName { get; init; } = "";
      public string LinkLabel { get; init; } = "";
      public string AlphaPath { get; init; } = "";
      public string BetaPath { get; init; } = "";
      public bool Online { get; init; } = true;
      public bool Paused { get; init; }
      public string? Halted { get; init; }   // "root deletion" / "one-sided root emptying" / "root type change" / 其他
  }
  internal sealed record FakeSyncScenario
  {
      public int PropagationDelayMs { get; init; } = 0;
      public bool FlushFails { get; init; }
      public string CallLog { get; init; } = "";
      public List<FakeSyncLink> Links { get; init; } = [];
  }
  internal static class FakeSyncSimulator
  {
      internal static void Reset();                                  // 测试隔离：清空状态（不删日志文件）
      internal static string Register(string sessionName, string alphaPath, string betaPath); // 记 create；启动泵
      internal static bool IsRegistered(string sessionName);
      internal static void Tick();                                   // 一轮传播（确定性驱动；泵每 200ms 自动调用）
      internal static bool HasPending(string sessionName);
      internal static string[] Conflicts(string sessionName);
      internal static string RenderList();                           // mutagen --long 形状；记 list
      internal static string? Flush(string sessionName);             // 记 flush；FlushFails→错误且保留；成功→强制收敛+记 terminate+注销
      internal static Task WaitReadyAsync(string sessionName, CancellationToken cancellation);
  }
  ```
  场景文件由 `AGENT_LAUNCHER_FAKE_SYNC_SCENARIO` 指向（JSON，属性名大小写不敏感）；每次 Tick/RenderList/Flush 重新读文件，测试通过改写 JSON 切换 online/paused/halted/flushFails。调用日志是 JSONL：`{"op":"create|list|flush|terminate","session":"...","at":"..."}`。未设场景变量时 FakeSyncSession 保持现有即时成功行为（RemoteE2EIntegration/RemoteReattachIntegration 不受影响）。

- [ ] **Step 1: 写失败测试（新集成套件）**

创建 `tests/AgentLauncher.Tests/SyncReliabilityIntegration.cs`（完整文件；R42 用例在 Task 4 追加到同一 `RunAsync`）：

```csharp
using System.Diagnostics;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

// 同步可靠性传播层（R35/R36/R37/R38/R42）：FakeSyncSimulator 用三个本地目录模拟本机/京云/阿里云，
// 不打开真实 VPS。120 秒观察窗只覆盖 ≤4KiB、不在忽略规则内的夹具文件（规格 §8）。
internal static class SyncReliabilityIntegration
{
    public static async Task<int> RunAsync()
    {
        var testRoot = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") ?? "";
        if (!testRoot.Contains("AgentLauncher-tests-") || Directory.Exists(testRoot))
            throw new IOException("请使用全新隔离的 AgentLauncher-tests- 测试目录（AGENT_LAUNCHER_TEST_ROOT）。");
        if (Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SAMPLE") != "1")
            throw new IOException("同步可靠性集成需要 AGENT_LAUNCHER_FAKE_SAMPLE=1（Worker 驱动用例）。");
        Directory.CreateDirectory(testRoot);
        var evidence = new List<object>();
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
        async Task Test(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS " + name); evidence.Add(new { name, passed = true }); }
            catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e.Message); evidence.Add(new { name, passed = false, error = e.ToString() }); }
        }

        var hub = Path.Combine(testRoot, "hub");
        var jy = Path.Combine(testRoot, "jy-fixture");
        var aly = Path.Combine(testRoot, "aly-fixture");
        foreach (var dir in new[] { hub, jy, aly }) Directory.CreateDirectory(dir);
        var scenarioPath = Path.Combine(testRoot, "fake-sync-scenario.json");
        var callLog = Path.Combine(testRoot, "fake-sync-calls.jsonl");
        var jyName = "agent-launcher-fake-jy-" + Guid.NewGuid().ToString("N");
        var alyName = "agent-launcher-fake-aly-" + Guid.NewGuid().ToString("N");

        void WriteScenario(bool jyOnline, bool alyOnline, bool flushFails = false, bool jyPaused = false, string? jyHalted = null, string? jyAlpha = null) =>
            File.WriteAllText(scenarioPath, JsonSerializer.Serialize(new FakeSyncScenario
            {
                PropagationDelayMs = 0,
                FlushFails = flushFails,
                CallLog = callLog,
                Links =
                [
                    new FakeSyncLink { SessionName = jyName, LinkLabel = "本机—京云", AlphaPath = jyAlpha ?? hub, BetaPath = jy, Online = jyOnline, Paused = jyPaused, Halted = jyHalted },
                    new FakeSyncLink { SessionName = alyName, LinkLabel = "本机—阿里云", AlphaPath = hub, BetaPath = aly, Online = alyOnline },
                ],
            }));
        string[] Calls() => File.Exists(callLog)
            ? File.ReadAllLines(callLog).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("op").GetString()!).ToArray()
            : [];
        string[] CallsFor(string session) => File.Exists(callLog)
            ? File.ReadAllLines(callLog).Where(l => l.Length > 0)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("session").GetString() == session)
                .Select(e => e.GetProperty("op").GetString()!).ToArray()
            : [];

        RemoteExecutionSpec Remote(string host, string local, string remoteDir, WorkspaceSyncSpec sync, string[]? arguments = null) =>
            new(host, 22, "root", "agent", "pi", remoteDir, arguments ?? [], null, null, sync);
        WorkspaceSyncSpec Spec(string name, string local, string remoteDir) =>
            new(WorkspaceSyncMode.Mutagen, local, remoteDir, name, null);

        // 120 秒观察窗（规格 §8）：轮询直到条件成立或超时；超时即失败，不放宽。
        async Task Within120Seconds(Func<bool> condition, string why)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(120))
            {
                FakeSyncSimulator.Tick();
                if (condition()) return;
                await Task.Delay(100);
            }
            throw new Exception("120 秒观察窗超时：" + why);
        }
        byte[] Fixture(string text) => System.Text.Encoding.UTF8.GetBytes(text); // ≤4KiB
        bool SameBytes(string path, byte[] expected) => File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(expected);

        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC", "1");
        Environment.SetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC_SCENARIO", scenarioPath);
        WriteScenario(true, true);
        FakeSyncSimulator.Reset();

        await Test("R35 three-way small file propagation within 120 seconds", async () =>
        {
            // 不用 await using：会话必须保留到 R42 的显式 flush 用例。提前 Dispose 会 flush+terminate，
            // 既让后续用例失去注册会话，也污染「离线/恢复不 terminate」的调用日志断言。
            var jySession = await WorkspaceSync.StartAsync(Spec(jyName, hub, jy), Remote("jy.example.com", hub, jy, Spec(jyName, hub, jy)));
            var alySession = await WorkspaceSync.StartAsync(Spec(alyName, hub, aly), Remote("aly.example.com", hub, aly, Spec(alyName, hub, aly)));
            await jySession.WaitReadyAsync();
            await alySession.WaitReadyAsync();
            Assert(jySession.SessionName == jyName && alySession.SessionName == alyName, "会话名与场景不一致");

            var fromHub = Fixture("hub writes R35\n");
            var fromJy = Fixture("jingyun writes R35\n");
            var fromAly = Fixture("aliyun writes R35\n");
            await File.WriteAllBytesAsync(Path.Combine(hub, "hub-r35.txt"), fromHub);
            await File.WriteAllBytesAsync(Path.Combine(jy, "jy-r35.txt"), fromJy);
            await File.WriteAllBytesAsync(Path.Combine(aly, "aly-r35.txt"), fromAly);
            await Within120Seconds(() =>
                SameBytes(Path.Combine(jy, "hub-r35.txt"), fromHub) && SameBytes(Path.Combine(aly, "hub-r35.txt"), fromHub) &&
                SameBytes(Path.Combine(hub, "jy-r35.txt"), fromJy) && SameBytes(Path.Combine(aly, "jy-r35.txt"), fromJy) &&
                SameBytes(Path.Combine(hub, "aly-r35.txt"), fromAly) && SameBytes(Path.Combine(jy, "aly-r35.txt"), fromAly),
                "三端未读到相同字节");
            // 两条链路此时都可判已收敛，整体才是全部已收敛（R34 的传播层佐证）。
            var now = DateTimeOffset.UtcNow;
            var blocks = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList());
            var states = blocks.Select(b => SyncReliability.ParseSession(b)).Where(p => p != null)
                .Select(p => SyncReliability.EvaluateLink(p!, now, now).State).ToArray();
            Assert(states.Length == 2 && states.All(s => s == SyncLinkState.Converged), "传播完成后链路不是已收敛：" + string.Join(",", states));
            Assert(SyncReliability.OverallLabel(states) == "全部已收敛", "双健康链路整体标签错误");
            // 忽略规则内的文件不传播（忽略列表不扩大的反向证明）。
            Directory.CreateDirectory(Path.Combine(hub, "node_modules"));
            await File.WriteAllBytesAsync(Path.Combine(hub, "node_modules", "ignored.txt"), Fixture("ignored"));
            FakeSyncSimulator.Tick(); await Task.Delay(300); FakeSyncSimulator.Tick();
            Assert(!File.Exists(Path.Combine(jy, "node_modules", "ignored.txt")), "忽略规则内文件被传播");
        });

        await Test("R36 both sides write same path different bytes keeps conflict and bytes", async () =>
        {
            // 先建立共同祖先，再双方改同一路径。
            var baseline = Fixture("base R36\n");
            await File.WriteAllBytesAsync(Path.Combine(hub, "conflict-r36.txt"), baseline);
            await Within120Seconds(() => SameBytes(Path.Combine(jy, "conflict-r36.txt"), baseline) && SameBytes(Path.Combine(aly, "conflict-r36.txt"), baseline), "祖先文件未传播");
            var jyBytes = Fixture("jingyun version R36\n");
            var alyBytes = Fixture("aliyun version R36\n");
            // 京云链路上双方分歧：先冻结两条链路（离线开关即冻结传播），hub 与 jy 同时改同一路径，再恢复。
            WriteScenario(false, false);
            await File.WriteAllBytesAsync(Path.Combine(hub, "conflict-r36.txt"), Fixture("hub version R36\n"));
            await File.WriteAllBytesAsync(Path.Combine(jy, "conflict-r36.txt"), jyBytes);
            WriteScenario(true, true);
            await Within120Seconds(() => FakeSyncSimulator.Conflicts(jyName).Contains("conflict-r36.txt"), "双方修改未产生冲突");
            Assert(SameBytes(Path.Combine(jy, "conflict-r36.txt"), jyBytes), "冲突后京云字节被覆盖");
            Assert(SameBytes(Path.Combine(hub, "conflict-r36.txt"), Fixture("hub version R36\n")), "冲突后本机字节被覆盖");
            var now = DateTimeOffset.UtcNow;
            var parsed = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList())
                .Select(SyncReliability.ParseSession).First(p => p?.Name == jyName)!;
            var obs = SyncReliability.EvaluateLink(parsed, now, now);
            Assert(obs.State == SyncLinkState.Conflict, "冲突链路状态错误：" + obs.State);
            var list = SyncReliability.BuildConflictList([(parsed, "本机—京云", "本机", "京云")]);
            Assert(list.Rows.Any(r => r.RelativePath == "conflict-r36.txt" && r.LinkLabel == "本机—京云"), "清单缺少相对路径与受影响会话");
            Assert(SyncReliability.OverallLabel([obs.State, SyncLinkState.Converged]) == "冲突", "整体不是冲突");
            // 消解方式（规格 §5/§9）：删掉应失败的一端后，另一端覆盖。本轮不实现按钮，用文件操作验证语义。
            File.Delete(Path.Combine(jy, "conflict-r36.txt"));
            await Within120Seconds(() => SameBytes(Path.Combine(jy, "conflict-r36.txt"), Fixture("hub version R36\n")) && !FakeSyncSimulator.Conflicts(jyName).Contains("conflict-r36.txt"), "删除一端后冲突未消解");
            // 阿里云链路同路径分歧同样成立（独立会话独立冲突）：同样冻结后让 hub 与 aly 都相对基线改过。
            WriteScenario(false, false);
            await File.WriteAllBytesAsync(Path.Combine(hub, "conflict-r36.txt"), Fixture("hub version 2 R36\n"));
            await File.WriteAllBytesAsync(Path.Combine(aly, "conflict-r36.txt"), alyBytes);
            WriteScenario(true, true);
            await Within120Seconds(() => FakeSyncSimulator.Conflicts(alyName).Contains("conflict-r36.txt"), "阿里云链路双方修改未产生冲突");
            Assert(SameBytes(Path.Combine(aly, "conflict-r36.txt"), alyBytes), "阿里云冲突字节被覆盖");
            File.Delete(Path.Combine(aly, "conflict-r36.txt"));
            await Within120Seconds(() => !FakeSyncSimulator.Conflicts(alyName).Contains("conflict-r36.txt"), "阿里云冲突未消解");
        });

        await Test("R37 hub offline one-sided writes wait and recover on the same session", async () =>
        {
            WriteScenario(false, false); // 枢纽离线：两条链路都断开
            var offlineFile = Fixture("jingyun offline write R37\n");
            await File.WriteAllBytesAsync(Path.Combine(jy, "offline-r37.txt"), offlineFile);
            FakeSyncSimulator.Tick(); await Task.Delay(300); FakeSyncSimulator.Tick();
            Assert(!File.Exists(Path.Combine(aly, "offline-r37.txt")) && !File.Exists(Path.Combine(hub, "offline-r37.txt")), "离线期间文件被传播");
            var now = DateTimeOffset.UtcNow;
            var offlineStates = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList())
                .Select(SyncReliability.ParseSession).Where(p => p != null)
                .Select(p => SyncReliability.EvaluateLink(p!, now, now).State).ToArray();
            Assert(offlineStates.All(s => s == SyncLinkState.Offline), "离线链路状态错误：" + string.Join(",", offlineStates));
            WriteScenario(true, true); // 恢复：不 terminate 不 create
            await Within120Seconds(() => SameBytes(Path.Combine(hub, "offline-r37.txt"), offlineFile) && SameBytes(Path.Combine(aly, "offline-r37.txt"), offlineFile), "恢复后单侧写入未传到另外两端");
            Assert(CallsFor(jyName).Count(op => op == "create") == 1 && !CallsFor(jyName).Contains("terminate"), "恢复重建了会话");
            Assert(FakeSyncSimulator.IsRegistered(jyName) && FakeSyncSimulator.IsRegistered(alyName), "恢复后会话不在");
            // 第二组：离线期间两侧云改同一路径 → 恢复后报告冲突，不自动合并。
            WriteScenario(false, false);
            var jyConflict = Fixture("jy offline conflict R37\n");
            var alyConflict = Fixture("aly offline conflict R37\n");
            await File.WriteAllBytesAsync(Path.Combine(jy, "offline-conflict-r37.txt"), jyConflict);
            await File.WriteAllBytesAsync(Path.Combine(aly, "offline-conflict-r37.txt"), alyConflict);
            WriteScenario(true, true);
            await Within120Seconds(() => FakeSyncSimulator.Conflicts(jyName).Contains("offline-conflict-r37.txt") && FakeSyncSimulator.Conflicts(alyName).Contains("offline-conflict-r37.txt"), "离线双方修改恢复后未报告冲突");
            Assert(SameBytes(Path.Combine(jy, "offline-conflict-r37.txt"), jyConflict) && SameBytes(Path.Combine(aly, "offline-conflict-r37.txt"), alyConflict), "离线冲突字节被自动合并覆盖");
            File.Delete(Path.Combine(jy, "offline-conflict-r37.txt"));
            File.Delete(Path.Combine(aly, "offline-conflict-r37.txt"));
            await Within120Seconds(() => !FakeSyncSimulator.Conflicts(jyName).Contains("offline-conflict-r37.txt"), "清理后冲突仍在");
        });

        await Test("R38 one link offline other healthy converges alone", async () =>
        {
            WriteScenario(false, true);
            var healthyFile = Fixture("aliyun healthy R38\n");
            await File.WriteAllBytesAsync(Path.Combine(aly, "healthy-r38.txt"), healthyFile);
            await Within120Seconds(() => SameBytes(Path.Combine(hub, "healthy-r38.txt"), healthyFile), "健康链路未传完自己的小型文件");
            Assert(!File.Exists(Path.Combine(jy, "healthy-r38.txt")), "离线链路收到了文件");
            var now = DateTimeOffset.UtcNow;
            var parsed = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList()).Select(SyncReliability.ParseSession).Where(p => p != null).ToArray();
            var jyObs = SyncReliability.EvaluateLink(parsed.First(p => p!.Name == jyName)!, now, now);
            var alyObs = SyncReliability.EvaluateLink(parsed.First(p => p!.Name == alyName)!, now, now);
            Assert(jyObs.State == SyncLinkState.Offline && alyObs.State == SyncLinkState.Converged, "一离线一健康状态错误");
            Assert(SyncReliability.OverallLabel([jyObs.State, alyObs.State]) != "全部已收敛", "单链路健康被写成全部已收敛");
            Assert(SyncReliability.OverallLabel([alyObs.State]) == "单链路已收敛", "单链路已收敛标签错误");
            WriteScenario(true, true);
            await Within120Seconds(() => SameBytes(Path.Combine(jy, "healthy-r38.txt"), healthyFile), "恢复后健康文件未到京云");
        });

        Console.WriteLine($"RESULT {evidence.Count} cases, {evidence.Count(e => e.GetType().GetProperty("passed")?.GetValue(e) is false)} failed. Artifacts: {testRoot}");
        File.WriteAllText(Path.Combine(testRoot, "sync-reliability-results.json"), JsonSerializer.Serialize(evidence));
        return evidence.Any(e => e.GetType().GetProperty("passed")?.GetValue(e) is false) ? 1 : 0;
    }
}
```

确定性说明：所有「双方同时写」都按 冻结（`WriteScenario(false,false)`）→ 双写 → 恢复（`WriteScenario(true,true)`）的顺序，不依赖泵线程竞态。冲突判定相对每条链路自己的基线：R36 阿里云组必须让 hub 也相对基线改过（写 "hub version 2 R36"），否则只算单侧修改会被传播而不是冲突。R37 第二组离线期间两侧云各写同一路径，恢复后无论哪侧先到枢纽，另一条链路都会看到「枢纽已变 + 本侧已变」而报告冲突；云端字节互不覆盖。清理用删除文件验证「修改赢删除」与冲突消解语义（规格 §5/§9），本轮不提供界面按钮。

- [ ] **Step 2: 接线并运行确认失败**

`tests/AgentLauncher.Tests/Program.cs` 第 43 行 `--sync-review-checks` 分发之后加：

```csharp
if(args is ["--sync-reliability-integration"])return await SyncReliabilityIntegration.RunAsync();
```

`scripts/test.ps1` 第 26 行套件数组加 `'sync-reliability-integration'`，第 28 行特殊映射列表加同名：

```powershell
  foreach($suite in @('admission-integration','startup-queue','parallel-batch','terminal-batch','integration','remote-execution-integration','remote-startup-e2e','sync-reliability-integration')){
    $env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-'+$suite+'-'+[guid]::NewGuid().ToString('N'))
    $testArgument=if($suite -in @('integration','admission-integration','remote-execution-integration','remote-startup-e2e','sync-reliability-integration')){'--'+$suite}else{'--'+$suite+'-checks'}
```

Run:
```powershell
dotnet publish tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
dotnet publish src/AgentLauncher.Worker/AgentLauncher.Worker.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-syncrel-'+[guid]::NewGuid().ToString('N'))
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --sync-reliability-integration
```
Expected: 编译失败 `CS0246: FakeSyncSimulator/FakeSyncScenario/FakeSyncLink 未找到`。

- [ ] **Step 3: 实现模拟器**

创建 `src/AgentLauncher.Windows/FakeSyncSimulator.cs`（完整文件）：

```csharp
#if INTEGRATION_TESTS
using System.Text.Json;

namespace AgentLauncher.Windows;

// 同步可靠性传播模拟器（仅 INTEGRATION_TESTS）：三个本地目录模拟本机/京云/阿里云，
// 按链路在线/离线开关做小文件复制传播；双方改同一路径→冲突且互不覆盖；list 输出由状态生成（mutagen --long 形状）；
// flush 失败开关；调用日志（create/list/flush/terminate）供断言不 terminate/create/reset。
// 场景文件由 AGENT_LAUNCHER_FAKE_SYNC_SCENARIO 指向，每次操作重读，测试改写 JSON 即可切换开关。
internal sealed record FakeSyncLink
{
    public string SessionName { get; init; } = "";
    public string LinkLabel { get; init; } = "";
    public string AlphaPath { get; init; } = "";
    public string BetaPath { get; init; } = "";
    public bool Online { get; init; } = true;
    public bool Paused { get; init; }
    public string? Halted { get; init; }
}

internal sealed record FakeSyncScenario
{
    public int PropagationDelayMs { get; init; } = 0;
    public bool FlushFails { get; init; }
    public string CallLog { get; init; } = "";
    public List<FakeSyncLink> Links { get; init; } = [];
}

internal static class FakeSyncSimulator
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, LinkState> States = [];
    private static Thread? pump;

    private sealed class LinkState
    {
        public required FakeSyncLink Link;
        public Dictionary<string, byte[]?> Baseline = [];
        public Dictionary<string, DateTimeOffset> PendingSince = [];
        public HashSet<string> Conflicts = [];
    }

    private static FakeSyncScenario Scenario()
    {
        var path = Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC_SCENARIO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new FakeSyncScenario();
        return JsonSerializer.Deserialize<FakeSyncScenario>(File.ReadAllText(path), Json) ?? new FakeSyncScenario();
    }

    private static void Log(string op, string session)
    {
        var log = Scenario().CallLog;
        if (log.Length == 0) return;
        File.AppendAllText(log, JsonSerializer.Serialize(new { op, session, at = DateTimeOffset.UtcNow }) + "\n");
    }

    internal static void Reset()
    {
        lock (Gate) { States.Clear(); }
    }

    internal static string Register(string sessionName, string alphaPath, string betaPath)
    {
        lock (Gate)
        {
            var scenario = Scenario();
            var link = scenario.Links.FirstOrDefault(l => l.SessionName == sessionName)
                ?? new FakeSyncLink { SessionName = sessionName, AlphaPath = alphaPath, BetaPath = betaPath };
            States[sessionName] = new LinkState { Link = link };
            Log("create", sessionName);
            if (pump is null || !pump.IsAlive)
            {
                pump = new Thread(() =>
                {
                    while (true)
                    {
                        lock (Gate) { if (States.Count == 0) { pump = null; return; } }
                        Tick();
                        Thread.Sleep(200);
                    }
                }) { IsBackground = true, Name = "FakeSyncSimulator" };
                pump.Start();
            }
        }
        return sessionName;
    }

    internal static bool IsRegistered(string sessionName) { lock (Gate) return States.ContainsKey(sessionName); }

    internal static bool HasPending(string sessionName) { lock (Gate) return States.TryGetValue(sessionName, out var s) && s.PendingSince.Count > 0; }

    internal static string[] Conflicts(string sessionName) { lock (Gate) return States.TryGetValue(sessionName, out var s) ? s.Conflicts.ToArray() : []; }

    // 与创建参数一致的忽略规则（不扩大）：.git/node_modules/bin/obj 任意层级目录跳过。
    private static readonly string[] IgnoredDirs = [".git", "node_modules", "bin", "obj"];

    private static Dictionary<string, byte[]?> Snapshot(string root)
    {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return result;
        void Walk(string dir, string prefix)
        {
            foreach (var file in Directory.GetFiles(dir))
                result[prefix + Path.GetFileName(file)] = File.ReadAllBytes(file);
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (IgnoredDirs.Contains(name)) continue;
                Walk(sub, prefix + name + "/");
            }
        }
        Walk(root, "");
        return result;
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);

    private static void WriteOrDelete(string root, string relative, byte[]? content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (content is null) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void Reconcile(LinkState state, int delayMs)
    {
        var link = state.Link;
        var alpha = Snapshot(link.AlphaPath);
        var beta = Snapshot(link.BetaPath);
        var now = DateTimeOffset.UtcNow;
        foreach (var path in alpha.Keys.Union(beta.Keys).ToArray())
        {
            var a = alpha.GetValueOrDefault(path);
            var b = beta.GetValueOrDefault(path);
            var basis = state.Baseline.GetValueOrDefault(path);
            if (Same(a, b))
            {
                state.Baseline[path] = a;
                state.Conflicts.Remove(path);
                state.PendingSince.Remove(path);
                continue;
            }
            var aChanged = !Same(a, basis);
            var bChanged = !Same(b, basis);
            if (aChanged && bChanged)
            {
                // two-way-safe 的无数据丢失自动处理：一侧删除、另一侧修改 → 修改赢（规格 §5 引用的模式行为）。
                if (a is null && b is not null && basis is not null)
                {
                    WriteOrDelete(link.AlphaPath, path, b);
                    state.Baseline[path] = b;
                    state.Conflicts.Remove(path);
                }
                else if (b is null && a is not null && basis is not null)
                {
                    WriteOrDelete(link.BetaPath, path, a);
                    state.Baseline[path] = a;
                    state.Conflicts.Remove(path);
                }
                else
                {
                    state.Conflicts.Add(path); // 双方修改/新建不同内容：冲突，互不覆盖，不自动选边。
                }
                state.PendingSince.Remove(path);
                continue;
            }
            var winner = aChanged ? a : b;
            var target = aChanged ? link.BetaPath : link.AlphaPath;
            if (state.PendingSince.TryGetValue(path, out var since) && (now - since).TotalMilliseconds >= delayMs)
            {
                WriteOrDelete(target, path, winner);
                state.Baseline[path] = winner;
                state.PendingSince.Remove(path);
                state.Conflicts.Remove(path);
            }
            else state.PendingSince.TryAdd(path, now);
        }
        // 清理根删除后的基线残留（两侧都不存在的路径）。
        foreach (var path in state.Baseline.Keys.Where(p => !alpha.ContainsKey(p) && !beta.ContainsKey(p)).ToArray())
            state.Baseline.Remove(path);
    }

    internal static void Tick()
    {
        lock (Gate)
        {
            var scenario = Scenario();
            foreach (var state in States.Values)
            {
                var link = scenario.Links.FirstOrDefault(l => l.SessionName == state.Link.SessionName) ?? state.Link;
                state.Link = link;
                if (!link.Online || link.Paused || link.Halted is not null) continue;
                Reconcile(state, scenario.PropagationDelayMs);
            }
        }
    }

    internal static string RenderList()
    {
        lock (Gate)
        {
            Log("list", "*");
            var scenario = Scenario();
            var text = "";
            foreach (var state in States.Values)
            {
                var link = scenario.Links.FirstOrDefault(l => l.SessionName == state.Link.SessionName) ?? state.Link;
                var status = link.Halted is { } halted ? "Halted due to " + halted
                    : link.Paused ? "[Paused]"
                    : !link.Online ? "Connecting to beta"
                    : state.PendingSince.Count > 0 ? "Reconciling changes"
                    : "Watching for changes";
                text += "-------------------------------------------------------------------------------\n";
                text += $"Name: {link.SessionName}\n";
                text += $"Identifier: sync_fake{link.SessionName}\n";
                text += "Configuration:\n\tSynchronization mode: Two Way Safe\n";
                text += $"Alpha:\n\tURL: {link.AlphaPath}\n\tConnected: Yes\n";
                text += $"Beta:\n\tURL: {link.BetaPath}\n\tConnected: {(link.Online ? "Yes" : "No")}\n";
                foreach (var path in state.Conflicts.OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (path == state.Conflicts.OrderBy(p => p, StringComparer.Ordinal).First()) text += "Conflicts:\n";
                    text += $"\t(alpha) {path} (<non-existent> -> File (fakealpha))\n";
                    text += $"\t(beta)  {path} (<non-existent> -> File (fakebeta))\n";
                }
                if (!link.Online)
                    text += "Last error: unable to connect to beta: unable to connect to endpoint: dial tcp: connection refused (fake offline)\n";
                text += $"Status: {status}\n";
                text += "-------------------------------------------------------------------------------\n";
            }
            return text;
        }
    }

    internal static string? Flush(string sessionName)
    {
        lock (Gate)
        {
            Log("flush", sessionName);
            if (!States.TryGetValue(sessionName, out var state)) return null;
            if (Scenario().FlushFails)
                return $"工作区同步收敛失败（fake flush 失败开关）：会话 {sessionName} 已保留，请手动检查。";
            Reconcile(state, 0);
            Reconcile(state, 0); // 枢纽链两段：再跑一轮让对侧链路拿到刚收敛的内容。
            Log("terminate", sessionName);
            States.Remove(sessionName);
            return null;
        }
    }

    internal static async Task WaitReadyAsync(string sessionName, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (!States.TryGetValue(sessionName, out var state)) throw new IOException($"fake 同步会话不存在：{sessionName}");
                var link = Scenario().Links.FirstOrDefault(l => l.SessionName == sessionName) ?? state.Link;
                if (!link.Online) throw new TimeoutException($"fake 同步链路离线，无法就绪：{sessionName}");
            }
            Tick();
            if (!HasPending(sessionName)) return;
            await Task.Delay(50, cancellation);
        }
        throw new TimeoutException($"fake 同步就绪超时：{sessionName}");
    }
}
#endif
```

修改 `WorkspaceSync.cs` 的 `FakeSyncSession`（第 350-366 行）为：

```csharp
#if INTEGRATION_TESTS
internal sealed class FakeSyncSession : IWorkspaceSyncSession
{
    private readonly string? _name;
    private readonly string _remotePath;
    private readonly bool _simulated;
    public FakeSyncSession(WorkspaceSyncSpec spec)
    {
        _name = spec.Mode == WorkspaceSyncMode.Mutagen ? spec.MutagenSessionName ?? $"{WorkspaceSync.SessionNamePrefix}fake{Guid.NewGuid():N}" : null;
        _remotePath = spec.RemotePath;
        // 未设场景变量时保持原有即时成功行为（R33/E2E 回归不受影响）；设了则路由到传播模拟器。
        _simulated = _name is not null && Environment.GetEnvironmentVariable("AGENT_LAUNCHER_FAKE_SYNC_SCENARIO") is { Length: > 0 };
        if (_simulated) FakeSyncSimulator.Register(_name!, spec.LocalPath, spec.RemotePath);
    }
    public string? SessionName => _name;
    public Task WaitReadyAsync(CancellationToken cancellation) => _simulated ? FakeSyncSimulator.WaitReadyAsync(_name!, cancellation) : Task.CompletedTask;
    public string GetRemoteWorkingDirectory() => _remotePath;
    public async ValueTask DisposeAsync() { if (_simulated) await TryDisposeAsync(); }
    public Task<string?> TryDisposeAsync() => _simulated ? Task.FromResult(FakeSyncSimulator.Flush(_name!)) : Task.FromResult<string?>(null);
}
#endif
```

- [ ] **Step 4: 运行新套件与既有 fake-sync 套件，确认通过**

Run（每次用全新测试根）:
```powershell
dotnet publish tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
dotnet publish src/AgentLauncher.Worker/AgentLauncher.Worker.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-syncrel-'+[guid]::NewGuid().ToString('N'))
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --sync-reliability-integration
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-e2e-'+[guid]::NewGuid().ToString('N'))
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --remote-startup-e2e
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-remote-'+[guid]::NewGuid().ToString('N'))
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --remote-execution-integration
```
Expected: 新套件 R35/R36/R37/R38 全 PASS（120s 窗内，实际通常 <5s）；E2E 与远程集成 0 失败（未设场景变量时 FakeSyncSession 行为不变）。

- [ ] **Step 5: Commit**

```bash
git add src/AgentLauncher.Windows/FakeSyncSimulator.cs src/AgentLauncher.Windows/WorkspaceSync.cs tests/AgentLauncher.Tests/SyncReliabilityIntegration.cs tests/AgentLauncher.Tests/Program.cs scripts/test.ps1
git commit -m "Add scriptable fake sync simulator and propagation acceptance R35-R38"
```

---

### Task 4: R42 生命周期断言 + R41 None 拒收回归

**Files:**
- Modify: `tests/AgentLauncher.Tests/SyncReliabilityIntegration.cs`（追加 R42 用例）
- Modify: `src/AgentLauncher.Core/RemoteHostEditor.cs`（新增 `ValidateSyncMode`）
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs:1093`（EditHost 改用共享文案，逐字节不变）
- Test: `tests/AgentLauncher.Tests/SyncReliabilityChecks.cs`（追加 R41/R42 纯函数用例）

**Interfaces:**
- Consumes: Task 3 模拟器全部 API；现有 `WorkspacePreparer.PrepareAsync`（`WorkspacePreparer.cs:124`）、`Store.SyncFinalPath/SyncSessionPath`、fake-ssh `hold=<秒>`、`Ipc.EnsureMonitorAsync/SendAsync`、`RemoteExecutionSpec`。
- Produces:
  ```csharp
  // AgentLauncher.Core.RemoteHostEditor
  public static string? ValidateSyncMode(WorkspaceSyncMode mode);
  // None → "同步模式不能选 none：不同步时各机只看见自己的目录，改动不落回本机，不构成三台同权。请选择 mutagen 或 sshfs。"（与现有 EditHost 内联文案逐字节一致）
  // Mutagen/SshfsMount → null
  ```

- [ ] **Step 1: 写失败测试（R42 集成 + R41/R42 纯函数）**

1a. 在 `SyncReliabilityIntegration.RunAsync` 的 R38 用例之后、`Console.WriteLine($"RESULT …")` 之前追加（沿用同一 hub/jy/aly/scenarioPath/callLog/WriteScenario/Calls/CallsFor/Remote/Spec/Within120Seconds 局部函数；R35–R38 结束后两条会话仍注册）：

```csharp
        await Test("R42 offline pause and probe failure never terminate create or reset", async () =>
        {
            var before = Calls();
            // 普通离线：切换开关 + 多轮 list，不 terminate 不 create。
            WriteScenario(false, false);
            FakeSyncSimulator.Tick();
            _ = FakeSyncSimulator.RenderList();
            // 暂停（场景开关模拟操作员 pause 后的状态；本轮产品无 pause 按钮）：同样不 terminate 不 create。
            WriteScenario(true, true, jyPaused: true);
            _ = FakeSyncSimulator.RenderList();
            // 安全暂停（halted）：保持 halted，不自动 resume/reset。
            WriteScenario(true, true, jyHalted: "root deletion");
            _ = FakeSyncSimulator.RenderList();
            // 探针失败：纯函数层评估为未知；产品没有任何因探针失败而写会话的路径。
            var failed = SyncReliability.FailedProbe("mutagen sync list 超时（10 秒），已终止。", DateTimeOffset.UtcNow, SyncLinkState.Converged, DateTimeOffset.UtcNow);
            Assert(failed.State == SyncLinkState.Unknown, "探针失败不是未知");
            WriteScenario(true, true);
            var after = Calls();
            Assert(!after.Contains("terminate") && !after.Contains("reset"), "离线/暂停/探针失败触发了 terminate/reset");
            Assert(after.Count(op => op == "create") == before.Count(op => op == "create"), "离线/暂停/探针失败重建了会话");
            Assert(CallsFor(jyName).SequenceEqual(new[] { "create" }) || CallsFor(jyName).All(op => op is "create" or "list"), "京云会话出现意外写操作：" + string.Join(",", CallsFor(jyName)));
            Assert(FakeSyncSimulator.IsRegistered(jyName) && FakeSyncSimulator.IsRegistered(alyName), "会话被悄悄移除");
            // halted 状态解析为安全暂停（R40 的传播层佐证）。
            WriteScenario(true, true, jyHalted: "root deletion");
            var now = DateTimeOffset.UtcNow;
            var halted = SyncReliability.SplitSessionBlocks(FakeSyncSimulator.RenderList()).Select(SyncReliability.ParseSession).First(p => p?.Name == jyName)!;
            Assert(SyncReliability.EvaluateLink(halted, now, now).State == SyncLinkState.SafetyPaused, "模拟器 halted 不是安全暂停");
            WriteScenario(true, true);
        });

        await Test("R42 flush failure keeps session in process", async () =>
        {
            WriteScenario(true, true, flushFails: true);
            var session = await WorkspaceSync.StartAsync(Spec(jyName, hub, jy), Remote("jy.example.com", hub, jy, Spec(jyName, hub, jy)));
            // 同名会话已注册：Register 幂等覆盖状态，但 create 会再记一次——断言按「flush 后无 terminate」判定。
            var error = await session.TryDisposeAsync();
            Assert(error is not null && error.Contains("已保留"), "flush 失败未返回可读保留错误：" + error);
            Assert(FakeSyncSimulator.IsRegistered(jyName), "flush 失败后会话被终止");
            Assert(!CallsFor(jyName).Contains("terminate"), "flush 失败仍 terminate");
            Assert(CallsFor(jyName).Contains("flush"), "flush 未被调用");
            WriteScenario(true, true);
            var ok = await session.TryDisposeAsync();
            Assert(ok is null && !FakeSyncSimulator.IsRegistered(jyName), "flush 成功后未正常终止");
        });

        await Test("R42 worker exit with flush failure writes problem receipt and keeps session evidence", async () =>
        {
            // Worker 驱动：fake-ssh hold=2 正常退出 → Worker flush（场景开关失败）→ sync-final problem + sync-session 保留。
            // 调用日志截断：只断言 Worker 进程自己的操作（前面用例的 create/flush/terminate 不属于本条）。
            if (File.Exists(callLog)) File.Delete(callLog);
            var fakeSshDir = AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(fakeSshDir, "ssh.exe"))) throw new IOException("fake SSH not found: " + fakeSshDir);
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", fakeSshDir + Path.PathSeparator + originalPath);
            var workerProcesses = new List<Process>();
            try
            {
                var cwd = Path.Combine(Store.Root, "r42-cwd"); Directory.CreateDirectory(cwd);
                var authorityConfig = new Configuration { SchemaVersion = 2 };
                authorityConfig.Draft.SchemaVersion = 2; authorityConfig.Draft.Revision = 3;
                authorityConfig.Draft.Project = cwd; authorityConfig.Draft.DirectoryMode = "isolated";
                authorityConfig.Draft.Slots = [new Slot { Enabled = true, AgentId = "pi" }];
                Store.Save(Store.FilePath("configuration.json"), authorityConfig);
                var initial = await Ipc.EnsureMonitorAsync();
                Assert(initial.Ok, "Monitor start failed: " + initial.Message);
                var plan = new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), 1, "r42-flush", cwd, PhysicalDirectory.Workspace(cwd), false,
                    new CommandSpec("pi", ["hold=2"], "remote"), 0,
                    RemoteExecution: Remote("jy.example.com", cwd, jy, Spec(jyName, cwd, jy), ["hold=2"]));
                var policy = ResourcePolicy.SnapshotFor(new ResourceSettings(), 16 * ResourcePolicy.GiB, 32 * ResourcePolicy.GiB);
                var reserved = await Ipc.SendAsync(new("reserve", Guid.NewGuid(), [plan], Policy: policy, PolicyHash: ResourcePolicy.PolicyHash(policy), PlanDigest: ResourcePolicy.PlanDigest([plan]), DraftRevision: 3));
                Assert(reserved.Ok, "reserve failed: " + reserved.Message);
                WriteScenario(true, true, flushFails: true, jyAlpha: cwd);
                var start = new ProcessStartInfo(Store.WorkerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("pane"); start.ArgumentList.Add(plan.Id.ToString());
                start.Environment["AGENT_LAUNCHER_TEST_ROOT"] = Store.Root;
                start.Environment["PATH"] = fakeSshDir + Path.PathSeparator + originalPath;
                start.Environment["AGENT_LAUNCHER_FAKE_SYNC"] = "1";
                start.Environment["AGENT_LAUNCHER_FAKE_SYNC_SCENARIO"] = scenarioPath;
                var worker = Process.Start(start)!;
                worker.StandardInput.Close();
                workerProcesses.Add(worker);
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(60) && !File.Exists(Store.SyncFinalPath(plan.Id))) await Task.Delay(200);
                Assert(File.Exists(Store.SyncFinalPath(plan.Id)), "Worker 未写 sync-final 回执");
                var final = Store.Read<SyncFinalRecord>(Store.SyncFinalPath(plan.Id))!;
                Assert(final.SchemaVersion == 1 && final.State == SyncFinalRecord.Problem, "flush 失败回执不是 problem：" + final.State);
                Assert(final.Detail.Contains("已保留"), "problem 回执未说明会话保留");
                Assert(File.Exists(Store.SyncSessionPath(plan.Id)), "flush 失败删掉了 sync-session.json");
                var sessionRecord = Store.Read<SyncSessionRecord>(Store.SyncSessionPath(plan.Id))!;
                Assert(sessionRecord.Name == jyName, "会话名证据丢失");
                Assert(!CallsFor(jyName).Contains("terminate"), "Worker flush 失败仍 terminate");
                Assert(!Calls().Contains("reset"), "出现未确认的 reset");
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                foreach (var w in workerProcesses) { try { if (!w.HasExited) w.Kill(true); } catch { } w.Dispose(); }
                Environment.SetEnvironmentVariable("PATH", originalPath);
                WriteScenario(true, true);
            }
        });
```

说明：Worker 用例的 `Spec(jyName, cwd, jy)` 以 `cwd` 为 LocalPath，`WriteScenario` 的 `jyAlpha` 参数（Step 1 已定义）把场景京云链路的 AlphaPath 指到同一 `cwd`，模拟器 Register 按会话名匹配场景链路，两端一致。`RemoteExecution.Arguments` 必须是 `["hold=2"]`（Worker 的远端命令取自 RemoteExecution，fake-ssh 靠 hold 参数存活 2 秒后正常退出触发 flush）。用例结束在 finally 里 `WriteScenario(true, true)` 恢复。Worker 进程内的模拟器与测试进程各自独立注册会话，调用日志是同一文件，故用例开头截断日志。

1b. 在 `SyncReliabilityChecks.Run` 末尾追加：

```csharp
        test("R41 None save and launch are rejected with readable equal-rights copy", () =>
        {
            // 保存侧共享文案（EditHost 使用同一函数；偏差：规格 §6 的「None 继续允许」已被 IMPLEMENTATION §92 拒收决定取代）。
            var error = RemoteHostEditor.ValidateSyncMode(WorkspaceSyncMode.None);
            assert(error is not null && error.Contains("同权") && error.Contains("mutagen") && error.Contains("sshfs"), "None 保存拒收文案缺少同权与改法：" + error);
            assert(RemoteHostEditor.ValidateSyncMode(WorkspaceSyncMode.Mutagen) == null && RemoteHostEditor.ValidateSyncMode(WorkspaceSyncMode.SshfsMount) == null, "合法模式被拒收");
            // 启动侧：WorkspacePreparer 对 None 主机拒收（与 RemoteExecutionChecks 的 F09/R23 断言同源，R41 独立复证）。
            var temp = Path.Combine(Path.GetTempPath(), "AgentLauncher-tests-r41-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var host = new RemoteHost(Guid.NewGuid(), "R41 VPS", "r41.example.com", 22, "root", "agent", ["pi"], null, WorkspaceSyncMode.None);
                var configuration = new Configuration { SchemaVersion = 2, RemoteHosts = [host] };
                var draft = new Draft
                {
                    SchemaVersion = 2,
                    Project = temp,
                    DirectoryMode = "isolated",
                    Slots = [new Slot { Enabled = true, AgentId = "pi", RemoteHostId = host.Id, RemoteDirectory = "/home/agent/work" }]
                };
                var preparer = new AgentLauncher.Windows.WorkspacePreparer(Path.Combine(temp, "worktrees"));
                var rejected = false;
                try { preparer.PrepareAsync(draft, _ => { }, _ => Task.FromResult(false), default, configuration: configuration).GetAwaiter().GetResult(); }
                catch (IOException e) { rejected = e.Message.Contains("同权"); }
                assert(rejected, "None 主机启动未被拒收或错误缺少同权说明");
            }
            finally { Directory.Delete(temp, recursive: true); }
        });
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release`
Expected: 编译失败 `CS0117: RemoteHostEditor 未包含 ValidateSyncMode`。

- [ ] **Step 3: 实现共享文案并替换内联字符串**

`src/AgentLauncher.Core/RemoteHostEditor.cs` 追加：

```csharp
    // 三台同权（2026-11-18 产品决定；2026-11-19 拒收实现见 IMPLEMENTATION §92）：None 保存拒收文案。
    // 主机编辑器与测试共用同一条边界；启动准备层的拒收在 WorkspacePreparer（文案含「同权」）。
    public static string? ValidateSyncMode(WorkspaceSyncMode mode) =>
        mode == WorkspaceSyncMode.None
            ? "同步模式不能选 none：不同步时各机只看见自己的目录，改动不落回本机，不构成三台同权。请选择 mutagen 或 sshfs。"
            : null;
```

`src/AgentLauncher.App/MainWindow.xaml.cs:1093` 把：

```csharp
            if(syncMode==WorkspaceSyncMode.None)error="同步模式不能选 none：不同步时各机只看见自己的目录，改动不落回本机，不构成三台同权。请选择 mutagen 或 sshfs。";
```

替换为（文案逐字节不变，仅来源共享；保留原来「None 错误覆盖先前错误」的语义，不用 `??=`）：

```csharp
            if(syncMode==WorkspaceSyncMode.None)error=RemoteHostEditor.ValidateSyncMode(syncMode)!;
```

- [ ] **Step 4: 运行受影响套件，确认通过**

Run:
```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
dotnet publish tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
dotnet publish src/AgentLauncher.Worker/AgentLauncher.Worker.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-syncrel-'+[guid]::NewGuid().ToString('N'))
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --sync-reliability-integration
```
Expected: 原生全 PASS（含 R41）；集成 R35–R38 + R42 三条全 PASS，退出码 0。

- [ ] **Step 5: Commit**

```bash
git add tests/AgentLauncher.Tests/SyncReliabilityIntegration.cs tests/AgentLauncher.Tests/SyncReliabilityChecks.cs src/AgentLauncher.Core/RemoteHostEditor.cs src/AgentLauncher.App/MainWindow.xaml.cs
git commit -m "Assert sync lifecycle guardrails and None rejection copy (R41/R42)"
```

---

### Task 5: UI 链路徽章 + 上次观测 + 同步详情对话框 + UiChecks

**Files:**
- Modify: `src/AgentLauncher.App/MainWindow.Practical.cs:24-30`（缓存字段）、`:36`（timer tick）、`:123-152`（WrapSlotSource/ApplySyncBadge/RefreshSlotSyncHealthAsync）、`:154-195`（探针重构）
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs:432`（菜单追加「同步详情…」）
- Modify: `src/AgentLauncher.App/UiChecks.cs`（UX12 同步审阅块之后追加）

**Interfaces:**
- Consumes: Task 1 `SyncReliability.*`（含 `NonMutagenSyncBadge`）、Task 2 `WorkspaceInsights.FindSyncSession`、现有 `LatestRemoteEntryForSlot`、`ResolveTerminalSync`、`Store.SyncSessionPath`、`WorkspaceInsights.ReadSyncEvidence`、`SyncSessionRecord`、`RemoteHostForSlot`、`UiScale.Apply`。
- Produces（MainWindow partial，internal 供 UiChecks 驱动）:
  ```csharp
  internal async Task ApplySyncListToBadgesAsync(string? output, bool ok, string error, DateTimeOffset observedAt);
  internal void ShowSyncDetails(Guid slotId);
  ```
  徽章 AutomationProperties 名保持 `$"同步状态 {slot.DisplayName}"`；详情对话框控件名：`"同步整体标签"`（TextBlock）、`"同步链路列表"`（ListBox）、`"同步冲突清单"`（ListBox）、`"同步冲突剩余计数"`（TextBlock）；关闭按钮设 `DialogResult=true`（§89 教训：IsCancel 的 RaiseEvent 不关窗，检查点击设 DialogResult 的按钮）。

- [ ] **Step 1: 写失败 UI 检查**

在 `UiChecks.cs` 现有 UX12 同步审阅块结束处（B1 diff 对话框检查之后、同一 `check(...)` 小写局部函数作用域内）追加：

```csharp
            // ---- 同步可靠性 R34/R39/R40/R41/R36：链路徽章 + 上次观测 + 只读冲突清单 ----
            var jyHost=new RemoteHost(Guid.NewGuid(),"京云夹具","jy.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.Mutagen);
            var alyHost=new RemoteHost(Guid.NewGuid(),"阿里云夹具","aly.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.Mutagen);
            var sshfsHost=new RemoteHost(Guid.NewGuid(),"SSHFS 夹具","sshfs.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.SshfsMount);
            var noneHost=new RemoteHost(Guid.NewGuid(),"None 遗留","none.example.com",22,"root","agent",["pi"],null,WorkspaceSyncMode.None);
            config=new Configuration();config.RemoteHosts=[jyHost,alyHost,sshfsHost,noneHost];
            config.Edit(d=>{d.Project=repo;
                d.Slots[0].Enabled=true;d.Slots[0].Directory=repo;d.Slots[0].RemoteHostId=jyHost.Id;d.Slots[0].RemoteDirectory="/home/agent/work";
                d.Slots[1].Enabled=true;d.Slots[1].Directory=repo;d.Slots[1].RemoteHostId=alyHost.Id;d.Slots[1].RemoteDirectory="/home/agent/work";
                d.Slots[2].Enabled=false;d.Slots[2].Directory=repo;d.Slots[2].RemoteHostId=sshfsHost.Id;d.Slots[2].RemoteDirectory="/home/agent/work";
                d.Slots[3].Enabled=false;d.Slots[3].Directory=repo;d.Slots[3].RemoteHostId=noneHost.Id;d.Slots[3].RemoteDirectory="/home/agent/work";
                foreach(var s in d.Slots.Skip(4))s.Enabled=false;});
            Render();
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
            TextBlock Badge(int i)=>FindLogical<TextBlock>(this).First(t=>AutomationProperties.GetName(t)==$"同步状态 {config.Draft.Slots[i].DisplayName}");
            string RelSession(string name,string mode,string status,string betaConnected="Yes",string conflicts="",string lastError="")=>
                "-------------------------------------------------------------------------------\n"+
                $"Name: {name}\nIdentifier: sync_fake{name}\nConfiguration:\n\tSynchronization mode: {mode}\n"+
                $"Alpha:\n\tURL: {repo}\n\tConnected: Yes\nBeta:\n\tURL: ssh://agent@jy.example.com:22/home/agent/work\n\tConnected: {betaConnected}\n"+
                conflicts+(lastError.Length>0?$"Last error: {lastError}\n":"")+$"Status: {status}\n"+
                "-------------------------------------------------------------------------------\n";
            var jyAttempt=Guid.NewGuid();var alyAttempt=Guid.NewGuid();
            var jyPlan=new LaunchItem(jyAttempt,Guid.NewGuid(),1,"r34-jy",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[0].Id,RemoteExecution:new RemoteExecutionSpec(jyHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            var alyPlan=new LaunchItem(alyAttempt,Guid.NewGuid(),2,"r34-aly",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[1].Id,RemoteExecution:new RemoteExecutionSpec(alyHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            status=new(true,"sync reliability probe",[new ControlEntry{Plan=jyPlan,State="Started"},new ControlEntry{Plan=alyPlan,State="Started"}]);
            var jyName="agent-launcher-"+Guid.NewGuid().ToString("N");var alyName="agent-launcher-"+Guid.NewGuid().ToString("N");
            Store.Save(Store.SyncSessionPath(jyAttempt),new SyncSessionRecord(1,jyName,repo,"/home/agent/work",DateTimeOffset.UtcNow));
            Store.Save(Store.SyncSessionPath(alyAttempt),new SyncSessionRecord(1,alyName,repo,"/home/agent/work",DateTimeOffset.UtcNow));
            string ConflictBlock(string path)=>"Conflicts:\n\t(alpha) "+path+" (<non-existent> -> File (aa))\n\t(beta)  "+path+" (<non-existent> -> File (bb))\n";
            // R34：两条链路都 Watching+two-way-safe → 各自已收敛，详情含上次观测；整体全部已收敛。
            var bothOk=RelSession(jyName,"Two Way Safe","Watching for changes")+RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com");
            await ApplySyncListToBadgesAsync(bothOk,true,"",DateTimeOffset.UtcNow);
            check("R34 both healthy links show 已收敛 with last observation",Badge(0).Text=="同步：已收敛"&&Badge(1).Text=="同步：已收敛"&&Badge(0).ToolTip!.ToString()!.Contains("上次观测"));
            // 同步详情对话框：整体标签 + 逐链路 + 只读冲突清单（无处理按钮）。
            var detailsSeen=false;
            var detailsInspect=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            detailsInspect.Tick+=(_,_)=>{
                if(detailsSeen)return;
                var window=OwnedWindows.OfType<Window>().FirstOrDefault(w=>w.Title=="同步详情");
                if(window is null)return;
                detailsSeen=true;detailsInspect.Stop();
                _=Dispatcher.InvokeAsync(async()=>{
                    try
                    {
                        var overall=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步整体标签");
                        var links=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步链路列表");
                        var conflicts=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步冲突清单");
                        var remaining=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步冲突剩余计数");
                        check("R34 details dialog shows overall 全部已收敛 and per-link observation",overall.Text=="全部已收敛"&&links.Items.Count>=2&&links.Items.OfType<ListBoxItem>().Any(i=>((string)i.Content).Contains("上次观测")));
                        check("R36 conflict list is read-only with no action buttons",conflicts.IsEnabled&&FindLogical<Button>(window).All(b=>(string)b.Content=="关闭"));
                        check("R36 empty conflict list shows zero remainder",remaining.Text.Contains("0"));
                        var close=FindLogical<Button>(window).Single(b=>(string)b.Content=="关闭");
                        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(100);
                        check("R34 details dialog closes via DialogResult button",!OwnedWindows.OfType<Window>().Any(w=>w.Title=="同步详情"));
                    }
                    catch(Exception e){check("R34 details dialog inspection failed: "+e.Message,false);try{window.Close();}catch{}}
                });
            };
            detailsInspect.Start();
            try{ShowSyncDetails(config.Draft.Slots[0].Id);}finally{detailsInspect.Stop();}
            check("R34 details dialog opened for inspection",detailsSeen);
            // R39：观测超过 30 秒 → 未知并保留上次观测；探针失败 → 未知（非终态条目不回退 sync-final）。
            await ApplySyncListToBadgesAsync(bothOk,true,"",DateTimeOffset.UtcNow.AddSeconds(-31));
            check("R39 stale observation shows 未知 with last observation",Badge(0).Text=="同步：未知"&&Badge(0).ToolTip!.ToString()!.Contains("上次观测"));
            await ApplySyncListToBadgesAsync(null,false,"mutagen sync list 超时（10 秒），已终止。",DateTimeOffset.UtcNow);
            check("R39 probe failure shows 未知 not 未同步",Badge(0).Text=="同步：未知"&&Badge(1).Text=="同步：未知");
            // R39：命名会话在输出里但无法匹配/解析 → fail-closed 未知，不回退成未同步。
            await ApplySyncListToBadgesAsync($"Name: {jyName}\ngarbage\nName: {alyName}\ngarbage\n",true,"",DateTimeOffset.UtcNow);
            check("R39 unparsable named sessions show 未知",Badge(0).Text=="同步：未知");
            // R40：安全暂停徽章与原因文案。
            var halted=RelSession(jyName,"Two Way Safe","Halted due to root deletion")+RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com");
            await ApplySyncListToBadgesAsync(halted,true,"",DateTimeOffset.UtcNow);
            check("R40 root deletion shows 安全暂停 badge",Badge(0).Text=="同步：安全暂停"&&Badge(0).ToolTip!.ToString()!.Contains("根被删除"));
            // R36：冲突徽章 + 详情清单 50 行截断与剩余计数（60 条冲突 → 50 行 + 剩余 10）。
            var manyConflicts="Conflicts:\n"+string.Concat(Enumerable.Range(1,60).Select(i=>$"\t(alpha) f{i:D3}.txt (<non-existent> -> File (aa))\n\t(beta)  f{i:D3}.txt (<non-existent> -> File (bb))\n"));
            var conflicted=RelSession(jyName,"Two Way Safe","Watching for changes",conflicts:manyConflicts)+RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com");
            await ApplySyncListToBadgesAsync(conflicted,true,"",DateTimeOffset.UtcNow);
            check("R36 conflict shows 冲突 badge",Badge(0).Text=="同步：冲突");
            var conflictSeen=false;
            var conflictInspect=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
            conflictInspect.Tick+=(_,_)=>{
                if(conflictSeen)return;
                var window=OwnedWindows.OfType<Window>().FirstOrDefault(w=>w.Title=="同步详情");
                if(window is null)return;
                conflictSeen=true;conflictInspect.Stop();
                _=Dispatcher.InvokeAsync(async()=>{
                    try
                    {
                        var overall=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步整体标签");
                        var conflicts=FindLogical<ListBox>(window).Single(l=>AutomationProperties.GetName(l)=="同步冲突清单");
                        var remaining=FindLogical<TextBlock>(window).Single(t=>AutomationProperties.GetName(t)=="同步冲突剩余计数");
                        check("R36 conflict list caps at 50 rows with remaining 10 and overall 冲突",overall.Text=="冲突"&&conflicts.Items.Count==50&&remaining.Text.Contains("10"));
                        check("R36 conflict rows carry relative path and link label",conflicts.Items.OfType<ListBoxItem>().All(i=>((string)i.Content).Contains("本机—京云")));
                        var close=FindLogical<Button>(window).Single(b=>(string)b.Content=="关闭");
                        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch(Exception e){check("R36 conflict dialog inspection failed: "+e.Message,false);try{window.Close();}catch{}}
                });
            };
            conflictInspect.Start();
            try{ShowSyncDetails(config.Draft.Slots[0].Id);}finally{conflictInspect.Stop();}
            check("R36 conflict details dialog opened for inspection",conflictSeen);
            // R41：启用 SSHFS/None 槽位后按最近尝试状态显示挂载提示与拒收文案（都不是已收敛）。
            var sshfsAttempt=Guid.NewGuid();var noneAttempt=Guid.NewGuid();
            var sshfsPlan=new LaunchItem(sshfsAttempt,Guid.NewGuid(),3,"r41-sshfs",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[2].Id,RemoteExecution:new RemoteExecutionSpec(sshfsHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            var nonePlan=new LaunchItem(noneAttempt,Guid.NewGuid(),4,"r41-none",repo,PhysicalDirectory.Workspace(repo),false,new("pi",[],"remote"),0,SourceSlotId:config.Draft.Slots[3].Id,RemoteExecution:new RemoteExecutionSpec(noneHost.Address,22,"root","agent","pi","/home/agent/work",[],null,null));
            status=new(true,"sync reliability probe",[new ControlEntry{Plan=jyPlan,State="Started"},new ControlEntry{Plan=alyPlan,State="Started"},new ControlEntry{Plan=sshfsPlan,State="Started"},new ControlEntry{Plan=nonePlan,State="FailedBeforeStart"}]);
            config.Edit(d=>{d.Slots[2].Enabled=true;d.Slots[3].Enabled=true;});
            Render();
            await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
            check("R41 SSHFS alive shows 已挂载 not convergence",Badge(2).Text=="同步：已挂载"&&Badge(2).ToolTip!.ToString()!.Contains("不是已收敛"));
            check("R41 None shows rejection copy",Badge(3).Text=="同步：未同步"&&Badge(3).ToolTip!.ToString()!.Contains("拒收"));
            // A1 回归：会话已终止（list 无该会话）时终态条目仍回退 sync-final，新状态机不吞掉旧回退链。
            Store.Save(Store.SyncFinalPath(jyAttempt),new SyncFinalRecord(1,SyncFinalRecord.Synced,"Mutagen flush 成功，会话已终止。",DateTimeOffset.UtcNow));
            status=new(true,"sync reliability probe",[new ControlEntry{Plan=jyPlan,State="Settled"},new ControlEntry{Plan=alyPlan,State="Started"},new ControlEntry{Plan=sshfsPlan,State="Started"},new ControlEntry{Plan=nonePlan,State="FailedBeforeStart"}]);
            await ApplySyncListToBadgesAsync(RelSession(alyName,"Two Way Safe","Watching for changes").Replace("jy.example.com","aly.example.com"),true,"",DateTimeOffset.UtcNow);
            check("A1 terminal fallback preserved under link badges",Badge(0).Text=="同步：已同步"&&Badge(1).Text=="同步：已收敛");
```

（插入位置说明：以上整段追加在 `RunSyncReviewUiChecksAsync` 的 `check("UX12 B1 review left worktree untouched",…)` 之后、方法 `finally` 之前；`check`/`FindLogical`/`repo`/`config`/`status`/`Render` 均沿用该方法现有局部环境。既有 `finally` 会恢复 originalConfig/originalStatus，无需新增清理。SSHFS/None 槽位初始必须禁用，否则首个详情对话框的整体标签会被 NotConverged 拉成「未收敛」，R34 的「全部已收敛」断言不成立。）

- [ ] **Step 2: 运行确认失败**

Run:
```powershell
dotnet publish src/AgentLauncher.App/AgentLauncher.App.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
```
Expected: 编译失败 `CS0117: MainWindow 未包含 ApplySyncListToBadgesAsync / ShowSyncDetails 的定义`。

- [ ] **Step 3: 实现链路徽章、新鲜度刷新与同步详情对话框**

全部改动在 `src/AgentLauncher.App/MainWindow.Practical.cs`（除菜单一行在 `MainWindow.xaml.cs`）。

3a. 缓存字段：在第 30 行 `private readonly Dictionary<Guid,SyncHealth> syncHealthCache=[];` 之后追加：

```csharp
    // 同步可靠性（规格 §4）：链路观测缓存。syncLinkObservedAt/syncLastState 在回退后仍保留，供「上次观测」与未知详情使用。
    private readonly Dictionary<Guid,SyncLinkObservation> syncLinkCache=[];
    private readonly Dictionary<Guid,ParsedMutagenSession> syncParsedCache=[];
    private readonly Dictionary<Guid,DateTimeOffset> syncLinkObservedAt=[];
    private readonly Dictionary<Guid,SyncLinkState> syncLastState=[];
```

3b. 定时器 tick（第 36 行）改为（5s 重渲染缓存徽章，让 30s 新鲜度在不发新 list 的情况下也能到期降级；list 仍 15s 节流）：

```csharp
        memoryTimer.Tick+=(_,_)=>{UpdateMemoryBoard();_=ProbeSyncHealthAsync();RefreshCachedSyncBadges();};
```

3c. `WrapSlotSource` 的三个模式分支（第 130-132 行）替换为：

```csharp
        if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen)RefreshNonMutagenBadge(slot,host,sync);
        else if(syncLinkCache.TryGetValue(slot.Id,out var link))ApplyLinkBadge(sync,link,syncParsedCache.TryGetValue(slot.Id,out var parsed)?parsed.Name:"");
        else ApplySyncBadge(slot.Id,sync,syncHealthCache.GetValueOrDefault(slot.Id)??new(SyncHealthState.NotSynced,"","尚未检查；点菜单「刷新同步状态」或等待自动刷新（约 15 秒一次）。"),cache:false);
```

3d. 在 `ApplySyncBadge` 之后新增徽章助手与缓存重渲染：

```csharp
    // None/SSHFS 徽章（§6/R41）：按最近尝试状态给挂载提示/拒收文案；永远不声称已收敛。
    private void RefreshNonMutagenBadge(Slot slot,RemoteHost host,TextBlock badge)
    {
        var entry=LatestRemoteEntryForSlot(slot.Id);
        var alive=entry?.State is "Started" or "Reattaching";
        var failed=entry?.State=="FailedBeforeStart";
        var (label,detail)=SyncReliability.NonMutagenSyncBadge(host.WorkspaceSyncMode,alive,failed);
        ApplyTextBadge(badge,label,detail,host.WorkspaceSyncMode==WorkspaceSyncMode.SshfsMount&&alive?"Muted":failed?"StatusBad":"StatusOff");
    }
    private static void ApplyTextBadge(TextBlock badge,string label,string detail,string brushKey)
    {
        badge.Text="同步："+label;
        badge.SetResourceReference(TextBlock.ForegroundProperty,brushKey);
        badge.ToolTip=detail;
    }
    private void ApplyLinkBadge(TextBlock badge,SyncLinkObservation observation,string sessionName)
    {
        var brush=observation.State switch{SyncLinkState.Converged=>"StatusOk",SyncLinkState.Conflict or SyncLinkState.SafetyPaused or SyncLinkState.Offline=>"StatusBad",SyncLinkState.Syncing=>"Muted",_=>"StatusOff"};
        var observed=observation.LastObservedAt is {} at?"\n上次观测："+at.ToLocalTime().ToString("HH:mm:ss"):"\n上次观测：尚无";
        ApplyTextBadge(badge,observation.Label,observation.Detail+observed+(sessionName.Length>0?"\n会话："+sessionName:""),brush);
    }
    // 每次内存 tick 用缓存重算新鲜度：超过 30 秒的观测自动降为未知，不发新 list、不占名额。
    private void RefreshCachedSyncBadges()
    {
        foreach(var (slotId,parsed) in syncParsedCache)
        {
            if(!syncBadges.TryGetValue(slotId,out var badge))continue;
            var observation=SyncReliability.EvaluateLink(parsed,syncLinkObservedAt[slotId],DateTimeOffset.UtcNow);
            syncLinkCache[slotId]=observation;
            ApplyLinkBadge(badge,observation,parsed.Name);
        }
    }
```

3e. `ProbeSyncHealthAsync` 的 try/catch 主体（第 162-180 行）替换为（单飞、15s 节流、targets 计算不变）：

```csharp
        try
        {
            var (ok,output,error)=await WorkspaceInsights.ListMutagenSessionsAsync(CancellationToken.None);
            await ApplySyncListToBadgesAsync(ok?output:null,ok,error,DateTimeOffset.UtcNow);
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            await ApplySyncListToBadgesAsync(null,false,"同步状态探测失败："+e.Message,DateTimeOffset.UtcNow);
        }
        finally{syncProbing=false;}
```

3f. 新增决策表方法（internal 供 UiChecks 注入 canned list 直接驱动）与两个只读助手：

```csharp
    // 同步可靠性徽章决策表（规格 §4）：list 成功且匹配到会话 → 七态评估；
    // 命名会话在输出里但无法匹配/解析 → fail-closed 未知；无会话 → A1/B3 旧回退链（终态 sync-final）；
    // list 失败 → 先走终态回退，否则未知（探针失败 ≠ 未同步）。文件读取离开 UI 线程。
    internal async Task ApplySyncListToBadgesAsync(string? output,bool ok,string error,DateTimeOffset observedAt)
    {
        var targets=config.Draft.Slots.Where(s=>s.RemoteHostId!=null&&RemoteHostForSlot(s) is {WorkspaceSyncMode:WorkspaceSyncMode.Mutagen}).ToArray();
        foreach(var slot in targets)
        {
            if(!syncBadges.TryGetValue(slot.Id,out var badge))continue;
            var host=RemoteHostForSlot(slot)!;
            var local=string.IsNullOrWhiteSpace(slot.Directory)?config.Draft.Project:slot.Directory;
            var persisted=await Task.Run(()=>PersistedSessionName(slot));
            var found=ok&&output is not null?WorkspaceInsights.FindSyncSession(output,local,host.Address,slot.RemoteDirectory??"",persisted):null;
            if(found is {} f)
            {
                var observation=SyncReliability.EvaluateLink(f.Session,observedAt,DateTimeOffset.UtcNow);
                syncLinkCache[slot.Id]=observation;syncParsedCache[slot.Id]=f.Session;syncLinkObservedAt[slot.Id]=observedAt;
                if(DateTimeOffset.UtcNow-observedAt<=TimeSpan.FromSeconds(SyncReliability.FreshnessSeconds))syncLastState[slot.Id]=observation.State;
                ApplyLinkBadge(badge,observation,f.Name);
                continue;
            }
            syncLinkCache.Remove(slot.Id);syncParsedCache.Remove(slot.Id);
            if(ok&&output is not null&&persisted is not null&&output.Contains("Name: "+persisted,StringComparison.Ordinal))
            {
                var unknown=SyncReliability.FailedProbe("mutagen sync list 输出无法解析该会话。",LastSyncObservation(slot.Id),syncLastState.TryGetValue(slot.Id,out var last)?last:null,DateTimeOffset.UtcNow);
                ApplyLinkBadge(badge,unknown,persisted);
                continue;
            }
            var health=ok&&output is not null?WorkspaceInsights.EvaluateSync(output,local,host.Address,slot.RemoteDirectory??""):new SyncHealth(SyncHealthState.NotSynced,"",error+"；视为未同步。");
            health=await Task.Run(()=>ResolveTerminalSync(health,slot,host,ok?output:null));
            if(!ok&&health.State==SyncHealthState.NotSynced)
            {
                var unknown=SyncReliability.FailedProbe(error,LastSyncObservation(slot.Id),syncLastState.TryGetValue(slot.Id,out var last)?last:null,DateTimeOffset.UtcNow);
                ApplyLinkBadge(badge,unknown,persisted??"");
                continue;
            }
            ApplySyncBadge(slot.Id,badge,health);
        }
    }
    private DateTimeOffset? LastSyncObservation(Guid slotId)=>syncLinkObservedAt.TryGetValue(slotId,out var at)?at:null;
    private string? PersistedSessionName(Slot slot)
    {
        var entry=LatestRemoteEntryForSlot(slot.Id);
        return entry is null?null:WorkspaceInsights.ReadSyncEvidence<SyncSessionRecord>(Store.SyncSessionPath(entry.Plan.Id)) is {SchemaVersion:1} session?session.Name:null;
    }
```

3g. `RefreshSlotSyncHealthAsync` 的非 Mutagen 早退分支（第 149 行）替换为（手动刷新也更新 None/SSHFS 徽章；每次点击仍只发一次 list）：

```csharp
        if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen)
        {
            if(syncBadges.TryGetValue(slot.Id,out var nonMutagen))RefreshNonMutagenBadge(slot,host,nonMutagen);
            ProgressText.Text=host.WorkspaceSyncMode==WorkspaceSyncMode.None?"该主机同步模式为 none：不构成三台同权，保存与启动已被拒收；远端改动不会落回本机。":"该主机使用 SSHFS 挂载同步，无 Mutagen 会话可查询；徽章显示挂载提示，不是已收敛。";
            return;
        }
```

3h. 新增只读「同步详情」对话框（放在 `ResolveTerminalSync` 之前；关闭按钮设 `DialogResult=true`，不用 IsCancel+RaiseEvent——§89 教训）：

```csharp
    // 同步详情（只读）：整体标签 + 逐链路状态/上次观测 + 冲突清单（≤50 行 + 剩余计数）。
    // 本轮不提供暂停/恢复、冲突处理或重置按钮（规格 §7/§10）；打开对话框不改变任何会话状态。
    internal void ShowSyncDetails(Guid slotId)
    {
        var links=new List<(string Label,SyncLinkObservation Observation,ParsedMutagenSession? Session)>();
        foreach(var slot in config.Draft.Slots.Where(s=>s.Enabled&&s.RemoteHostId!=null))
        {
            var host=RemoteHostForSlot(slot);if(host==null)continue;
            var label="本机—"+host.Name;
            if(host.WorkspaceSyncMode!=WorkspaceSyncMode.Mutagen)
            {
                var entry=LatestRemoteEntryForSlot(slot.Id);
                var (text,detail)=SyncReliability.NonMutagenSyncBadge(host.WorkspaceSyncMode,entry?.State is "Started" or "Reattaching",entry?.State=="FailedBeforeStart");
                links.Add((label,new SyncLinkObservation(SyncLinkState.NotConverged,text+"："+detail,null),null));
                continue;
            }
            if(syncLinkCache.TryGetValue(slot.Id,out _)&&syncParsedCache.TryGetValue(slot.Id,out var parsed))
                links.Add((label,SyncReliability.EvaluateLink(parsed,syncLinkObservedAt[slot.Id],DateTimeOffset.UtcNow),parsed));
            else links.Add((label,new SyncLinkObservation(SyncLinkState.Unknown,"尚未成功观测该链路；点菜单「刷新同步状态」或等待自动刷新。",LastSyncObservation(slot.Id)),null));
        }
        var overall=SyncReliability.OverallLabel(links.Select(l=>l.Observation.State).ToArray());
        var conflicts=SyncReliability.BuildConflictList(links.Where(l=>l.Session!=null).Select(l=>(l.Session!,l.Label,"本机",l.Label.Split('—')[^1])).ToArray());
        var window=new Window{Owner=this,Title="同步详情",Width=Math.Min(760,SystemParameters.WorkArea.Width-32),Height=Math.Min(640,SystemParameters.WorkArea.Height-32),WindowStartupLocation=WindowStartupLocation.CenterOwner,Style=(Style)Application.Current.Resources[typeof(Window)]};
        var root=new DockPanel{Margin=new(24)};
        var footer=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,16,0,0)};DockPanel.SetDock(footer,Dock.Bottom);
        var close=new Button{Content="关闭",IsDefault=true,MinWidth=88};close.Click+=(_,_)=>window.DialogResult=true;footer.Children.Add(close);root.Children.Add(footer);
        var header=new StackPanel{Margin=new(0,0,0,12)};DockPanel.SetDock(header,Dock.Top);
        var overallText=new TextBlock{Text=overall,FontSize=18,FontWeight=FontWeights.Bold};AutomationProperties.SetName(overallText,"同步整体标签");
        header.Children.Add(overallText);
        header.Children.Add(new TextBlock{Text="只读诊断：一次 mutagen sync list（--long），不 flush、不 reset、不新建连接。本轮不提供暂停/恢复、冲突处理或重置按钮。",TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),Margin=new(0,8,0,0)});
        root.Children.Add(header);
        var grid=new Grid();
        grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        var linkList=new ListBox();AutomationProperties.SetName(linkList,"同步链路列表");
        foreach(var (label,observation,_) in links)
            linkList.Items.Add(new ListBoxItem{Content=$"{label}：{observation.Label} · 上次观测 {(observation.LastObservedAt is {} at?at.ToLocalTime().ToString("HH:mm:ss"):"尚无")} · {observation.Detail}"});
        var conflictPanel=new DockPanel{Margin=new(0,12,0,0)};
        var remaining=new TextBlock{Text=$"冲突清单：显示 {conflicts.Rows.Length} 行，剩余 {conflicts.RemainingCount} 条未显示"+(conflicts.DiscardedCount>0?$"；另有 {conflicts.DiscardedCount} 个条目无法展示（路径不安全或输出只有计数）。":"。"),TextWrapping=TextWrapping.Wrap,Foreground=(Brush)FindResource("Muted"),Margin=new(0,0,0,8)};
        AutomationProperties.SetName(remaining,"同步冲突剩余计数");DockPanel.SetDock(remaining,Dock.Top);conflictPanel.Children.Add(remaining);
        var conflictList=new ListBox();AutomationProperties.SetName(conflictList,"同步冲突清单");
        foreach(var row in conflicts.Rows)conflictList.Items.Add(new ListBoxItem{Content=$"{row.RelativePath} · {row.LinkLabel} · {row.AlphaRole} ↔ {row.BetaRole}"});
        if(conflicts.Rows.Length==0)conflictList.Items.Add(new ListBoxItem{Content="没有可展示的冲突。",IsEnabled=false});
        conflictPanel.Children.Add(conflictList);
        Grid.SetRow(linkList,0);Grid.SetRow(conflictPanel,1);grid.Children.Add(linkList);grid.Children.Add(conflictPanel);
        root.Children.Add(grid);window.Content=root;UiScale.Apply(window);
        window.ShowDialog();
    }
```

3i. 文件头 F3 注释（第 18-21 行）更新为：

```csharp
// F3 同步状态：VPS 槽位的每链路七态徽章（安全暂停/冲突/未知/离线/未收敛/同步中/已收敛，规格 2026-11-18 §4）+ 上次观测时间；
//    30 秒新鲜度由 5s 内存 tick 重渲染缓存降级，list 仍 ~15 秒节流、单次只读 mutagen sync list --long 覆盖全部槽位；
//    菜单「刷新同步状态」按需刷新、「同步详情…」只读展示整体标签/逐链路/冲突清单（≤50 行 + 剩余计数）；
//    A1 终态回退：会话终止后按 attempts/<id>/sync-final.json 显示「已同步」/「同步有问题」（旧回退链保留）；
//    B3 名字持久化：按 attempts/<id>/sync-session.json 的会话名匹配 mutagen list（重启后恢复）；
//    None/SSHFS 徽章按最近尝试状态给拒收文案/挂载提示，永远不声称已收敛（R41）。
```

3j. `src/AgentLauncher.App/MainWindow.xaml.cs` 第 432 行 `Menu("刷新同步状态",…)` 之后追加一行（末尾追加，既有菜单项索引不变）：

```csharp
            Menu("同步详情…",()=>Owner.ShowSyncDetails(slot.Id),Remote);
```

- [ ] **Step 4: 运行 UI 检查与受影响套件，确认通过**

Run（UI 调用方式与 `scripts/test.ps1` 的 UI 段一致：独立测试根、`--ui-check` + 报告目录；App 的 ui-check 模式自带 fake sample，不额外设 `AGENT_LAUNCHER_FAKE_SAMPLE`）:
```powershell
dotnet publish src/AgentLauncher.App/AgentLauncher.App.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
dotnet publish tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
dotnet publish src/AgentLauncher.Worker/AgentLauncher.Worker.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime-r34 --nologo -m:1
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-ui-'+[guid]::NewGuid().ToString('N'))
& artifacts/test-runtime-r34/AgentLauncher.App.exe --ui-check artifacts/ui-r34
```
Expected: 退出码 0；`artifacts/ui-r34/ui-checks.json` 的 `passed=true`，新增 R34/R36/R39/R40/R41/A1 各条全 true，既有全部 UI 检查（含 UX12 A1/B3/B1）不回归。若 90 秒内未结束，按 test.ps1 的停滞判据处理（详情对话框检查卡住时先查 DialogResult 关闭路径）。

再跑原生与集成（各自全新测试根）:
```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -- --sync-review-checks
$env:AGENT_LAUNCHER_FAKE_SAMPLE='1'
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-syncrel-'+[guid]::NewGuid().ToString('N'))
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --sync-reliability-integration
$env:AGENT_LAUNCHER_TEST_ROOT=Join-Path $env:TEMP ('AgentLauncher-tests-remote-'+[guid]::NewGuid().ToString('N'))
& artifacts/test-runtime-r34/AgentLauncher.Tests.exe --remote-execution-integration
```
Expected: 全部 0 失败。

- [ ] **Step 5: Commit**

```bash
git add src/AgentLauncher.App/MainWindow.Practical.cs src/AgentLauncher.App/MainWindow.xaml.cs src/AgentLauncher.App/UiChecks.cs
git commit -m "Show per-link sync states with last observation and read-only conflict details"
```

---

### Task 6: 文档对齐（ACCEPTANCE §18 / CONTRACTS C14 / IMPLEMENTATION §93）+ 完整门禁

**Files:**
- Modify: `docs/ACCEPTANCE.md:331-349`（§18 标题、引言、R34–R43 行；R44 不动；§17 段落不动）
- Modify: `docs/CONTRACTS.md`（C14 §5 的「待实现」条例、§6 追加一条测试记录）
- Modify: `docs/IMPLEMENTATION.md`（文末追加 §93）

**Interfaces:**
- Consumes: Task 1–5 的全部实测结果（原生/集成/UI 条数与退出码）。
- Produces: 无代码接口；文档状态与偏差记录。规格 `docs/superpowers/specs/2026-11-18-sync-reliability-design.md` **一字不改**（§90 偏差先例）。

- [ ] **Step 1: 更新 ACCEPTANCE §18**

1a. 标题 `## 18. 同步可靠性（待实现）` 改为 `## 18. 同步可靠性（R34–R43 fake 通过；R44 待活测）`。

1b. 引言段（第 333 行）替换为：

```markdown
本节对应 [同步可靠性设计](superpowers/specs/2026-11-18-sync-reliability-design.md)。R34–R43 已按设计实现为 fake 回归（纯函数 + FakeSyncSimulator 传播 + UI 三层），实测记录见 IMPLEMENTATION §93。fake 不打开真实 VPS。R44 才是三台活测，现在没有执行。§83–§84 的双 VPS 单对证据不能代替 R44。R41 的 None 部分按 2026-11-19 更晚的拒收决定执行（IMPLEMENTATION §92），与本节原句的偏差记录在该行与 §93；规格历史句子不改写。
```

1c. R34–R40、R42、R43 各行：把行尾 `状态为未执行。` 改为 `状态为通过（fake，IMPLEMENTATION §93）。`（其余文字不动）。

1d. R41 行整行替换为（偏差记录）：

```markdown
| R41 | fake：同步模式 `None` 与 SSHFS | 偏差（2026-11-19 产品决定，IMPLEMENTATION §92/§93）：`None` 的保存与启动被拒收，界面给出可读的同权改法文案，不显示已收敛；规格 §6「None 继续允许」的历史句子不改写。SSHFS 成功显示已挂载，失败显示挂载失败。两者都不显示全部已收敛。状态为通过（fake，IMPLEMENTATION §93）。 |
```

1e. R44 行与 §17 各段（含「当前实现仍允许 None」的历史句）**不改**；第 335 行「本表不使用 R33」段不改。

- [ ] **Step 2: 更新 CONTRACTS C14**

2a. §5 的「待实现」条例（`- 待实现：冲突、离线、传播延迟、安全暂停和会话历史以…`）整条替换为：

```markdown
- 已实现（2026-11-19，IMPLEMENTATION §93）：冲突、离线、传播延迟、安全暂停和会话历史护栏按 [同步可靠性设计](superpowers/specs/2026-11-18-sync-reliability-design.md) 实现为每链路七态状态机（30 秒新鲜度）、只读冲突清单（安全路径过滤，≤50 行 + 剩余计数）、two-way-safe 新建会话与一次只读 `sync list --long` 探针（10 秒超时、15 秒节流、单飞不变）。验收 R34～R43 为 fake 通过，R44 仍是 opt-in 三台活测待办。flush 失败或超时仍保留会话并写入回执。普通离线、暂停与探针失败不 terminate、不 create；本轮没有 pause/resume、冲突处理或 reset 入口。既有 two-way-resolved 会话不批量迁移，解析到该模式当前状态最高未收敛。R41 的 None 部分按 2026-11-19 拒收决定执行（偏差见 ACCEPTANCE §18 与 IMPLEMENTATION §93）。本条不把 fake 写成三台已收敛活测，也不改操作员重新挂接的段落。
```

2b. §6 必要测试末尾追加一条：

```markdown
- 同步可靠性（2026-11-19）：原生 `SyncReliabilityChecks`（R34/R36 过滤/R38–R41/R43 纯函数，canned mutagen 0.18.1 形状夹具）、`--sync-reliability-integration`（R35–R38/R42，FakeSyncSimulator 三目录传播模拟器，场景经 `AGENT_LAUNCHER_FAKE_SYNC_SCENARIO` JSON 驱动，不打开真实 VPS）、UiChecks 链路徽章/上次观测/只读冲突清单回归；`--sync-review-checks` 自本轮接入 `scripts/test.ps1`。次数与结果以 IMPLEMENTATION §93 为准，本合同不重跑、不改写历史计数。
```

- [ ] **Step 3: 追加 IMPLEMENTATION §93**

在 `docs/IMPLEMENTATION.md` 文末追加（两处「实测」数字必须用 Step 4 门禁的真实输出填写，不沿用历史计数；其余文字照抄）：

```markdown
## 93. 同步可靠性 R34–R43：七态状态机、只读冲突清单与 two-way-safe（2026-11-19，Asia/Shanghai）

- 实现：新增 Core `SyncReliability.cs`（纯函数、无 IO）：mutagen `sync list --long` 文本解析、每链路七态（安全暂停/冲突/未知/离线/未收敛/同步中/已收敛）、30 秒新鲜度（过期降未知并保留上次观测）、整体标签七条优先级（两条必要链路都已收敛才是全部已收敛；单链路至多单链路已收敛）、冲突清单安全路径过滤（拒绝空/绝对/盘符/UNC/含 .. 段与 <root>）与 ≤50 行截断 + 剩余计数、错误文本脱敏（去地址/用户名/密钥路径，保留 agent-launcher- 会话名；主机卡片名来自配置不来自错误文本）。徽章显示链路状态与上次观测时间；菜单「同步详情…」只读展示整体标签、逐链路与冲突清单。
- 创建模式：新建会话 `--sync-mode two-way-safe`（替换 two-way-resolved 与其「冲突时保留两个版本」的错误注释）；既有 two-way-resolved 会话不批量迁移，解析到该模式当前状态最高未收敛。忽略列表不变（--ignore-vcs + node_modules/ bin/ obj/）。
- 探针细化（对规格措辞的实现澄清）：mutagen 0.18.1 默认 `sync list` 不显示同步模式、冲突只有计数，无法支撑规格 §4/§5 判定；探针改用一次只读 `sync list --long`（10 秒超时、512 KiB 上限、15 秒节流、单飞均不变）。WaitReadyAsync 的默认 list 判据（Watching 且无 Transition problems）不受影响。夹具形状经本机 mutagen 0.18.1 实测与 v0.18.1 源码（cmd/mutagen/sync/list_monitor_common.go、pkg/synchronization/state.go）核对；探测会话已全部 terminate，临时目录已清理。
- 生命周期护栏：普通离线/暂停/探针失败不 terminate、不 create；flush 失败保留会话并写 problem 回执（既有行为保持，Worker 驱动用例复证）；本轮不实现 pause/resume 按钮、冲突处理按钮或 reset；IWorkspaceSyncSession 无 Reset/Pause/Resume 成员（原生反射断言）。
- R41 偏差：规格 §6/R41 的「None 继续允许保存和启动」已被 2026-11-19 更晚的产品决定（§92）取代；本轮按拒收回归（保存文案共享为 RemoteHostEditor.ValidateSyncMode，EditHost 文案逐字节不变；启动拒收在 WorkspacePreparer），SSHFS 部分照规格（已挂载/挂载失败/未挂载，都不是已收敛）。规格历史句子不改写，照 §90 偏差先例记录于 ACCEPTANCE §18。
- 测试：三层。纯函数 `SyncReliabilityChecks`（原生 harness）；传播层 `--sync-reliability-integration`（FakeSyncSimulator：三个本地目录模拟本机/京云/阿里云、链路在线/离线/暂停/halted 开关、小文件复制传播、双方同路径冲突互不覆盖、flush 失败开关、JSONL 调用日志断言无 terminate/create/reset；120 秒观察窗、≤4 KiB 夹具、忽略规则内文件不传播）；UI 层 UiChecks 追加徽章/上次观测/详情对话框/冲突清单/A1 回退保留回归。未设 AGENT_LAUNCHER_FAKE_SYNC_SCENARIO 时 FakeSyncSession 保持即时成功，R33/E2E 不受影响。`--sync-review-checks` 接入 scripts/test.ps1。
- 资源边界：MaxConcurrentAgents=8、MaxSlots=16 未动；远程不进本地内存/CPU 准入；一次 list 不占窗格名额（R43 断言）。诊断只读：不 flush、不 reset、不新建 SSH、不新增自动重连计时器。
- 实测：原生套件 <原生通过数>/<原生失败数>；`--sync-reliability-integration` <集成条数>/0；`--sync-review-checks` <UX12 条数>/0；完整 `scripts/test.ps1 -RuntimeDirectory artifacts/test-runtime-r34` 退出码 <退出码>，UI <UI 通过数>/<UI 失败数>。
- 限制：fake 不打开真实 VPS；R44 未执行；不宣称 G4、浸泡或新活测。Mutagen 安全检测非原子，不承诺零丢失。two-way-resolved 存量会话的替换需破坏性重置的单独确认，本轮未实现。冲突消解本轮只验证「删除应失败一端后另一端覆盖」的会话语义（规格 §5/§9），不提供界面按钮。
```

- [ ] **Step 4: 完整门禁（worktree，隔离运行时目录）**

前置确认：主树 `D:\Users\pj\agent-launcher` 的长稳验收不依赖 TEMP 下超过 2 小时的 `AgentLauncher-tests-*` 根（test.ps1 既有卫生清理会删过期根）；如有冲突，等主树验收结束再跑。不运行 `scripts/run-soak.ps1`，不 push、不 merge。

Run:
```powershell
powershell -NoProfile -File scripts/test.ps1 -RuntimeDirectory artifacts/test-runtime-r34
```
Expected: 退出码 0；原生（含 SyncReliabilityChecks）、TerminalModel、ManualRelay、repair、八个集成套件（含 sync-reliability-integration）、`--sync-review-checks`、UI（含新增同步可靠性检查）、embedded、prototype、omp、browser 全绿。把真实条数/退出码填入 Step 3 的两处「实测」句。任何失败：修复后重跑完整门禁，不用 `-SkipUi -SkipBrowser` 关闭 C14。

- [ ] **Step 5: Commit**

```bash
git add docs/ACCEPTANCE.md docs/CONTRACTS.md docs/IMPLEMENTATION.md
git commit -m "Record sync reliability R34-R43 acceptance and R41 None deviation"
```

---

## 自检（写计划时已执行）

**规格覆盖映射（§8 每行 → Task）：**

| R | 场景 | 纯函数（Task 1/2/4） | 传播（Task 3/4） | UI（Task 5） |
| --- | --- | --- | --- | --- |
| R34 | 双链路全部已收敛 + 上次观测 | 解析/已收敛判据/整体优先级/resolved 上限/同步中全集 | R35 末双链路断言 | 双健康徽章 + 详情整体标签 |
| R35 | 三端小文件 120s 传播 | — | R35 用例（含忽略规则反证） | — |
| R36 | 同路径双方写 → 冲突不覆盖 | 冲突解析/配对/过滤/50 行截断/排除计数 | R36 用例（含删除消解语义） | 冲突徽章 + 清单 50 行 + 剩余 10 |
| R37 | 离线单侧写等待 + 恢复同会话 | — | R37 用例（create 计数=1、无 terminate） | — |
| R38 | 一离线一健康 | 离线判据/单链路已收敛 | R38 用例 | — |
| R39 | 探针失败/无法解析/过期 → 未知 | FailedProbe/30s 边界/垃圾输出 null | — | 过期/失败/不可解析徽章 |
| R40 | 三类安全暂停 + 操作员暂停 | halted 原因映射/[Paused]/优先级 | R42 内 halted 断言 | 安全暂停徽章 + 原因文案 |
| R41 | None 拒收（偏差）+ SSHFS 挂载提示 | NonMutagenSyncBadge/ValidateSyncMode/Preparer 拒收 | — | 已挂载/拒收文案徽章 |
| R42 | 离线/暂停/探针失败不 terminate/create；flush 失败保留 | 接口无 Reset/Pause/Resume 反射断言 | 三条用例（含 Worker problem 回执） | — |
| R43 | 8/16/分域/单 list 不占名额 | 常量 + IsTerminal + 探针参数只读 | 调用日志只 list | — |
| R44 | 三台活测 | 不在本轮（保持未执行） | — | — |

§4 七态与七条优先级、30s 新鲜度、脱敏：Task 1 全覆盖。§5 过滤五类拒绝 + ≤50 行 + 剩余计数 + 全不安全→未知 + 截断不改冲突：Task 1/5。§7 生命周期表（离线/暂停/探针失败/flush 失败/无确认 reset）：Task 2 护栏 + Task 4 断言。§6 None 偏差与 SSHFS：Task 1/4/5。§10 范围外（pause/resume 按钮、冲突处理按钮、reset、自动重连）：均未实现且有接口反射断言。

**签名一致性：** `SyncReliability` 全部成员在 Task 1 定义，Task 2/3/4/5 只消费；`FailedProbe(string,DateTimeOffset?,SyncLinkState?,DateTimeOffset)` 四处调用一致；`BuildConflictList` 元组元素名 (Session,LinkLabel,AlphaRole,BetaRole) 在 Task 1 测试与 Task 5 对话框一致；`FindSyncSession` 返回 `(string Name,ParsedMutagenSession Session)?` 在 Task 2 定义、Task 5 消费；`FakeSyncSimulator` API 在 Task 3 定义、Task 4 消费；`ApplySyncListToBadgesAsync(string?,bool,string,DateTimeOffset)` 与 `ShowSyncDetails(Guid)` 在 Task 5 定义并被 UiChecks 调用。

**已知偏差/澄清（均已在 Task 6 记录）：** ① R41 None 拒收（更晚产品决定）；② 探针 `--long`（默认 list 无模式/冲突路径）；③ ACCEPTANCE 同步可靠性表实际在 §18（交接文写 §17）；④ `--sync-review-checks` 原不在门禁，本轮接入。

**占位符扫描：** 无 TBD/TODO/「类似 Task N」；唯一需执行时填写的是 IMPLEMENTATION §93 两处实测数字（文档任务记录真实门禁结果，来源与填写时机已在 Step 3/4 写明）。
                   