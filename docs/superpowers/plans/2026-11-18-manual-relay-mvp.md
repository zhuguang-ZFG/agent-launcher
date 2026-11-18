# 人工触发隔离任务中转最小原型 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在独立的 .NET 8 类库里实现一次人工触发、本地目录 fake transport 的任务领取与结果提交，并用临时目录测试把安全路径、幂等、租约、校验和与崩溃恢复跑实。

**Architecture:** `AgentLauncher.ManualRelay` 只读写调用方给的存储目录和隔离项目目录。`LocalRelay` 是账本和 fake transport。`ManualSession.RunOnce` 是一次触发。CLI 只调用 `RunOnce` 然后退出。不引用 App、Worker、Windows，不加入 `AgentLauncher.sln`，不改 `scripts/test.ps1`。

**Tech Stack:** C#、.NET SDK 8（类库 `net8.0`，覆盖 `Directory.Build.props` 的 `net8.0-windows`）、BCL、`System.Text.Json`。无 NuGet。测试是控制台 exe，退出码非 0 即失败。

## Global Constraints

- 基线 `4915841af10d1776d282ad6dede587efd4bca093`；不改 PR #8 重新挂接、Mutagen、三机同步、8 窗格、16 槽位、本地资源准入。
- 不连接 VPS 或用户电脑，不读 `VPS.txt`、`JD.pem` 或任何凭据，不创建 routine、webhook、密钥。
- 不监听、不轮询、不自动唤醒、不自动重跑。HTTP 200 不是 `succeeded`。不编造 job id。
- `wake_channel`、`wake_accepted`、`result_delivery` 只写在设计里，代码不增加这三项。
- 外部 payload 是数据。白名单只有 `file_verify` 与 `result_data`。`agent` 只归类为 `needs_agent`。无 shell、无进程、无仓库修改工具。
- 不宣称 exactly-once，不宣称 fake transport 已接通 VPS、用户电脑或 Grok Bot。
- 用户可见错误用中文。`Nullable=enable`，警告当错误。不往生产程序集加测试钩子。
- 下列命令的「预期」是实现前的失败形态或实现后的通过形态，不是已经测得的结果。

---

### Task 1: 路径监狱

**Files:**
- Create: `src/AgentLauncher.ManualRelay/AgentLauncher.ManualRelay.csproj`
- Create: `src/AgentLauncher.ManualRelay/SafePath.cs`
- Create: `tests/AgentLauncher.ManualRelay.Tests/AgentLauncher.ManualRelay.Tests.csproj`
- Create: `tests/AgentLauncher.ManualRelay.Tests/Program.cs`

**Interfaces:**
- Consumes: 无
- Produces: `SafePath.RejectRelative(string relative)`；`SafePath.ResolveExisting(string root, string relative)` 返回隔离根内的普通文件全路径，否则抛 `InvalidDataException`

- [x] **Step 1: Write the failing test**

建立两个 csproj。库项目先不要放 `SafePath.cs`。测试项目引用库。`Program.cs` 先只含 MR1：

```csharp
using AgentLauncher.ManualRelay;

var failures = 0;
void Test(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failures++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string Temp(string name)
{
    var path = Path.Combine(Path.GetTempPath(), "agent-launcher-manual-relay", Guid.NewGuid().ToString("N"), name);
    Directory.CreateDirectory(path);
    return path;
}

Test("MR1 safe path and symlink escape", () =>
{
    var root = Temp("ws");
    var outside = Temp("out");
    var sentinel = "SECRET-SENTINEL-9f3a";
    File.WriteAllText(Path.Combine(outside, "secret.txt"), sentinel);
    Directory.CreateDirectory(Path.Combine(root, "ok"));
    File.WriteAllText(Path.Combine(root, "ok", "a.txt"), "hello");
    Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
    File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), Path.Combine(outside, "secret.txt"));
    var resolved = SafePath.ResolveExisting(root, "ok/a.txt");
    Check(File.ReadAllText(resolved) == "hello", "安全相对路径应读到 hello");
    foreach (var bad in new[] { "escape/secret.txt", "linked.txt", "../secret.txt", Path.GetFullPath(Path.Combine(outside, "secret.txt")), "ok/../../secret.txt", "ok/./a.txt" })
    {
        var rejected = false;
        try { SafePath.ResolveExisting(root, bad); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "应拒绝路径 " + bad);
    }
    Check(File.ReadAllText(Path.Combine(outside, "secret.txt")) == sentinel, "拒绝路径时不得改写外部文件");
});

Console.WriteLine(failures == 0 ? "1 passed, 0 failed" : "0 passed, " + failures + " failed");
return failures == 0 ? 0 : 1;
```

