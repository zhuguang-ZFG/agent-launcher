# 实用多窗格启动器 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 按已批准规格交付 P0：默认实用资源模式（保守/自定义可选）、最多 16 槽与 8/16 分屏、逐窗格 Agent 混搭、可见预算与 monitor 权威一次性确认、Terminal 窗格美化，以及空闲不空转等资源节约。

**Architecture:** 在 `AgentLauncher.Core` 新增资源决策与窗格几何纯函数，使配置 UI 与 monitor 调用同一实现；`Draft`/`Configuration` 以 schema 2 写出，读取 schema 1 时补默认资源字段且不改用户内容。Windows/Worker 负责 Job、预约、确认授权核销、`status` 短帧与 Windows Terminal 参数组装。本轮不实现规格中的 P1（错峰启动、追加启动）。

**Tech Stack:** .NET 8 / `net8.0-windows` / x64 WPF，零新 NuGet，Win32 Job Objects，当前用户 ACL 命名管道，Windows Terminal CLI，现有控制台测试 harness（无 xunit，入口 `scripts/test.ps1`）。

## Global Constraints

- 基准代码：`06e1e914424890947a55ae02c06711a12ba0aa62`；规格：`docs/superpowers/specs/2026-11-10-practical-multipane-design.md`（`ad27c8512ff69af65e3f4db9cb1ce728485654a9`）。实现不得偏离该规格的 P0 行为。
- 默认 Job 内存上限 **2 GiB**；禁止按「可用内存 ÷ 窗格数」自动把上限压到几百 MB；自定义下限 **1 GiB**。
- 根 Job CPU 封顶默认 **35%**（Rate=3500，Flags 启用+硬封顶）；本轮不改默认值；有活动批次时不得 `SetInformationJobObject` 改已运行 Job。
- 不写用户 Windows Terminal `settings.json`、fragment 或 profile；不承诺 CLI 字体、间距、scrollback。
- 禁止 16 个真实 Agent 压测，禁止制造真实 OOM；测试必须使用 `AGENT_LAUNCHER_TEST_ROOT` 隔离目录，不得写用户真实 `%LOCALAPPDATA%\AgentLauncher` 或用户项目。
- 轻量 16 pane 可视验证可写入手册与可选步骤，**本轮实施不必执行**。
- 保留现有启动门禁：`busy`、`launchFinalizing`、`launchCancellation`、`submittedAttempts`、`submittedRevision`、`submissionGeneration`，以及关闭等待 `launchTask`/`cancelTask`。
- 所有关键数字均为规格中的**设计选择**，不是本轮基准测试结果。硬保护不可被确认框绕过。
- 本轮交付范围 = 规格 P0 + 第 7.8 节 S1–S7。规格 P1（S8 错峰、S9 追加启动）**不交付、不实现、不在任务步骤中写 TODO 占位**。说明见下文「本轮明确不交付」。

---

## 本轮明确不交付（规格 P1，不改规格）

规格 §7.2、§7.4、§7.8 将 S8（错峰启动 + 逐项观察期）与 S9（追加启动到新窗口）标为 P1，并写明「可与 P0 同轮，但必须成套交付，否则整项不做」。用户已批准的是核心 P0，**没有另批 P1**。这与规格「否则整项不做」一致，因此本计划选择整项不做，**不修改规格原文**。

不得在实现中加入：`StartDelayMs`、错峰等待状态、把整批 10 秒观察改成逐项观察（该变更只为 S8 需要）、追加启动按钮、向已有 `-w` 窗口再拆窗格、空窗格占位协议。规格决策表中没有单独的 ACC-S08/S09；§15.3「错峰若交付则测」在本轮测试清单中删除，不作为通过条件。

若后续用户明确批准 P1，应另开计划并成套交付，而不是在本计划里留待办。

---

## 文件与职责

| 路径 | 职责 |
| --- | --- |
| `src/AgentLauncher.Core/Models.cs` | `Draft`/`Slot`/`Configuration`/`LaunchItem`/`Request`/`Response`/`ControlEntry` 字段扩展；`Draft.Copy` 补齐 schema 与资源；`Validate` 上限 16 |
| `src/AgentLauncher.Core/ResourcePolicy.cs` | **新建。** 从 `Models.cs` 迁出并重写资源常量、设置校验、`Evaluate`、账本加总、策略哈希。删除 `Models.cs` 中旧 `ResourcePolicy` |
| `src/AgentLauncher.Core/PaneLayout.cs` | **新建。** N=1..16 列行分配、创建序、`--size` 序列，纯函数可测 |
| `src/AgentLauncher.Windows/Services.cs` | `SubmitTerminal` 改用 `PaneLayout`；命令行长度 24000 fail-closed；`Request`/`status` 短帧客户端；发现结果仍只手动刷新 |
| `src/AgentLauncher.Windows/TerminalAppearance.cs` | 按 Agent 种类固定标签色；高对比度不传配色；不写 settings |
| `src/AgentLauncher.Windows/Native.cs` | 根 Job 默认仍 3500；自定义 CPU 只在**新建** monitor 根 Job 时读取，禁止改已有 Job |
| `src/AgentLauncher.Windows/WorkspacePreparer.cs` | 继续只处理启用槽；`LaunchItem` 写入 `Budget=JobMemoryLimit`、`PlanningBytes`；不给未勾选槽建 worktree |
| `src/AgentLauncher.Worker/Program.cs` | reserve 1–16；`Evaluate` + waiver 签发/核销；`status` 未变化短帧；回执未变跳过重读；`IdleTimeout=15s`；终态释放 Job 句柄 |
| `src/AgentLauncher.App/MainWindow.xaml` / `MainWindow.xaml.cs` / `MainWindow.Interaction.cs` / `MainWindow.Management.cs` | 规模预设、四计数、批量复制、资源模式与确认、自适应轮询、本机预览采样、16 槽滚动与拖放 |
| `src/AgentLauncher.App/UiChecks.cs` / `UiReviewChecks.cs` | 扩展 GUI 验收，隔离测试根 |
| `tests/AgentLauncher.Tests/ResourcePolicyChecks.cs` | **新建。** 决策表与授权语义 |
| `tests/AgentLauncher.Tests/PaneLayoutChecks.cs` | **新建。** 几何与命令序列 |
| `tests/AgentLauncher.Tests/Program.cs` / `ConfigurationReviewChecks.cs` / `Integration.cs` / `RuntimeReviewChecks.cs` / `WorkspaceReviewChecks.cs` | 接入新检查；更新旧 `ResourcePolicy.Check` 调用 |
| `docs/CONTRACTS.md` / `docs/IMPLEMENTATION.md` / `docs/SPEC.md` | 上限 16、确认协议、轮询与模式；不把 P1 写成已交付 |

---

## 共享接口（后续任务一律使用这些名字与类型）

