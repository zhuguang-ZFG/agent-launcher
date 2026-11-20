# R45/R33 真实 VPS 活测运行手册（操作员执行）

> 本手册只供操作员会话执行活测；实现者/CI 不得运行 `--remote-live-memory` / `--remote-live-reattach` / `--remote-live` 本体。
> 文中全部为占位符：`<address>`（主机地址）、`<sshUser>`（SSH 登录用户）、`<agentUser>`（agent 运行用户）、`<remoteDir>`（远端全新隔离目录）、`<keyPath|->`（私钥路径，`-` 表示走 SSH config/ssh-agent 默认密钥）。不要把这些占位符的真实值写进任何提交文档。

## 0. 前置条件

- 工作目录：worktree `D:\Users\pj\agent-launcher-remote-live-r45`（分支 `feat/remote-live-r45-r33`）。不要动主工作树 `D:\Users\pj\agent-launcher`（长稳验收进行中）。
- 本机：真实 OpenSSH 在 PATH；Mutagen 已安装（准备层拒绝 None 同步，夹具必须走 Mutagen）；不得设置 `AGENT_LAUNCHER_FAKE_SAMPLE=1`。
- 远端：cgroup v2 + systemd（`systemd-run` 可用）；`pi`、`python3` 对 `<sshUser>` 与 `<agentUser>` 都在 PATH；`<agentUser>` 可用同一密钥直接 SSH（nonroot 场景需要）。
- 每次运行前远端准备一个**全新**隔离目录（例：`/home/<agentUser>/work/launcher-live-r45-<nonce>`，`<nonce>` 每次换新）：
  `ssh -i <keyPath> -p 22 <sshUser>@<address> "mkdir -p <remoteDir> && chown <agentUser> <remoteDir>"`
  （父目录也须属于 `<agentUser>` 可写，否则 Mutagen 就绪会失败。）

## 1. 构建（worktree 内）

```powershell
dotnet publish tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release -p:DefineConstants=INTEGRATION_TESTS -o artifacts/live-runtime --nologo -m:1 -p:UseSharedCompilation=false -nodeReuse:false
```

**构建后必做**：发布输出会带上集成测试假 ssh（`ssh.exe`/`ssh.dll`/`ssh.pdb`/`ssh.deps.json`/`ssh.runtimeconfig.json`）。活测前从 `artifacts/live-runtime` 删除这五个文件并确认目录里没有 `ssh.exe`——harness 护栏也会拒绝带假 ssh 的运行时目录，但请先手工清干净：

```powershell
Remove-Item artifacts/live-runtime/ssh.exe, artifacts/live-runtime/ssh.dll, artifacts/live-runtime/ssh.pdb, artifacts/live-runtime/ssh.deps.json, artifacts/live-runtime/ssh.runtimeconfig.json -ErrorAction SilentlyContinue
Test-Path artifacts/live-runtime/ssh.exe   # 必须为 False
```

## 2. 四个运行（每个运行一个全新隔离根）

每次运行设置**全新且尚不存在**的 `AGENT_LAUNCHER_TEST_ROOT`（含 `AgentLauncher-tests-`；harness 自己创建，不要预建）：
`D:\Users\pj\agent-launcher\artifacts\remote-live-r45\AgentLauncher-tests-<scenario>-<guid>`（`<guid>` 每次换新，如 `[guid]::NewGuid().ToString('N')`）。
运行时目录用本 worktree 的 `artifacts/live-runtime`。每个运行也用全新的 `<remoteDir>`（见 §0）。

### 2.1 R45 scope（上限生效 + agent 存活）

```powershell
$env:AGENT_LAUNCHER_TEST_ROOT="D:\Users\pj\agent-launcher\artifacts\remote-live-r45\AgentLauncher-tests-scope-$([guid]::NewGuid().ToString('N'))"
& .\artifacts\live-runtime\AgentLauncher.Tests.exe --remote-live-memory <address> <sshUser> <agentUser> <remoteDir> <keyPath|-> scope 512 600
```

### 2.2 R45 oom（超限在 scope 内被内核终止）

```powershell
$env:AGENT_LAUNCHER_TEST_ROOT="D:\Users\pj\agent-launcher\artifacts\remote-live-r45\AgentLauncher-tests-oom-$([guid]::NewGuid().ToString('N'))"
& .\artifacts\live-runtime\AgentLauncher.Tests.exe --remote-live-memory <address> <sshUser> <agentUser> <remoteDir> <keyPath|-> oom 512 600
```

（超限夹具分配 700 MiB > 512 MiB；上限必须填 512，判据依赖该差值。）