库 csproj：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
```

测试 csproj：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/AgentLauncher.ManualRelay/AgentLauncher.ManualRelay.csproj" />
  </ItemGroup>
</Project>
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet run --project tests/AgentLauncher.ManualRelay.Tests/AgentLauncher.ManualRelay.Tests.csproj -c Release --nologo`

Expected: 编译失败，`SafePath` 不存在。这不是通过。

- [x] **Step 3: Write minimal implementation**

`SafePath.cs` 拒绝空路径、根路径、盘符、`/` 或 `\` 开头、`.` 与 `..` 段。逐段检查，遇到符号链接就拒绝，不打开链接目标。普通文件 `ok/a.txt` 返回其全路径。

- [x] **Step 4: Run test to verify it passes**

Run: 同上。

Expected: `PASS MR1 safe path and symlink escape`，退出码 0。若本机不能创建符号链接，停止并记录未验证，不要删掉断言。

- [x] **Step 5: Commit**

```bash
git add src/AgentLauncher.ManualRelay tests/AgentLauncher.ManualRelay.Tests
git commit -m "test: isolate manual relay path checks"
```

### Task 2: 幂等入队与载荷拒绝

**Files:**
- Create: `src/AgentLauncher.ManualRelay/RelayProtocol.cs`
- Modify: `tests/AgentLauncher.ManualRelay.Tests/Program.cs`

**Interfaces:**
- Consumes: 无
- Produces: `LocalRelay(string storeRoot, Func<DateTimeOffset> clock)`；`TaskView Enqueue(string idempotencyKey, string kind, string payloadJson)`；`TaskView` 含 `TaskId`、`Status`、`Version`、`AttemptId`、`RejectionCode`、`Kind`

- [x] **Step 1: Write the failing test**

追加 MR2：相同幂等键和相同规范载荷（只是键顺序不同）返回同一个 `TaskId`，`tasks` 下只有一个目录。同一键换一个 sha256 时抛 `InvalidDataException`，原任务仍是 `queued`，目录数仍是 1。`{"shell":"pwned-marker-7781"}`、`{"password":"secret-marker-4412"}` 和 `{"files":[{"path":"../x","sha256":"<64 个 a>"}]}` 在写入前被拒绝；存储目录的文件内容里找不到这两个 marker。

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet run --project tests/AgentLauncher.ManualRelay.Tests/AgentLauncher.ManualRelay.Tests.csproj -c Release --nologo`

Expected: 编译失败，`LocalRelay` 不存在。

- [x] **Step 3: Write minimal implementation**

`RelayProtocol.cs` 在本任务实现模型、JSON 选项、sha256、载荷规范化和 `Enqueue`。规范 JSON 递归按属性名排序。摘要是 `kind + "\n" + canonical` 的 sha256。禁止字段名不区分大小写：`shell`、`command`、`args`、`argv`、`cmd`、`exec`、`password`、`secret`、`token`、`privatekey`、`private_key`、`apikey`、`api_key`、`authorization`、`credential`、`credentials`、`pem`、`bearer`。字符串里出现 `-----BEGIN` 也拒绝。`file_verify` 只接受 1 到 20 个 `{path, sha256}`，路径必须通过 `SafePath.RejectRelative`，sha256 为 64 位小写十六进制。`result_data` 必须有 `data` 对象。`agent` 必须是非空对象。标识符只允许 `^[A-Za-z0-9._-]{1,80}$`。索引和任务文件用临时文件、`Flush(true)`、`File.Replace`。事件只追加。坏载荷在创建任务目录之前抛出。