```csharp
public static class ResourcePolicy
{
    public const ulong GiB = 1024UL * 1024 * 1024;
    public const ulong MiB = 1024UL * 1024;
    public const ulong DefaultJobMemoryLimit = 2 * GiB;
    public const ulong MinJobMemoryLimit = 1 * GiB;
    public const ulong DefaultPlanningBytes = 768 * MiB;
    public const ulong MinPlanningBytes = 256 * MiB;
    public const ulong GitJobMemoryLimit = GiB;
    public const ulong DefaultGitPlanningBytes = 256 * MiB;
    public const int DefaultCpuRate = 3500;
    public const int MinCpuRate = 1500;
    public const int MaxCpuRate = 8000;
    public const int CpuBusyPercent = 80;
    public const int SampleTtlSeconds = 3;
    public const int WaiverTtlSeconds = 45;
    public const int IdleTimeoutSeconds = 15;
    public const int CommandLineLimit = 24000;
    public const int MaxSlots = 16;

    public static ulong DefaultPhysicalFloor(ulong total) => Math.Max(2 * GiB, total / 8);
    public static ulong DefaultCommitFloor(ulong commitLimit) => Math.Max(2 * GiB, commitLimit / 20);
    public static ulong SoftCap(ulong total) => Math.Min(8 * GiB, total * 35 / 100);
    public static ulong MinCustomPhysicalFloor => 3 * GiB / 2; // 1.5 GiB
    public static ulong MinCustomCommitFloor => GiB;

    public static AdmissionDecision Evaluate(
        MemorySnapshot sample,
        ResourcePolicySnapshot policy,
        IReadOnlyList<BudgetItem> reserved,
        IReadOnlyList<BudgetItem> requested);

    public static string PolicyHash(ResourcePolicySnapshot policy);
    public static void ValidateSnapshot(ResourcePolicySnapshot policy, ulong totalPhysical, ulong commitLimit);
}

public sealed class ResourceSettings
{
    public string Mode { get; set; } = "practical"; // conservative | practical | custom
    public ulong JobMemoryLimit { get; set; } = ResourcePolicy.DefaultJobMemoryLimit;
    public ulong PlanningBytes { get; set; } = ResourcePolicy.DefaultPlanningBytes;
    public ulong? PhysicalFloorOverride { get; set; }
    public ulong? CommitFloorOverride { get; set; }
    public int CpuRate { get; set; } = ResourcePolicy.DefaultCpuRate;
    public ResourceSettings Copy() => new()
    {
        Mode = Mode, JobMemoryLimit = JobMemoryLimit, PlanningBytes = PlanningBytes,
        PhysicalFloorOverride = PhysicalFloorOverride, CommitFloorOverride = CommitFloorOverride, CpuRate = CpuRate
    };
}

public sealed record ResourcePolicySnapshot(
    string Mode, ulong JobMemoryLimit, ulong PlanningBytes,
    ulong PhysicalFloor, ulong CommitFloor, int CpuRate,
    ulong GitJobMemoryLimit, ulong GitPlanningBytes);

public sealed record BudgetItem(Guid Id, string Kind, ulong PlanningBytes, ulong JobMemoryLimit);

public enum AdmissionKind { Allow, RequireWaiver, Deny }

public sealed record AdmissionDecision(
    AdmissionKind Kind,
    string Reason,
    ulong PlanningRequested,
    ulong PlanningReserved,
    ulong CapSumRequested,
    ulong CapSumReserved,
    ulong SoftCap,
    ulong PhysicalFloor,
    ulong CommitFloor,
    bool SampleValid)
{
    public bool Launchable => Kind is AdmissionKind.Allow or AdmissionKind.RequireWaiver;
}

public static class ConfigurationCompatibility
{
    public const int CurrentSchemaVersion = 2;
    public static Configuration Accept(Configuration config); // 未知版本抛 InvalidDataException；v1 补默认且不改用户字段
}

public sealed record PaneCell(int EnabledIndex, int CreationOrder, int Column, int Row, int ColumnsInRow, int RowsInColumn);

public static class PaneLayout
{
    public static int Columns(int n, string twoPaneLayout);
    public static int[] RowsPerColumn(int n, string twoPaneLayout);
    public static IReadOnlyList<PaneCell> Map(int n, string twoPaneLayout);
    public static IReadOnlyList<string> SplitSteps(int n, string twoPaneLayout); // 不含进程参数，只含 new-tab/split-pane/-V|-H/--size/focus
}

public sealed record LaunchItem(
    Guid Id, Guid BatchId, int Position, string Name, string Cwd, WorkspaceIdentity Workspace,
    bool Shared, CommandSpec Command, ulong Budget, string Kind = "agent",
    Guid? RetryOf = null, ulong? PlanningBytes = null, string? ResourceMode = null);

public sealed record Request(
    string Operation, Guid RequestId, LaunchItem[]? Items = null, Guid? Attempt = null,
    string? Epoch = null, string? Digest = null, Guid? WaiverId = null,
    string? StatusEpoch = null, string? StatusSignature = null,
    long? DraftRevision = null, string? PolicyHash = null, string? PlanDigest = null,
    IReadOnlyList<Guid>? EnabledSlotIds = null);

public sealed record Response(
    bool Ok, string Message, ControlEntry[]? Entries = null, MemorySnapshot? Memory = null,
    string? Epoch = null, string? AdmissionBlockedReason = null, bool More = false,
    bool Unchanged = false, Guid? WaiverId = null, DateTimeOffset? WaiverExpiresAt = null,
    string? Decision = null);
```

`LaunchItem.Budget` **永远是 Job 内存上限**（遗留字段语义不变）。规划估算走 `PlanningBytes`；缺失时准入按 `DefaultPlanningBytes`（768 MiB）计，运行中 Job 仍用 `Budget`。`Request.Operation` 新增 `"waiver"`；现有 `"status"|"reserve"|"revoke"|"begin"|"resume"` 保持。

标准测试命令（各任务复用，隔离目录，禁止打真实配置）：

```powershell
Set-Location D:\Users\pj\agent-launcher
$env:AGENT_LAUNCHER_TEST_ROOT = Join-Path $env:TEMP ('AgentLauncher-tests-' + [guid]::NewGuid().ToString('N'))
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
# 期望结尾：RESULT <n> passed, 0 failed. Artifacts: ...
# 退出码 0
```

集成（需要时）继续用 `scripts/test.ps1`，它已设置临时 `AGENT_LAUNCHER_TEST_ROOT` 与 `AGENT_LAUNCHER_FAKE_SAMPLE=1`。本计划编写阶段不跑这些命令。

---

### Task 1: 模型、schema 迁移与 16 槽校验

**Files:**
- Create: `src/AgentLauncher.Core/ResourcePolicy.cs`（先放常量、`ResourceSettings`、`ResourcePolicySnapshot`、`ValidateSnapshot`、`ConfigurationCompatibility`；`Evaluate` 可先抛 `NotImplementedException`，在 Task 2 写测试后再实现）
- Create: `tests/AgentLauncher.Tests/ResourcePolicyChecks.cs`（本任务先写迁移与校验；决策表测试放 Task 2，但本文件本轮就建好）
- Modify: `src/AgentLauncher.Core/Models.cs`（`Draft.Resource`、`Draft.SchemaVersion` 写入 2、`Copy`、`Validate` 1–16；删除旧 `ResourcePolicy` 类，改 `using` 后由新文件提供同名类型以免外部编译中断）
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs` 第 78、614 行附近：`SchemaVersion!=1` 改为 `ConfigurationCompatibility.Accept`
- Modify: `tests/AgentLauncher.Tests/Program.cs`：调用 `ResourcePolicyChecks.RunMigration(Test,temp)`；更新「P low memory」可推迟到 Task 2，但若仍编译旧 `Check` 签名则先改为注释保留并在本任务提供 `Check` 薄包装或直接改测试到 `Evaluate` 的 Deny 路径
- Test: `tests/AgentLauncher.Tests/ResourcePolicyChecks.cs`、`tests/AgentLauncher.Tests/ConfigurationReviewChecks.cs`

**Interfaces:**
- Consumes: 现有 `Draft`/`Configuration`/`Store` JSON 与 `InvalidDataException` fail-closed 约定
- Produces: 上文共享接口中除 `Evaluate`/`PaneLayout`/`waiver` 以外的类型；`Draft.Resource`；`ConfigurationCompatibility.CurrentSchemaVersion = 2`；`Draft.Validate` 允许 1–16 槽

为让本任务结束时整个解决方案仍能编译，`ResourcePolicy.Check` 暂以包装保留，签名不变，内部先继续旧逻辑 **仅用于过渡编译**。Task 2 第一步会写新决策表测试并删除该旧行为。不得把包装当成最终准入。

- [ ] **Step 1: 写会失败的迁移与校验测试**

在 `tests/AgentLauncher.Tests/ResourcePolicyChecks.cs`：

```csharp
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

