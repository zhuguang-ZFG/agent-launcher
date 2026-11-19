# 远程内存上限（MemoryLimitBytes / cgroup）规格

日期：2026-11-19 起草，随实现一并落地。对象：`AgentLauncher` 远程执行的远程主机内存上限。
背景：CONTRACTS C14 与缺口收口计划把「`MemoryLimitBytes` 与远程 cgroup」列为开放项；`RemoteExecutionSpec.Validate()` 在此之前一律拒绝该字段。本规格定义已实现的子集与仍然开放的边界。

## 1. 目标与语义

- 远程 agent 的进程树在远程 Linux 主机上受到内存上限约束；上限是**主机能力属性**，配置在远程主机上（`RemoteHost.MemoryLimitMib`，MiB 整数，留空 = 不限制），由准备层换算为 `RemoteExecutionSpec.MemoryLimitBytes`（字节）。
- 单位边界：256 MiB ≤ 上限 ≤ 64 GiB；出界一律拒收（防止 MiB/GiB 录入错误静默生效）。0 与负数非法；null 合法（不限制）。
- 上限只作用于**本次启动**的 agent 及其后代（cgroup 语义）；重新挂接打开的交互 shell 不新建限制，存活的原进程保持它启动时的限制不变。

## 2. 强制机制（cgroup v2 / systemd transient scope）

- 启动命令按远程主机是否有上限分派：
  - 无上限：现行为不变（`trap '' HUP; export NODE_OPTIONS=…; cd <dir> && <agent> <args>`，必要时外包 `su`）。
  - 有上限：同一 shell 先执行 systemd 可用性守卫，再以 `systemd-run --scope -p MemoryMax=<bytes>` 包裹原有命令：
    `if ! command -v systemd-run >/dev/null 2>&1; then echo '远程内存上限需要 systemd（systemd-run）；当前远程主机不可用，未启动 agent。' >&2; exit 97; fi; systemd-run --scope -p MemoryMax=<bytes> cd <dir> && <agent> <args>`（`su` 包裹关系不变：scope 在 su 外层，agent 及其后代落入 scope 的 cgroup）。
- 失败即关闭（fail-closed）：
  - 远程无 `systemd-run`：守卫打印可读错误并以非零码退出，agent 不启动；回执按既有路径如实记录退出码。
  - 有 `systemd-run` 但无 root（或无 user manager）：systemd 自身报错，agent 不启动；错误行进入窗格可见输出。本规格**不**为此引入 sudo 提权或 user-lingering 配置。
- `NODE_OPTIONS` 堆上限（C14 既有项）保持不变：它是 agent 进程自身的保守护栏，不是本合同的强制机制，两者可并存。

## 3. 配置与 UI

- `RemoteHostEditor`：新增「远程内存上限（MiB，留空 = 不限制）」字段；保存时校验边界与整数格式，错误信息可读。既有主机配置无该字段时按 null 处理，旧配置文件加载兼容。
- 主机编辑器保存时拒收同步模式 `None`（三台同权，2026-11-18 产品决定）；本规格不改变该决定。

## 4. 验证与验收

- R34（新增）：命令形状回归——`SshRunnerBackend.Create` 对带 `MemoryLimitBytes` 的 spec 生成含 `systemd-run --scope -p MemoryMax=<bytes>` 的远程命令；无上限时不出现该包裹；`Validate()` 的边界（256 MiB 下限、64 GiB 上限、null 合法）成立；准备层把主机的 MiB 字段换算为字节并写入 spec；fake-ssh 集成不回归。
- 真实 VPS 活测（systemd scope 真实生效、超限被内核终止、非 root 拒绝路径）仍是 opt-in 待办，不进 CI，不在本规格宣称完成。
- 本规格不涉及 CPU 限制、swap 配置（`MemorySwapMax`）或 cgroup v1 回退；这些留给后续规格。