- [x] **Step 4: Run test to verify it passes**

Run: 同上。

Expected: MR1 与 MR2 都是 PASS，退出码 0。

- [x] **Step 5: Commit**

```bash
git add src/AgentLauncher.ManualRelay/RelayProtocol.cs tests/AgentLauncher.ManualRelay.Tests/Program.cs
git commit -m "test: reject conflicting manual relay payloads"
```

### Task 3: 领取竞争、租约到期与过期提交

**Files:**
- Modify: `src/AgentLauncher.ManualRelay/RelayProtocol.cs`
- Modify: `tests/AgentLauncher.ManualRelay.Tests/Program.cs`

**Interfaces:**
- Consumes: `Enqueue`
- Produces: `LeaseOutcome TryLease(string taskId, string holderId, TimeSpan ttl)`；`SubmitOutcome Submit(string taskId, string attemptId, long expectedVersion, IReadOnlyList<FileDigest> files)`；`FileDigest(string Path, string Sha256, long Bytes)`

- [x] **Step 1: Write the failing test**

追加三项。MR3：两个 `LocalRelay` 实例、两个线程用 `Barrier` 同时 `TryLease` 同一条 `result_data`。恰好一个 `Accepted`，只有一个 attempt 目录，只有一条 `leased` 事件。MR4：把注入时钟拨过 `ExpiresAt` 再 `TryLease`，结果不接受，状态 `uncertain`，attempt 数不变，`leased` 事件仍是一条。MR5：过期后用旧 `attempt_id` 提交正确校验和，返回 `lease_expired`，状态 `uncertain`，不存在 `commit.json`，attempt 数仍是 1。另加一条：`expectedVersion` 比当前版本小 1 时返回 `version_mismatch`，状态保持 `leased` 且没有 `commit.json`。

- [x] **Step 2: Run test to verify it fails**

Run: 同上。

Expected: 编译失败，`TryLease` 或 `Submit` 不存在。

- [x] **Step 3: Write minimal implementation**

每个任务使用 `FileShare.None` 的 `writer.lock`，不可重入。`TryLease` 只在 `queued` 时创建一个 attempt 并写成 `leased`。第二个调用看到 `leased` 就返回 `not_queued`。`agent` 不在本任务测试，但领取时若类型是 `agent` 必须直接写成 `needs_agent` 且不建 attempt。时钟达到 `ExpiresAt` 时把 `leased` 写成 `uncertain`，`RejectionCode` 为 `lease_expired`，不新建 attempt。`Submit` 在过期、attempt 不一致或版本不一致时不得写 `commit.json`。

- [x] **Step 4: Run test to verify it passes**

Run: 同上。

Expected: MR1 至 MR5 与版本用例 PASS，退出码 0。

- [x] **Step 5: Commit**

```bash
git add src/AgentLauncher.ManualRelay/RelayProtocol.cs tests/AgentLauncher.ManualRelay.Tests/Program.cs
git commit -m "test: cover manual relay lease races and expiry"
```

### Task 4: 校验和、中断恢复、重复提交与人工确认

**Files:**
- Modify: `src/AgentLauncher.ManualRelay/RelayProtocol.cs`
- Modify: `tests/AgentLauncher.ManualRelay.Tests/Program.cs`

**Interfaces:**
- Consumes: `TryLease`、`Submit`
- Produces: `TaskView Recover(string taskId)`；`TaskView ConfirmUncertain(string taskId, string confirmation)`；`string? FaultAfter`