public static class ResourcePolicyChecks
{
    public static void RunMigration(Action<string, Action> Test, string temp)
    {
        Test("ACC-P02 v1 four-slot config loads with practical defaults and keeps user fields", () =>
        {
            var raw = """
            {"SchemaVersion":1,"Draft":{"SchemaVersion":1,"Revision":3,"Project":"","DirectoryMode":"isolated","Layout":"vertical","Slots":[{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1","Enabled":true,"AgentId":"pi","Directory":"","Note":"","Profiles":{}},{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2","Enabled":false,"AgentId":"codex","Directory":"D:\\proj\\keep","Note":"keep","Profiles":{}},{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3","Enabled":false,"AgentId":"pi","Directory":"","Note":"","Profiles":{}},{"Id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4","Enabled":false,"AgentId":"hermes","Directory":"","Note":"","Profiles":{}}]}}
            """;
            var path = Path.Combine(temp, "legacy-v1.json");
            File.WriteAllText(path, raw);
            var loaded = Store.Read<Configuration>(path)!;
            var accepted = ConfigurationCompatibility.Accept(loaded);
            accepted.Draft.Validate();
            if (accepted.Draft.Slots.Count != 4) throw new Exception("slot count changed");
            if (accepted.Draft.Slots[1].Directory.Length == 0 || accepted.Draft.Slots[1].Note != "keep") throw new Exception("user fields rewritten");
            if (accepted.Draft.Resource.Mode != "practical") throw new Exception("default mode is not practical");
            if (accepted.Draft.Resource.JobMemoryLimit != ResourcePolicy.DefaultJobMemoryLimit) throw new Exception("job cap default lost");
            if (accepted.Draft.Resource.PlanningBytes != ResourcePolicy.DefaultPlanningBytes) throw new Exception("planning default lost");
            if (accepted.Draft.Resource.CpuRate != ResourcePolicy.DefaultCpuRate) throw new Exception("cpu default changed");
        });

        Test("ACC-P01 unknown configuration schema is rejected without overwrite", () =>
        {
            var path = Path.Combine(temp, "future.json");
            var original = "{\"SchemaVersion\":99,\"Draft\":{\"SchemaVersion\":99,\"Revision\":1,\"Project\":\"\",\"DirectoryMode\":\"isolated\",\"Layout\":\"vertical\",\"Slots\":[{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Enabled\":true,\"AgentId\":\"pi\",\"Directory\":\"\",\"Profiles\":{}}]}}";
            File.WriteAllText(path, original);
            var loaded = Store.Read<Configuration>(path)!;
            var rejected = false;
            try { ConfigurationCompatibility.Accept(loaded); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("future schema accepted");
            if (File.ReadAllText(path) != original) throw new Exception("file overwritten");
        });

        Test("ACC-L02 validate accepts 16 slots and rejects 17", () =>
        {
            var d = new Draft { SchemaVersion = 2, Slots = Enumerable.Range(0, 16).Select(_ => new Slot()).ToList() };
            d.Slots[0].Enabled = true;
            d.Validate();
            d.Slots.Add(new Slot());
            var rejected = false;
            try { d.Validate(); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("17 slots accepted");
        });

        Test("Draft.Copy preserves schema and resource", () =>
        {
            var d = new Draft { SchemaVersion = 2 };
            d.Resource.Mode = "custom";
            d.Resource.JobMemoryLimit = 3 * ResourcePolicy.GiB;
            var c = d.Copy();
            if (c.SchemaVersion != 2 || c.Resource.Mode != "custom" || c.Resource.JobMemoryLimit != 3 * ResourcePolicy.GiB) throw new Exception("copy dropped fields");
            c.Resource.Mode = "conservative";
            if (d.Resource.Mode != "custom") throw new Exception("copy aliased resource");
        });
    }
}
```

在 `Program.cs` 的 `ConfigurationReviewChecks.Run` 之前加入：`ResourcePolicyChecks.RunMigration(Test,temp);`

- [ ] **Step 2: 跑模型测试，确认新用例失败**

```powershell
Set-Location D:\Users\pj\agent-launcher
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

期望：编译若已加入未实现的 `ConfigurationCompatibility`，测试输出 `FAIL ACC-P02 ...` / `FAIL ACC-P01 ...` / `FAIL ACC-L02 ...`；不得在实现前把测试改成通过。若因类型不存在而编译失败，也视为本步通过（红灯），进入 Step 3。

- [ ] **Step 3: 实现最小迁移与校验**

`Draft` 增加 `public ResourceSettings Resource { get; set; } = new();`，`Copy()` 复制 `SchemaVersion` 与 `Resource.Copy()`。`Validate()`：`SchemaVersion` 为 1 或 2（1 仅允许在 `Accept` 之前的原始对象上调用时由 `Accept` 先升级；对外 `Accept` 后为 2）；`Slots.Count is < 1 or > ResourcePolicy.MaxSlots`；`Resource.Mode` 必须是 `conservative|practical|custom`；自定义上下界在有采样总量前只检查与模式无关的绝对值（Job ≥ 1 GiB、Planning 在 `[256MiB, JobMemoryLimit]`、CpuRate 1500–8000）。`Accept`：

- `config.SchemaVersion` 与 `Draft.SchemaVersion` 若 `> 2` 或 `< 1` 或缺失非法 → `InvalidDataException`，不改调用方传入对象的已序列化文件。
- schema 1：补 `Resource` 默认值，将内存中 `SchemaVersion` 置 2，不改槽内容；模板与 `LastEdited` 同样处理后再 `Validate`。
- schema 2：缺资源字段按默认补齐，用户已写字段保留。

`MainWindow.LoadAsync` 与恢复备份路径改为 `config = ConfigurationCompatibility.Accept(...)`，去掉写死的 `SchemaVersion!=1`。只读失败路径保持不覆盖原文件。

新文件删除 `Models.cs` 里旧 `ResourcePolicy` 时，把旧常量与旧 `Check` 暂时搬到新文件，保证 Task 1 结束可编译。

- [ ] **Step 4: 再跑模型测试，确认新用例通过且旧持久化测试仍通过**

同一 `dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release`。期望：`PASS ACC-P02 ...`、`PASS ACC-P01 ...`、`PASS ACC-L02 ...`、`PASS Draft.Copy ...`，以及原有 `F15`/`F16`/`F atomic persistence`/`R11`/`F12`/`ConfigurationReviewChecks` 全部 PASS，结尾 `0 failed`，退出码 0。

- [ ] **Step 5: Commit**

```powershell
git add src/AgentLauncher.Core/Models.cs src/AgentLauncher.Core/ResourcePolicy.cs src/AgentLauncher.App/MainWindow.xaml.cs tests/AgentLauncher.Tests/ResourcePolicyChecks.cs tests/AgentLauncher.Tests/Program.cs tests/AgentLauncher.Tests/ConfigurationReviewChecks.cs
git commit -m "Add schema v2 migration and 16-slot draft validation"
```

**检查点 CP1：** 16 槽校验与旧 4 槽配置迁移可独立验收；尚未改变运行时准入或 UI。
### Task 2: 资源策略、一次性确认与 monitor 权威决策

**Files:**
- Modify: `src/AgentLauncher.Core/ResourcePolicy.cs`（实现 `Evaluate`、`PolicyHash`、加总规则；删除旧「把 Job 上限当占用加总就直接拒绝」的最终行为）
- Modify: `src/AgentLauncher.Core/Models.cs`（`LaunchItem.PlanningBytes`、`Request`/`Response` 扩展、`ControlEntry` 可选策略字段）
- Modify: `src/AgentLauncher.Worker/Program.cs`（reserve 1–16；用 `Evaluate`；`waiver` 操作；reserve 必须带未过期未使用且绑定一致的 `WaiverId` 才能在 `RequireWaiver` 时通过；活动账本加总；遗留项规划 768 MiB；禁止改已运行 Job；`begin` 只做硬保护）
- Modify: `src/AgentLauncher.Windows/WorkspacePreparer.cs` 写 `Budget`/`PlanningBytes`/`ResourceMode`
- Modify: `src/AgentLauncher.Windows/Native.cs` 仅在**新建**根 Job 时读取自定义 `CpuRate`（monitor 启动参数或环境/策略文件）。有活动句柄时不得 `SetInformationJobObject`
- Modify: `tests/AgentLauncher.Tests/ResourcePolicyChecks.cs` 增加 `RunDecisions`；`Program.cs` 旧 `P low memory` 改走 `Evaluate`
- Modify: `tests/AgentLauncher.Tests/Integration.cs`、`RuntimeReviewChecks.cs`：预算字段与 16 上限
- Test: `tests/AgentLauncher.Tests/ResourcePolicyChecks.cs`、必要时 `Integration.cs`

**Interfaces:**
- Consumes: Task 1 的 `ResourceSettings`、`ConfigurationCompatibility`、`BudgetItem`、`ResourcePolicySnapshot`
- Produces: `ResourcePolicy.Evaluate`、`PolicyHash`；`Ipc` 操作 `"waiver"`；`Response.WaiverId`/`WaiverExpiresAt`/`Decision`；monitor 内存中一次性授权（不写磁盘）

决策规则（测试必须按此写死期望，不要「差不多」）：

1. 采样无效（>3 秒、CPU 非有限或越出 `[0,100]`、内存字段非正/可用大于总量）→ `Deny`，不可确认。
2. 硬保护：`Available - planning(requested) - planning(reserved) >= PhysicalFloor`；提交余量同样对照 `CommitFloor`；CPU ≥ 80% → `Deny`。
3. 软预算：`cap(requested)+cap(reserved)` 与 `SoftCap(total)` 比较。Git：若 `requested`/`reserved` 含 `Kind=="git"`，保守口径 cap 用 `GitJobMemoryLimit`，规划口径用 `GitPlanningBytes`（256 MiB）。本轮 App 仍在 reserve 之前做 worktree 准备；预检在将创建 worktree 时把一项 git `BudgetItem` 计入 requested，准备结束后不把已退出的 git 进程留在 ledger。
4. `conservative`：软超 → `Deny`。`practical`/`custom`：硬保护过且仅软超 → `RequireWaiver`；硬保护失败 → `Deny`。
5. 遗留活动项无 `PlanningBytes`：规划加总用 768 MiB，软预算与运行 Job 用 `Plan.Budget`。

- [ ] **Step 1: 写决策表失败测试**

```csharp
public static void RunDecisions(Action<string, Action> Test)
{
    var total = 16 * ResourcePolicy.GiB;
    var policy = new ResourcePolicySnapshot("practical", ResourcePolicy.DefaultJobMemoryLimit, ResourcePolicy.DefaultPlanningBytes,
        ResourcePolicy.DefaultPhysicalFloor(total), ResourcePolicy.DefaultCommitFloor(32 * ResourcePolicy.GiB),
        ResourcePolicy.DefaultCpuRate, ResourcePolicy.GitJobMemoryLimit, ResourcePolicy.DefaultGitPlanningBytes);
    BudgetItem Agent(int i, ulong? plan = null, ulong? cap = null) =>
        new(Guid.NewGuid(), "agent", plan ?? policy.PlanningBytes, cap ?? policy.JobMemoryLimit);
    MemorySnapshot Ok(ulong avail = 8 * ResourcePolicy.GiB, ulong commitAvail = 20 * ResourcePolicy.GiB, double cpu = 10, int ageMs = 0) =>
        new(total, avail, 32 * ResourcePolicy.GiB, commitAvail, cpu, DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(ageMs));

    Test("ACC-R03 stale or invalid sample is hard deny", () =>
    {
        var stale = ResourcePolicy.Evaluate(Ok(ageMs: 4000), policy, [], [Agent(0)]);
        var badCpu = ResourcePolicy.Evaluate(Ok() with { CpuPercent = double.NaN }, policy, [], [Agent(0)]);
        if (stale.Kind != AdmissionKind.Deny || badCpu.Kind != AdmissionKind.Deny || stale.SampleValid || badCpu.SampleValid)
            throw new Exception("invalid sample not hard-denied");
    });

    Test("ACC-R02 hard floor cannot be waived", () =>
    {
        // PhysicalFloor default 2 GiB; 1.5 GiB available cannot cover 768 MiB + 2 GiB floor
        var d = ResourcePolicy.Evaluate(Ok(avail: (ulong)(1.5 * ResourcePolicy.GiB)), policy, [], [Agent(0)]);
        if (d.Kind != AdmissionKind.Deny || !d.Reason.Contains("内存")) throw new Exception(d.Reason);
    });

    Test("ACC-R01 practical soft overage requires waiver when hard checks pass", () =>
    {
        var many = Enumerable.Range(0, 8).Select(Agent).ToArray();
        var d = ResourcePolicy.Evaluate(Ok(avail: 12 * ResourcePolicy.GiB, commitAvail: 24 * ResourcePolicy.GiB), policy, [], many);
        if (d.Kind != AdmissionKind.RequireWaiver) throw new Exception("expected waiver, got " + d.Kind + " " + d.Reason);
        if (d.CapSumRequested != 8 * policy.JobMemoryLimit) throw new Exception("soft cap used planning bytes");
    });

    Test("ACC-R06 conservative soft overage is denied", () =>
    {
        var cons = policy with { Mode = "conservative" };
        var many = Enumerable.Range(0, 8).Select(Agent).ToArray();
        var d = ResourcePolicy.Evaluate(Ok(avail: 12 * ResourcePolicy.GiB, commitAvail: 24 * ResourcePolicy.GiB), cons, [], many);
        if (d.Kind != AdmissionKind.Deny) throw new Exception("conservative allowed overage");
    });

    Test("ACC-R04 legacy reserved item uses 768 MiB planning and Plan.Budget cap", () =>
    {
        var legacy = new BudgetItem(Guid.NewGuid(), "agent", ResourcePolicy.DefaultPlanningBytes, 2 * ResourcePolicy.GiB);
        var d = ResourcePolicy.Evaluate(Ok(), policy, [legacy], [Agent(0)]);
        if (d.PlanningReserved != ResourcePolicy.DefaultPlanningBytes) throw new Exception("legacy planning wrong");
        if (d.CapSumReserved != 2 * ResourcePolicy.GiB) throw new Exception("legacy cap wrong");
    });

    Test("ACC-C02 custom job cap cannot go below 1 GiB or zero floors", () =>
    {
        var bad = false;
        try { ResourcePolicy.ValidateSnapshot(policy with { JobMemoryLimit = 512 * ResourcePolicy.MiB }, total, 32 * ResourcePolicy.GiB); }
        catch (InvalidDataException) { bad = true; }
        if (!bad) throw new Exception("sub-GiB cap accepted");
        var zeroFloor = false;
        try { ResourcePolicy.ValidateSnapshot(policy with { PhysicalFloor = 0, Mode = "custom" }, total, 32 * ResourcePolicy.GiB); }
        catch (InvalidDataException) { zeroFloor = true; }
        if (!zeroFloor) throw new Exception("zero floor accepted");
    });

    Test("ACC-C01 default CPU rate stays 3500", () =>
    {
        if (ResourcePolicy.DefaultCpuRate != 3500) throw new Exception("cpu default changed");
    });
}
```

把 Step 1 里「1.5 * GiB」用 `3 * ResourcePolicy.GiB / 2` 书写，避免浮点。`Program.cs` 调用 `RunDecisions(Test)`，并把旧 `P low memory` 改成对 `Evaluate(..., requested: 2GiB planning/cap, avail: 1GiB)` 期望 `Deny`。

另写 monitor 授权测试（可放 `Integration.cs` 或带 `INTEGRATION_TESTS` 的 Worker 测试，使用 `AGENT_LAUNCHER_FAKE_SAMPLE=1` 与隔离 `AGENT_LAUNCHER_TEST_ROOT`）：

- 申请 waiver 后 reserve 成功一次；第二次用同一 `WaiverId` 失败。
- 过期：把 TTL 在测试中通过将签发时间回拨不可行则用「等待」不得靠真实 45 秒睡眠——在测试程序集用内部钩子 `WaiverTtlSeconds` 不可改产品默认。做法：测试直接调用将加入 Worker 的内部可测类型 `WaiverTable`（放在 `AgentLauncher.Windows` 或 Core 的纯类 `WaiverTable`），用注入的 `TimeFunc` 快进 46 秒。
- 改 `DraftRevision` 或 `PolicyHash` 或计划摘要后核销失败。
- 硬拒绝时 `Issue` 返回无 `WaiverId`。
- 不得接受客户端传来的 `confirmed=true` 布尔。

```csharp
public sealed class WaiverTable
{
    public WaiverTable(Func<DateTimeOffset> now);
    public Response Issue(AdmissionDecision current, long draftRevision, string policyHash, string planDigest, IReadOnlyList<Guid> enabledSlotIds, DateTimeOffset sampleAt);
    public bool Consume(Guid id, long draftRevision, string policyHash, string planDigest, IReadOnlyList<Guid> enabledSlotIds);
}
```

- [ ] **Step 2: 跑测试确认失败**

```powershell
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

期望：`FAIL ACC-R01 ...`（或同类决策测试）出现，退出码非 0。不得为了变绿删测试。

- [ ] **Step 3: 实现 `Evaluate`、`WaiverTable` 与 monitor 路径**

`Evaluate` 严格按上表。`PolicyHash` 对规范化 JSON（模式、上限、规划、两个余量、CpuRate、Git 两项）做 SHA256 十六进制。

Worker `Handle`：

- `"waiver"`：重新 `Refresh` 采样，`Evaluate`，仅当 `RequireWaiver` 时 `Issue`，把 `WaiverId` 与到期时间放入 `Response`；`Deny`/`Allow` 不签发。
- `"reserve"`：`items.Length` 改为 1–16；用规划/上限字段代替「Budget 必须等于 2GiB 常量」；若 `Evaluate` 为 `RequireWaiver`，必须 `Consume` 成功，否则抛「需要有效确认授权」；`Allow` 不得要求授权；`Deny` 拒绝。写入的 `ControlEntry` 带上 `Mode`、`JobMemoryLimit`、`PlanningBytes`、`PhysicalFloor`、`CommitFloor`、`CpuRate`、可选 `WaiverId`（仅审计）。
- `"begin"`：只跑硬保护（把本次已预约项视为 reserved、requested 空），不消费 waiver；不得改已存在 Job 的内存/CPU 上限。
- 自定义 CPU：monitor 启动时若策略为 custom，用该 `CpuRate` 创建**新**根 Job；已有根 Job 不更新。

同步修改 `WorkspacePreparer` 构造 `LaunchItem` 时填入草稿的 Job 上限与规划估算。App 侧启动预检改为 `Evaluate`（可在本任务先改 `UpdateLaunch`/`Launch` 中的 `ResourcePolicy.Check`，确认框 UI 放到 Task 4，但预检必须已能返回 `RequireWaiver`，否则 Task 4 无法接）。暂时：`Launchable` 为真才启用按钮；`RequireWaiver` 时按钮可点，Task 4 再弹确认。

- [ ] **Step 4: 再跑测试确认通过**

同一 `dotnet run ... -c Release`。期望全部决策与 waiver 测试 PASS，`G1 CPU cap configured at aggregate root` 仍断言默认 Rate==3500，`0 failed`。

再跑（隔离）：

```powershell
$env:AGENT_LAUNCHER_TEST_ROOT = Join-Path $env:TEMP ('AgentLauncher-tests-integration-' + [guid]::NewGuid().ToString('N'))
$env:AGENT_LAUNCHER_FAKE_SAMPLE = '1'
# 仅当本任务已增加集成用例时：
dotnet publish tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime --nologo -m:1
dotnet publish src/AgentLauncher.Worker/AgentLauncher.Worker.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/test-runtime --nologo -m:1
& artifacts/test-runtime/AgentLauncher.Tests.exe --integration
```

期望退出码 0。测完 `Remove-Item Env:AGENT_LAUNCHER_FAKE_SAMPLE; Remove-Item Env:AGENT_LAUNCHER_TEST_ROOT`。

- [ ] **Step 5: Commit**

```powershell
git add src/AgentLauncher.Core/ResourcePolicy.cs src/AgentLauncher.Core/Models.cs src/AgentLauncher.Worker/Program.cs src/AgentLauncher.Windows/WorkspacePreparer.cs src/AgentLauncher.Windows/Native.cs src/AgentLauncher.Windows/Services.cs tests/AgentLauncher.Tests/ResourcePolicyChecks.cs tests/AgentLauncher.Tests/Program.cs tests/AgentLauncher.Tests/Integration.cs tests/AgentLauncher.Tests/RuntimeReviewChecks.cs
git commit -m "Add practical admission decisions and one-time monitor waivers"
```

**检查点 CP2：** 配置 UI 与 monitor 将共用 `Evaluate`；确认授权不能绕过硬保护；旧 ledger 预算语义安全。尚未做 8/16 布局与确认对话框文案。

---

### Task 3: 8/16 终端布局与参数

**Files:**
- Create: `src/AgentLauncher.Core/PaneLayout.cs`
- Create: `tests/AgentLauncher.Tests/PaneLayoutChecks.cs`
- Modify: `src/AgentLauncher.Windows/Services.cs` `SubmitTerminal`（用 Map 与 SplitSteps；长度 > 24000 抛错，不截断）
- Modify: `src/AgentLauncher.Windows/TerminalAppearance.cs`（按 `AgentId` 或名称的固定调色板设置 `--tabColor`，每个 `split-pane` 都带；统一 `One Half Dark`；高对比度不传配色）
- Modify: `tests/AgentLauncher.Tests/Program.cs` 调用 `PaneLayoutChecks.Run(Test)`
- Test: `tests/AgentLauncher.Tests/PaneLayoutChecks.cs`、现有 `Integration.cs` 中 `SubmitTerminal` 用例

**Interfaces:**
- Consumes: `LaunchItem`、`ResourcePolicy.CommandLineLimit`、`TerminalAppearance.AddPaneArguments`
- Produces: `PaneLayout.Columns/RowsPerColumn/Map/SplitSteps`；`Commands.ComposeTerminalArguments`（纯列表，便于测长度）；`Commands.QuotedLength`

算法（规格 §3.3–3.4，必须有表可断言）：

- N=1：C=1；N=2：`horizontal` 则 C=1 否则 C=2；N=3：C=2 且行数 `[1,2]`；N=4：C=2（2×2）；N=5..16：C=4，前 `N%C` 列 `ceil` 行，其余 `floor` 行。
- 先按列 `split-pane -V --size ((remainingCols-1)/remainingCols)`（新窗格在右侧的假设）；再在需要的列 ` -H` 同样公式。最后 `focus-pane --target 0`。若实现后轻量实测发现「新窗格在左/上」，只改公式，不改变等分不变量；本任务单元测试先锁定「新窗格为右/下」的 `--size` 序列，并在注释写明该假设。
- 启用项按草稿顺序映射到「先左后右、同列先上后下」。
- N 只等于启用项数，不生成空窗格。

调色板写死，例如：`pi=#61AFEF`、`codex=#E5C07B`、`claude=#C678DD`、`gemini=#56B6C2`、`opencode=#98C379`、`hermes=#E06C75`、`omp=#D19A66`、`kimi=#BE5046`、`qoder=#528BFF`、`custom=#ABB2BF`。同类同色。不是对对比度的实测承诺。

- [ ] **Step 1: 写布局失败测试**

```csharp
Test("ACC-L01 eight and sixteen grids", () =>
{
    if (!PaneLayout.RowsPerColumn(8, "vertical").SequenceEqual(new[]{2,2,2,2})) throw new Exception("8");
    if (!PaneLayout.RowsPerColumn(16, "vertical").SequenceEqual(new[]{4,4,4,4})) throw new Exception("16");
    if (PaneLayout.Columns(8, "vertical") != 4 || PaneLayout.Columns(16, "vertical") != 4) throw new Exception("C");
});
Test("ACC-L03 packing examples and N=3 leftover", () =>
{
    if (!PaneLayout.RowsPerColumn(3, "vertical").SequenceEqual(new[]{1,2})) throw new Exception("N=3");
    if (!PaneLayout.RowsPerColumn(5, "vertical").SequenceEqual(new[]{2,1,1,1})) throw new Exception("N=5");
    if (!PaneLayout.RowsPerColumn(6, "vertical").SequenceEqual(new[]{2,2,1,1})) throw new Exception("N=6");
    if (!PaneLayout.RowsPerColumn(7, "vertical").SequenceEqual(new[]{2,2,2,1})) throw new Exception("N=7");
    if (!PaneLayout.RowsPerColumn(9, "vertical").SequenceEqual(new[]{3,2,2,2})) throw new Exception("N=9");
    if (PaneLayout.Map(6, "vertical").Count != 6) throw new Exception("empty placeholder");
});
Test("ACC-L06 command line over 24000 is rejected without truncation", () =>
{
    var items = Enumerable.Range(0, 16).Select(i => new LaunchItem(Guid.NewGuid(), Guid.NewGuid(), i+1, new string('n', 2000), "C:\\d", new WorkspaceIdentity("C:\\d","k"), false, new CommandSpec("C:\\a.exe", [], "exe"), ResourcePolicy.DefaultJobMemoryLimit)).ToArray();
    var rejected = false;
    try { Commands.SubmitTerminal("C:\\Windows\\System32\\cmd.exe", items, "vertical"); }
    catch (IOException ex) when (ex.Message.Contains("24000") || ex.Message.Contains("命令行")) { rejected = true; }
    if (!rejected) throw new Exception("oversize submitted or truncated");
});
```

再断言 N=8 的 `SplitSteps` 含 1 次 `new-tab`、7 次 `split-pane`、3 次 `-V` 再 4 次 `-H`（按先列后行），并以分数字符串（如 `0.75`/`0.667` 等实现选定的不变格式）锁定 `--size`。N=4 最终几何须等分（2×2），允许不再走旧的 `move-focus left/right` 特殊路径，但测试检查 4 个 cell 的行列为 `(0,0)(1,0)(0,1)(1,1)` 按创建序映射后覆盖这四格。

`QuotedLength` 按现有 `ProtectedProcess` 引号规则计算，`ComposeTerminalArguments` 的拼接长度 > 24000 时 `SubmitTerminal` 在 `Process.Start` 之前抛错。超长测试不得真的启动 16 个 Agent；上面用超长 Name 迫使超限。

- [ ] **Step 2: 跑测试确认失败**

`dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release`  
期望：`FAIL ACC-L01` 或编译缺少 `PaneLayout`。

- [ ] **Step 3: 实现布局与 `SubmitTerminal`**

用 `ArgumentList`，继续 `;` 转义与 `DisplayText` 过滤控制字符。不写 settings.json。`AddPaneArguments` 对每个 pane 设置 title / startingDirectory / suppressApplicationTitle / 非高对比度下的 scheme 与 tabColor。

- [ ] **Step 4: 再跑测试确认通过**

同一命令，期望 `PASS ACC-L01/L03/L06`，原 `Integration` 里两项/四项 `SubmitTerminal` 若依赖旧 2×2 特殊路径，改为断言新几何仍等分，全部 `0 failed`。本步可用隔离目录，不得打开用户仓库。

- [ ] **Step 5: Commit**

```powershell
git add src/AgentLauncher.Core/PaneLayout.cs src/AgentLauncher.Windows/Services.cs src/AgentLauncher.Windows/TerminalAppearance.cs tests/AgentLauncher.Tests/PaneLayoutChecks.cs tests/AgentLauncher.Tests/Program.cs tests/AgentLauncher.Tests/Integration.cs
git commit -m "Add testable 8 and 16 pane terminal layouts"
```

**检查点 CP3：** 任意 N=1..16 的网格与命令可单测；未勾选槽不会出现在 `items` 里（由调用方保证，Task 4 落实）。轻量 16 窗格可视验证不在本任务执行。
### Task 4: UI、混搭、四计数与确认框

**Files:**
- Modify: `src/AgentLauncher.App/MainWindow.xaml`、`MainWindow.xaml.cs`、`MainWindow.Interaction.cs`、`MainWindow.Management.cs`
- Modify: `src/AgentLauncher.App/UiChecks.cs`、`UiReviewChecks.cs`
- Test: 上述 UI 检查（`AGENT_LAUNCHER_TEST_ROOT` 隔离，`--ui-check`）

**Interfaces:**
- Consumes: `ResourcePolicy.Evaluate`、`AdmissionKind`、`Ipc` `"waiver"`/`"reserve"`、`PaneLayout`（预览）、`Draft.Resource`、规模与槽上限 16
- Produces: 用户可见的规模预设、四计数、批量复制、资源模式与确认文案、启动只提交启用槽

行为要点（全部要有检查或代码断言，禁止只写「注意体验」）：

- 预设按钮 1/2/4/8/16。放大：末尾补默认槽并启用新槽，不改已有槽的 Agent/目录/参数/备注。缩小：不删除槽；未编辑且超出规模的可自动取消勾选；已编辑槽保留并提示，删除只能走现有「移除」+撤销。`Add` 上限 16。撤销移除容量 16。
- 实际启动 `N = 启用数`，预览按 `PaneLayout.Map(N, layout)`，不为未勾选制造空格。
- 四计数：已配置 / 已勾选 / 已预约 / 已启动进程；后两项以 monitor 条目为准（`Started`/`Creating` 等非 Pending 非终态计为已启动或已创建；`Pending` 计预约未启动）。不得把未创建进程显示为「已启动进程」。
- 「将此项 Agent 应用到后续/全部窗格」只深拷贝 `AgentId` 与对应 `Profiles[AgentId]`（路径+参数数组）。不覆盖 `Directory`、`RequiresDirectory`、`Note`、`CustomName`（除非用户另选「连名称一起复制」，默认不复制）、`Id`、`Enabled`。
- `UpdateLaunch` 使用 `Evaluate` 的 `Launchable`；顶部展示规划合计、Job 上限对照、余量、CPU 35%（或自定义值）。`RequireWaiver` 时点击启动先弹出确认，文案必须含：范围（槽与规划/上限）、采样时间、临界余量、软预算差额、有效期 45 秒、后果（分配可能失败、系统可能变卡、不能突破硬保护、授权不可复用）。是 → 调 `"waiver"`，仅当返回 `WaiverId` 后再 `Prepare`/`reserve`（携带该 ID）。硬拒绝不得出现确认按钮。monitor 改判 `Deny` 则中止并显示新原因。
- 有活动批次时自定义上限控件只读，说明新值只对之后启动生效。
- 拖放文件夹上限 16；超过则抛现有风格的中文错误，配置不变（改 `MainWindow.Interaction.cs` 第 19 行）。
- 槽列表可滚动；150% 缩放不要求一屏 16 行。项目路径 Enter 继续生效。错误用「第 n 项」定位。高对比度不向 WT 传配色（已有 `UseSystemColors`）。参数与密钥不出现在标题、进度和状态列表。
- 任一启用项 `Resolve` 失败则整批不预约。保留取消/关闭状态机；取消不删 worktree。
- `DirectoryMode` 与两项布局控件行为保持：布局仅在启用数为 2 时有效。

- [ ] **Step 1: 写/扩展会失败的 UI 检查**

在 `UiReviewChecks.cs` 增加（名称固定，便于看报告）：

- `ACC-L04 shrink preset keeps edited slot`：16 槽中第 10 项改了 Directory，切到预设 8 后槽数仍 ≥10 且 Directory 不变。
- `ACC-L05 batch apply does not copy directory or note`。
- `ACC-U02 missing exe locates slot index and blocks launch`。
- `ACC-S01 four counts with 16 configured and 3 enabled`：文本含配置 16、勾选 3，启动按钮为「启动 3 个 Agent」。
- `ACC-R02 deny path has no waiver prompt`：用假采样或注入 `AdmissionDecision(Deny, ...)` 后确认框不出现（可通过给 `MainWindow` 测试钩子 `PendingWaiverReason` 为 null 且不调用 Issue）。
- `ACC-R01 waiver prompt lists expiry and hard-floor warning`：在 `RequireWaiver` 下断言将展示的文案包含「45」与「不能突破」之类规格用语。

测试必须设置 `AGENT_LAUNCHER_TEST_ROOT`，不得写默认配置目录。

- [ ] **Step 2: 跑 UI 检查确认新用例失败**

实现阶段在隔离根下发布 `INTEGRATION_TESTS` 后：

```powershell
$env:AGENT_LAUNCHER_TEST_ROOT = Join-Path $env:TEMP ('AgentLauncher-tests-ui-' + [guid]::NewGuid().ToString('N'))
$report = 'D:\Users\pj\agent-launcher\artifacts\ui'
# 使用 scripts/test.ps1 -SkipBrowser 或与现网相同的 Start-Process AgentLauncher.App.exe --ui-check
```

期望：`ui-checks.json` 中新 ACC 项失败或进程退出码非 0。本计划阶段不执行。

- [ ] **Step 3: 实现 UI 行为**

预设条加在槽列表上方。资源设置可进入现有「资源详情」扩展对话或 `FormWindow` 字段：`mode`、`jobLimitGiB`、`planningMiB`、可选余量与 CPU（自定义才可编辑）。启动路径伪代码：

```csharp
var decision = ResourcePolicy.Evaluate(sample, snapshot, reservedItems, requestedItems);
if (decision.Kind == AdmissionKind.Deny) throw new IOException(decision.Reason);
Guid? waiverId = null;
if (decision.Kind == AdmissionKind.RequireWaiver)
{
    if (MessageBox.Show(this, waiverText, "资源确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        return;
    var grant = await Ipc.SendAsync(new("waiver", Guid.NewGuid(), Items: plans, DraftRevision: draft.Revision, PolicyHash: ResourcePolicy.PolicyHash(snapshot), PlanDigest: planDigest, EnabledSlotIds: enabledIds));
    if (!grant.Ok || grant.WaiverId is not {} id) throw new IOException(grant.Message);
    waiverId = id;
}
// Prepare 只含启用槽，然后 ReserveWithCleanupAsync，Request 带 WaiverId
```

注意：若确认发生在 `Prepare` 之前，计划 ID 尚未生成，则 `waiver` 必须在 Prepare **之后**、reserve 之前签发（规格：绑定计划摘要）。UI 可先用估算数字让用户同意继续准备；**真正授权**在 Prepare 后、按最终 `LaunchItem[]` 再弹一次或只在此时弹一次。本计划规定：**只在 Prepare 之后弹一次绑定确认**，以避免无计划摘要的空授权。若 Prepare 很慢，准备前用非授权的提示「将创建 N 个独立目录，取消不删除已创建项」，不是 waiver。

- [ ] **Step 4: 再跑 UI 检查与模型测试**

`dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release` 期望 0 failed。隔离 UI check 期望新 ACC 项通过。

- [ ] **Step 5: Commit**

```powershell
git add src/AgentLauncher.App/MainWindow.xaml src/AgentLauncher.App/MainWindow.xaml.cs src/AgentLauncher.App/MainWindow.Interaction.cs src/AgentLauncher.App/MainWindow.Management.cs src/AgentLauncher.App/UiChecks.cs src/AgentLauncher.App/UiReviewChecks.cs
git commit -m "Add 16-slot UI presets, counts, mix copy, and waiver prompt"
```

**检查点 CP4：** 用户可配置 16、只启动勾选、确认框有后端核销。轮询节能仍用现网 2 秒 `EnsureMonitor`，在 Task 5 改。

---

### Task 5: 节能轮询、缓存与 monitor 生命周期

**Files:**
- Modify: `src/AgentLauncher.App/MainWindow.xaml.cs`（定时器策略、本机 `Sampler`、启动/检查资源才 `EnsureMonitor`）
- Modify: `src/AgentLauncher.Windows/Services.cs`（`status` 请求带 `StatusEpoch`/`StatusSignature`；发现结果会话缓存已存在，保持仅按钮刷新；`Resolve` UI 标签 60 秒缓存，启动路径必须重新解析）
- Modify: `src/AgentLauncher.Worker/Program.cs`（`IdleTimeout = 15s`；`Unchanged` 短帧；回执写入时间与长度未变则跳过 `Store.Read`，仍查 Job 成员；无连接且无活动句柄时可不采样）
- Modify: `src/AgentLauncher.App/UiChecks.cs` / `tests/AgentLauncher.Tests/RuntimeReviewChecks.cs` 或 Integration
- Test: 空闲退出、短帧、句柄释放、启动前权威预检

**Interfaces:**
- Consumes: Task 2 的 `Response.Unchanged`/`Epoch`、现有 `Sampler`、`Store.IsTerminal`
- Produces: 空闲不拉起 monitor；忙碌 1 秒、稳态 2 秒、空闲本机 30 秒预览采样；过期预览不得单独点亮启动绿灯

状态定义与规格 §7.3 一致。上次见到活动项但 IPC 失败：保持「监控器不可用」，预约数不得当 0，禁止启动。干净退出（上次 status 全终态且随后 `!MonitorRunning`）才允许 reserved=0，并用本机采样做展示。

- [ ] **Step 1: 写失败测试**

- `ACC-S03 status unchanged returns short frame`：两次 `status` 在 ledger 不变时，第二次 `Unchanged==true` 且 `Entries` 为空或省略，但 `Memory` 仍有值；客户端不得把 `submittedAttempts` 清掉（构造仍有 Pending 的客户端状态，应用短帧后attempts 仍在）。
- `ACC-S04 job handle released after terminal`：已有终态释放逻辑，补断言 `jobs` 不再持有该 Id（可通过 monitor 可测钩子或 Integration：项 Settled 后再次 open Job 的行为不倒退）。
- `ACC-S02 idle window does not keep monitor alive`：集成测试启动 App 测试钩子或直接测「无客户端请求时 monitor 在 15 秒后退出」。不要用 16 真实 Agent。可用现有 `--idle-check` 扩展：启动后不发 status，只本机采样，断言在 20 秒后 Worker 进程不存在。若 UI 测试不便拉进程，就把「timer 在空闲不调用 EnsureMonitor」做成可观测计数器 `MonitorEnsureCalls`，空闲 5 个 tick 后仍为 0，而点击「检查资源」后 ≥1。两种都要写进测试，进程级退出至少在 Worker 单测：无 pipe 请求、无 job、全终态，15 秒后 `Run()` 返回 0。可将测试 TTL 用 `INTEGRATION_TESTS` 下的缩短钩子（例如 1 秒）验证退出路径，产品默认仍 15 秒，断言 `IdleTimeoutSeconds==15`。

- [ ] **Step 2: 跑测试确认失败**

`dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release` 期望新 ACC-S02/S03 失败。

- [ ] **Step 3: 实现轮询与短帧**

删除或改写第 22/62/83/368 行「每 2 秒 `EnsureMonitorAsync`」的空闲行为。`RefreshAsync` 在空闲策略下不调用 Ensure。`status` 处理比较签名。发现不在定时器里重跑。`Resolve` 缓存不得用于 `PrepareAsync`/`Commands.Resolve` 启动路径。

- [ ] **Step 4: 再跑测试确认通过**

模型测试 0 failed。有 INTEGRATION 的 Worker 空闲退出测试 0 failed。不得诱导 OOM。

- [ ] **Step 5: Commit**

```powershell
git add src/AgentLauncher.App/MainWindow.xaml.cs src/AgentLauncher.Windows/Services.cs src/AgentLauncher.Worker/Program.cs src/AgentLauncher.App/UiChecks.cs tests/AgentLauncher.Tests/RuntimeReviewChecks.cs tests/AgentLauncher.Tests/Integration.cs
git commit -m "Idle monitor exit, status short frames, and adaptive polling"
```

**检查点 CP5：** 主窗口空闲时 monitor 可退出；启动前仍权威预检。S8/S9 仍不实现。

---

### Task 6: 端到端回归与文档

**Files:**
- Modify: `docs/CONTRACTS.md`（panes 1～16；确认授权字段；status 短帧）
- Modify: `docs/IMPLEMENTATION.md`（本轮行为、测试结果记录栏、明确 P1 未交付）
- Modify: `docs/SPEC.md` 中「最多四个配置 / 1～4 格」改为与规格一致的 16 槽/按勾选启动，避免文档互相矛盾
- Modify: 若 Task 2–5 遗漏 `WorkspaceReviewChecks` 中任何 4 槽死限，一并改 16
- Test: 完整 `scripts/test.ps1`（可 `-SkipBrowser` 若浏览器套件与本次无关且原本就可跳过；**原生+集成+UI 必须跑**），在隔离临时目录下执行并记录实际 `RESULT` 行

**Interfaces:**
- Consumes: 本计划全部已实现 P0 接口
- Produces: 与代码一致的合同/实现文档；完整测试记录。轻量 16 pane 可视验收写入「后续手工步骤」和停止条件，**本任务不要求执行**，不得把未跑的可视验收写成已通过

- [ ] **Step 1: 对照验收表补齐遗漏测试后再跑模型测试**

先按文末覆盖表逐条确认已有测试。缺的只补 P0。然后：

```powershell
Set-Location D:\Users\pj\agent-launcher
$env:AGENT_LAUNCHER_TEST_ROOT = Join-Path $env:TEMP ('AgentLauncher-tests-' + [guid]::NewGuid().ToString('N'))
dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release
```

期望：`RESULT <n> passed, 0 failed`，退出码 0。

- [ ] **Step 2: 跑完整隔离 `test.ps1` 并记录真实输出**

```powershell
Set-Location D:\Users\pj\agent-launcher
powershell -NoProfile -File .\scripts\test.ps1
```

期望：脚本正常结束（原生、集成、UI 检查均通过）。把结尾结果写入 `docs/IMPLEMENTATION.md`（实际通过数、时间，America/Adak 标注）。失败则修 P0 缺陷，不得改测试去躲硬保护。禁止 16 真实 Agent 与 OOM。

- [ ] **Step 3: 更新文档**

`CONTRACTS.md` 第 55 行 panes 改为 1～16。说明 waiver 由 monitor 签发、45 秒一次性。`IMPLEMENTATION.md` 新增一节：实用模式、四计数、空闲轮询、确认协议、P1 未做。不要把错峰/追加写成已完成。

- [ ] **Step 4: 文档与测试再确认无 P1 伪装**

搜索工作区 `StartDelayMs`、`追加启动` 实现、`错峰等待` 代码，必须不存在（文档里作为「未交付」提及可以）。规格文件不得被本任务修改。

- [ ] **Step 5: Commit**

```powershell
git add docs/CONTRACTS.md docs/IMPLEMENTATION.md docs/SPEC.md
# 以及本任务为补测试而改动的测试文件
git commit -m "Record P0 verification and update contracts for 16 panes"
```

**检查点 CP6（实施阶段终点）：** P0 可交给用户使用；P1 仍待另批；轻量 16 窗格可视验收尚未跑，不得声称 16 真实 Agent 安全。

后续手工（不在本轮实施必做）：用隔离轻量进程做 16 窗格可视检查，停止条件见规格 §15.5（16 个可见窗格、焦点第 1 项、四角标题、无空占位、记录命令长度、无 OOM）。

---

## 规格验收 ID 覆盖表

| ID | 本轮 | 任务 | 测试落点 |
| --- | --- | --- | --- |
| ACC-L01 | 交付 | 3 | `PaneLayoutChecks` 8/16 网格 |
| ACC-L02 | 交付 | 1+4 | Validate 16；UI 四计数与只启动勾选 |
| ACC-L03 | 交付 | 3 | N=1..16 示例表 |
| ACC-L04 | 交付 | 4 | 缩小预设保留已编辑槽 |
| ACC-L05 | 交付 | 4 | 批量复制不覆盖目录/备注 |
| ACC-L06 | 交付 | 3 | >24000 拒绝 |
| ACC-L07 | 交付 | 4（保持现有对账，不改成逐项错峰观察） | 现有部分失败/撤销用例 + 回归 |
| ACC-R01 | 交付 | 2+4 | `Evaluate` RequireWaiver + UI 文案 + monitor 核销 |
| ACC-R02 | 交付 | 2+4 | 硬拒绝无确认 |
| ACC-R03 | 交付 | 2 | 采样失效 |
| ACC-R04 | 交付 | 2 | 遗留规划 768 MiB / Budget 作 cap |
| ACC-R05 | 交付 | 2+4 | 活动批次不改 Job；控件只读 |
| ACC-R06 | 交付 | 2 | 保守软超拒绝 |
| ACC-C01 | 交付 | 2 | DefaultCpuRate==3500；G1 CPU 测试仍在 |
| ACC-C02 | 交付 | 2 | 下限与非零余量 |
| ACC-S01 | 交付 | 4 | 16 配 3 启 |
| ACC-S02 | 交付 | 5 | 空闲不保活 monitor |
| ACC-S03 | 交付 | 5 | 短帧 |
| ACC-S04 | 交付 | 5 | 终态释放句柄 |
| ACC-S05 | 交付 | 6 | 现有 worktree 取消保留目录（WorkspaceReviewChecks，不得回退） |
| ACC-U01 | 交付 | 3+4 | 高对比度不传配色 |
| ACC-U02 | 交付 | 4 | 第 n 项定位 |
| ACC-P01 | 交付 | 1 | 未知版本不覆盖 |
| ACC-P02 | 交付 | 1 | 旧 4 槽 + 默认实用 |
| ACC-T01 | 交付 | 3 | 不写 settings（断言/代码路径 + 既有注释合同） |
| ACC-T02 | 交付 | 3 | 标题与每 pane tabColor |
| 规格 S8/S9、错峰观察期、追加启动 | **本轮不交付** | — | 不写测试去「暂时跳过实现」 |

---

## 计划自审

1. **规格覆盖：** P0 需求（规模、混搭、美化、三模式与确认、S1–S7 节约、状态机保持、迁移、秘密不回显、文档与隔离测试）都落到 Task 1–6。P1 按规格「整项不做」处理，未改规格。临界余量、45 秒、768 MiB、2 GiB、35% 均已写入接口常量，无待定安全值。确认框有 `WaiverTable` 后端，无空设计。
2. **占位扫描：** 任务步骤不含 TBD/TODO/「以后实现 P1」。Task 1 对旧 `Check` 的过渡包装限定在 Task 2 删除。轻量 16 可视验收标明本轮不执行，而不是留空通过条件。
3. **接口一致：** 全文统一使用 `Evaluate`、`AdmissionKind`、`ResourcePolicySnapshot`、`BudgetItem`、`WaiverTable`、`PaneLayout.Map`、`LaunchItem.Budget`=Job 上限、`PlanningBytes`、`Request.WaiverId`、`Response.Unchanged`。无 `Check()` 作为最终 API（Task 2 起删除旧语义）。

**首个实施检查点：** 从 Task 1 Step 1 开始写迁移/16 槽失败测试，跑 `dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release`，在实现 `ConfigurationCompatibility` 之前保持红灯，再进入 Step 3。不要先改 UI 或 monitor。

---

Plan complete and saved to `docs/superpowers/plans/2026-11-10-practical-multipane-plan.md`. Two execution options:

1. **Subagent-Driven (recommended)** — 每个 Task 派一个新子代理，Task 之间审查，迭代更快。REQUIRED SUB-SKILL: superpowers:subagent-driven-development。
2. **Inline Execution** — 在同一会话用 executing-plans 按检查点推进。REQUIRED SUB-SKILL: superpowers:executing-plans。

方向已由规格批准，不必请用户重复批准 P0。P1 仍须另批才能开新计划。