### 2.3 R45 nonroot（非 root 被 systemd 拒绝）

**`<sshUser>` 填 `<agentUser>`**（非 root 登录；harness 强制两者相同）：

```powershell
$env:AGENT_LAUNCHER_TEST_ROOT="D:\Users\pj\agent-launcher\artifacts\remote-live-r45\AgentLauncher-tests-nonroot-$([guid]::NewGuid().ToString('N'))"
& .\artifacts\live-runtime\AgentLauncher.Tests.exe --remote-live-memory <address> <agentUser> <agentUser> <remoteDir> <keyPath|-> nonroot 512 120
```

### 2.4 R33 reattach（断线留存 + 操作员确认重新挂接）

`<sshUser>` 用 root（杀会话 sshd 需要权限）：

```powershell
$env:AGENT_LAUNCHER_TEST_ROOT="D:\Users\pj\agent-launcher\artifacts\remote-live-r45\AgentLauncher-tests-reattach-$([guid]::NewGuid().ToString('N'))"
& .\artifacts\live-runtime\AgentLauncher.Tests.exe --remote-live-reattach <address> <sshUser> <agentUser> <remoteDir> <keyPath|-> 900
```

每个运行结束后 harness 自动：杀本次 attempt token 标记的远端进程树（只杀本次）→ 必要时操作员确认释放（Gone）→ 杀 monitor。退出码 0=PASS，1=FAIL。

## 3. 证据归档

证据 JSON 写在各自隔离根内：`remote-live-memory-results.json`（含 scenario/memoryMax/oomKill/核验输出/worker stdout/stderr）或 `remote-live-reattach-results.json`（含 transitions/探针输出/proof/pid 对比/ssh 退出码）。运行后把整个隔离根拷贝归档到：
`D:\Users\pj\agent-launcher\artifacts\remote-live-r45\evidence-<scenario>-<guid>\`
（artifacts 为忽略目录，不进版本控制；证据里含真实地址/用户/密钥路径，**不得提交**。）

## 4. 判定表（PASS 条件摘要，不得放宽）

| 运行 | PASS 必须全部满足 |
| --- | --- |
| scope | 条目 Started；agent 所在 cgroup 的 `memory.max` 精确等于 512×1024×1024；`heartbeat.txt` 两次采样（间隔 6 秒）行数增长 |
| oom | 条目 Started；`memory.max` 精确相等；scope 的 `memory.events` `oom_kill`≥1（python3 是否已死仅记录，不作判据） |
| nonroot | 条目到达终态；Detail/回执/worker-stdout/worker-stderr 含 `Access denied` 或 `Failed to start transient scope unit`（大小写不敏感；远端终端输出经 pane worker 转发，实际落在 **worker-stdout**，polkit 无 tty 代理时文案为 `Failed to start transient scope unit: Connection timed out`）；远端无本次 agent（token 探针 GONE 且 `pgrep -f 'pi -p'` 为空）；`heartbeat.txt` 从未创建 |
| reattach | 杀会话 sshd 后本地 ssh 退出码 255 且条目 Disconnected；断线后探针 ALIVE 且 heartbeat 继续增长；未确认 reattach 被拒（账本不变、无 operator-reattach.json）；确认后 Reattaching + operator-reattach.json；挂接窗格写入的 proof 文件含 nonce；agent pid 与断线前相同（未重启 agent）；heartbeat 仍在增长；全程条目非终态占名额；释放后 Settled |

说明（与计划的一处偏差，按代码现实）：产品语义里 `Started` 回执在**本地 ssh 启动**即写（远端命令尚未执行），nonroot 被拒前可能短暂出现 Started。因此「agent 从未运行」用 attempt 级证据判定：token 探针 GONE + pgrep 为空 + heartbeat.txt 从未创建 + 终态 + 拒绝文案；`sawStarted` 原样记录在证据里供核对。

## 5. 失败/受阻记录规则

- pi 不配合（不执行夹具命令/提前退出）：如实记 FAILED，保留隔离根内 worker-stdout.txt / worker-stderr.txt 与证据 JSON 的远端核验输出；**不得改用假 ssh、不得放宽判据、不得手工补证据**。
- 连接/认证/Mutagen 受阻：记录阻塞原因与已完成步骤，该运行记 BLOCKED（不算 PASS），换新隔离根与远端目录重试。
- 每次运行的实际退出码、判定表逐项结果、证据归档路径记入操作员的验收记录（如 IMPLEMENTATION 对应章节）；提交文档不写真实地址/用户名/密钥路径。