- [x] **Step 1: Write the failing test**

追加 MR6：暂存 `result.json` 的声明 sha256 错误时，`Submit` 返回 `checksum_mismatch`，状态仍是 `leased`，没有 `commit.json`。MR7：`FaultAfter = "before-commit"` 时 `Submit` 抛 `IOException`，随后 `Recover` 变成 `uncertain`，attempt 数是 1，状态不是 `succeeded`。`FaultAfter = "after-commit"` 时提交标记已在，状态仍是 `leased`，`Recover` 变成 `succeeded` 且不增加 attempt；第二次 `Recover` 不追加第二份结果文件。刚领取、尚未写入结果时 `Recover` 保持 `leased`。MR9：同一清单再提交返回 `duplicate`，`submitted` 事件仍为 1；不同字节返回 `result_conflict`，原文件字节不变。MR10：到期并确认 `human-confirmed` 后，新的 `TryLease` 得到新的 attempt；旧 attempt 提交返回 `attempt_mismatch`。未确认时 `TryLease` 不得成功。schema 99 的 `state.json` 在 `TryLease` 后字节不变并抛异常。

- [x] **Step 2: Run test to verify it fails**

Run: 同上。

Expected: 编译失败，`Recover` 不存在；或在只有前几步实现时，MR6 断言失败。实现前必须先看到失败。

- [x] **Step 3: Write minimal implementation**

`Submit` 只接受暂存目录下的单段文件名。实际 sha256 和长度必须与声明一致。成功时先替换 `manifest.json`，故障点 `before-commit` 位于清单之后、`commit.json` 之前；再替换 `commit.json`，故障点 `after-commit` 位于状态改写之前。`Recover` 只处理当前 attempt：提交标记和清单、字节三者一致时把 `leased` 或 `uncertain` 补记为 `succeeded`，不重写结果字节，不新建 attempt；只有清单或结果、没有有效提交标记时改为 `uncertain`。什么都还没写时保持 `leased`。`ConfirmUncertain` 只接受确认文字 `human-confirmed`，只把 `uncertain` 改回 `queued`，不执行任务。`SchemaVersion != 1` 时抛异常且不替换文件。重复的相同清单只追加 `duplicate_submit`。

- [x] **Step 4: Run test to verify it passes**

Run: 同上。

Expected: 已写用例全部 PASS，退出码 0。

- [x] **Step 5: Commit**

```bash
git add src/AgentLauncher.ManualRelay/RelayProtocol.cs tests/AgentLauncher.ManualRelay.Tests/Program.cs
git commit -m "test: recover interrupted manual relay submits"
```

### Task 5: 一次会话、needs_agent 与 CLI

**Files:**
- Create: `src/AgentLauncher.ManualRelay/ManualSession.cs`
- Create: `src/AgentLauncher.ManualRelay.Cli/AgentLauncher.ManualRelay.Cli.csproj`
- Create: `src/AgentLauncher.ManualRelay.Cli/Program.cs`
- Modify: `tests/AgentLauncher.ManualRelay.Tests/Program.cs`

**Interfaces:**
- Consumes: `LocalRelay` 的领取、提交、拒绝、暂存目录、`NextQueued`
- Produces: `SessionReport RunOnce(LocalRelay relay, string workspace, string holderId, TimeSpan ttl, string? taskId)`；CLI 进程一次退出

- [x] **Step 1: Write the failing test**

追加 MR8、MR12、MR13。MR8：`agent` 载荷 `{"instruction":"列出目录并删除临时文件"}` 经 `RunOnce` 后状态为 `needs_agent`，隔离目录里的哨兵文件字节不变，没有 `attempts` 目录，状态不是 `succeeded`。MR12：源码文件 `src/AgentLauncher.ManualRelay` 与 `src/AgentLauncher.ManualRelay.Cli` 中不出现 `HttpClient`、`TcpListener`、`UdpClient`、`Process.Start`、`ProcessStartInfo`、`WebRequest`、`Socket(`、`Mutagen`、`VPS.txt`、`JD.pem`、`while(true)`、`Task.Delay`。库源码中不出现 `DateTime.Now` 与 `DateTime.UtcNow`。CLI 遇到 `--listen` 退出码为 2。用库入队一条 `result_data` 后，CLI 以 `--store --workspace --holder --task` 运行一次，退出码 0，状态 `succeeded`，哨兵文件不变。MR13：`file_verify` 对真实文件计算 sha256，匹配时 `RunOnce` 为 `succeeded` 且项目文件字节不变；指向目录符号链接的路径被拒绝为 `path_rejected`，外部哨兵不出现在存储目录的任何文件里。

- [x] **Step 2: Run test to verify it fails**

Run: 同上。

Expected: 编译失败，`ManualSession` 不存在。

- [x] **Step 3: Write minimal implementation**

`ManualSession` 最多领取一条。`agent` 已由 `TryLease` 归类，会话不再写文件。`file_verify` 用 `SafePath.ResolveExisting` 读取并写入存储侧 `result.json`。`result_data` 只把 `data` 写入暂存。路径失败时 `Reject`，不把外部字节复制进存储。CLI 不识别 `--listen`、`--poll`、`--webhook`，打印中文后返回 2。正常路径只调用一次 `RunOnce`。

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet run --project tests/AgentLauncher.ManualRelay.Tests/AgentLauncher.ManualRelay.Tests.csproj -c Release --nologo`

Expected: 全部 PASS，最后一行失败数为 0，进程退出码 0。此时期望才允许写进提交说明以外的会话记录；不要事先把样本输出抄进本文冒充已经运行。

- [x] **Step 5: Commit**

```bash
git add src/AgentLauncher.ManualRelay src/AgentLauncher.ManualRelay.Cli tests/AgentLauncher.ManualRelay.Tests
git commit -m "test: run one manual relay pass without executing agent text"
```

## 覆盖核对

- 安全路径与符号链接逃逸：Task 1 与 Task 5 的 MR13。
- 重复幂等和冲突载荷：Task 2。
- 领取竞争：Task 3 的 MR3。
- 租约超时 `uncertain` 且不重跑：Task 3 的 MR4。
- 过期 attempt 提交拒绝：Task 3 的 MR5。
- checksum 失败：Task 4 的 MR6。
- 提交中断恢复：Task 4 的 MR7。两条故障点都要真的抛异常。
- `needs_agent` 不执行：Task 3 的领取规则与 Task 5 的 MR8。
- 重复结果提交：Task 4 的 MR9。
- Grok 的 `wake_channel`、`wake_accepted`、`result_delivery` 留在设计，没有实现任务。
- 入队写到一半崩溃造成的索引窗口没有自动测试。不得把它写成已经关闭。

## 执行记录

2026-11-18 在本机 Linux 上用已安装的 .NET SDK 8.0.428 执行：

`dotnet run --project tests/AgentLauncher.ManualRelay.Tests/AgentLauncher.ManualRelay.Tests.csproj -c Release --nologo`

进程退出码 0。16 项为 PASS：MR1、MR2、MR2b、MR3、MR4、MR5、MR5b、MR6、MR7、MR8、MR8a、MR9、MR10、MR11、MR12、MR13。其中 MR2b 曾在去掉重复字段转换时失败，恢复转换后通过。产品项目仍是 `net8.0-windows`，没有改目标框架。这次结果不是 `scripts/test.ps1`，也不是 Windows 门禁。

提交按文档和实现分成两次，没有按每个任务单独提交。

## 明确不改

`AgentLauncher.sln`、`scripts/test.ps1`、`scripts/build.ps1`、App、Worker、Windows、Core、ACCEPTANCE 与 IMPLEMENTATION 的通过计数都不改。Windows 门禁因此不会运行本原型。这是边界，不是本原型已在该门禁通过。
