# 启动、资源与恢复合同 v0.6

状态：完整发布合同，技术预览已实现子集；实际接口/差异与证据见 [IMPLEMENTATION.md](IMPLEMENTATION.md)。采用 `trellis-update-spec` 的七段式写法，沿用 docs 目录。下面的设计签名、字段和路径是发布目标，不能声称与当前代码逐字一致。

v0.6 实测补充：所有本产品对同一配置/回执的读写须持同一文件命名互斥锁（有限等待），读端不能以 File.Exists 的瞬时结果判断创建成败；仍使用临时文件 flush、原子替换与备份。只有明确的共享错误可短时重试，损坏/权限错误不能当作空记录。Job 最后一个句柄丢失不等于成员退出；没有 Finished 或可查询的空 Job 证据时保持 Unknown，禁止释放预算。两项都须纳入 R12/R13 回归。

## C1. 启动计划与进程生命周期

### 1. 范围 / 触发

跨越 WPF、Windows Terminal、监控器、Runner 和 CLI，任何一层退出都不能让另一层猜测启动结果。保护范围仅限本产品创建且能够归属的会话。

### 2. 接口签名

拟定内部接口（设计签名，不是已编译代码）：

```csharp
Task<ValidationResult> ValidateAsync(LaunchDraft draft, CancellationToken ct);
Task<ResourceCheck> PreflightAsync(ValidatedLaunch launch, CancellationToken ct);
Task<PreparedLaunch> PrepareAsync(ValidatedLaunch launch, ResourceCheck check, CancellationToken ct);
Task<AdmissionDecision> AdmitAsync(PreparedLaunch launch, CancellationToken ct);
Task<SubmitResult> SubmitAsync(AdmittedLaunch launch, CancellationToken ct);
Task<ReconcileResult> ReconcileAsync(Guid launchId, CancellationToken ct);
```

工作进程命令接口：

```text
AgentLauncher.Worker.exe monitor
AgentLauncher.Worker.exe pane --launch-id <guid> --pane-id <guid> --attempt-id <guid>
```

参数仅接受指定格式的 ID；不通过 CLI 传整段 JSON、用户脚本或可自选计划文件路径。计划从当前用户应用数据的固定位置解析。

### 3. 数据与状态合同

最小计划字段：

| 字段 | 类型 / 约束 |
| --- | --- |
| schemaVersion | 整数，首版 1；未知未来版本只读拒绝执行 |
| launchId / attemptId | GUID；尝试 ID 不复用 |
| createdAtUtc / appVersion | UTC 时间与实际构建版本 |
| windowName | 根据 launchId 生成的唯一 Terminal 窗口名；不操作 `last` 窗口 |
| panes | 1～4 个有效项的不可变、有序数组；不含停用配置 |
| pane.id / sourceSlotId | 稳定 ID，与 UI 配置/位置映射可追踪 |
| pane.cwd | 规范化绝对目录；独立 worktree 准备后才写入最终值 |
| pane.executable / arguments | 解析后的路径、字符串数组；不存 shell 拼接字符串 |
| pane.adapter | `exe` 或通过测试的 `npm-cmd`；其他类型显式拒绝 |
| pane.resourceProfile | 策略版本、内存预算、进程组标识；不能由项目文件注入 |
| pane.authorization | reservationId、grantId、generation、planDigest；授权必须与不可变计划匹配 |
| pane.workspaceClaim | 物理工作目录身份、占用 ID、独立/共享模式；与资源预约一起核对 |
| pane.workspace | 可选仓库身份、基线 OID、分支与工作目录 ID |

执行计划落盘后不可被 UI 编辑。自由参数不写入普通日志，也不作为密码保存入口；含密钥需求由 CLI 自身处理。

批次状态：`Draft → Validating → Preparing → Prepared → SubmitIntent → Observing → Settled`。随时可能进入 `Blocked` 或 `NeedsReconciliation`。`Settled` 只表示每项结果已有说明，不表示任务完成。

每项状态：

```text
Prepared → Claimed → SpawnIntent → CreatedSuspended → ResumeIntent → Started → Exited
              └─ 授权已撤销且确认未启动 → Revoked
              └─ 确认创建失败或未运行子进程已回收 → FailedBeforeStart
              └─ 无法证明当前状态 → Unknown
```

关键顺序：

1. 资源预检查后，为 Git 准备阶段单独预约受控预算（C2），再准备目录；完成后释放已清空的准备 Job 预算，重新采样并原子预约 Agent 预算与工作目录占用。持久化不可变计划与提交意图，再交给 Terminal。
2. Runner 以独占 CreateNew 写入 claim，随后取得该 attempt 的跨进程互斥锁，向监控器校验/领取 C2 的单次授权。claim 只用于去重，不能授予启动权；重复或已撤销的请求不能创建进程。
3. 先配置公共父 Job 和该 attempt 唯一的子 Job，由监控器持有句柄；持久化 Job 名称、系统启动身份、授权代次和 SpawnIntent。使用 `STARTUPINFOEX / PROC_THREAD_ATTRIBUTE_JOB_LIST` 在 CreateProcess 时按有效层级关联 Job，初始线程保持暂停。禁止以“先创建、稍后 AssignProcessToJobObject”作为静默回退。
4. 创建成功后，用实际句柄验证 Job 归属，落盘 CreatedSuspended（PID、创建时间、Job、attempt）。恢复线程前持久化 ResumeIntent，再调用 ResumeThread，写 Started 回执。Runner 从授权核对到该阶段结束始终持有 attempt 互斥锁；所有句柄关闭路径显式处理。
5. 监控器/恢复器只有取得同一个 attempt 锁且核对唯一 Job 后才能处理创建中断。尚无 ResumeIntent、能证明初始线程未运行的自有暂停进程可回收并等待退出；已有 ResumeIntent 就按“可能运行”处理，保留任务并核对，不自动结束或再启动。
6. 等待进程退出/Job 活动成员事件；主 CLI 退出但受管后代仍活着，预算和目录占用继续保留。

创建时关联减少了未归属的创建窗口，但不能据此宣称多 Job 关联、落盘、授权和 Resume 构成系统级事务。API 失败或控制方中断后必须枚举该 attempt Job、核对进程创建身份；多 Job 部分失败、创建期间崩溃等行为必须通过 R13 验证。无法证明归属时保留 Unknown，阻断该恢复路径；G1 不得在此项未通过时宣称防孤儿进程完成。

互斥顺序：先 attempt/目录操作锁，再进入预算账本的短串行事务；持账本锁时不能等待其他操作锁或进程结束。撤销请求遇到仍在创建的 attempt 应返回 Creating/NeedsReconciliation，而非阻塞整个监控器。GUI 退出不释放 Runner 正持有的 attempt 锁。

创建进程与写回执不是一个事务。若此间崩溃，不能只因 claim 写入者退出就重试；须证明没有运行中的旧实例、创建者不再执行，并持久化撤销旧授权。用户选择“另开一个”也要重新准入和检查目录占用，不能绕过 Unknown 的预算。

Terminal 返回 0 只证明命令提交，不证明全部窗格已打开。10 秒回执观察期到期后显示 Unknown，之后允许继续核对；不自动补发整组。

选择器解析只检查受控 PATH/PATHEXT 与显式程序位置，排除将项目当前目录当作隐式命令来源。PowerShell 函数/别名不属于可执行程序支持范围。

`.exe` 使用结构化参数；`.cmd` 只支持经验证的 npm shim 适配，必须保留其正确调用语义。不能只靠 `ArgumentList` 宣称已经解决 CMD、Windows Terminal 分号和脚本多层解析。任意 `.cmd/.bat/.ps1` 不在首版通用支持范围，不静默用 shell 执行。

### 4. 校验与错误矩阵

| 条件 | 错误 / 结果 | 重试规则 |
| --- | --- | --- |
| 有效项为零、布局映射错误 | VALIDATION_SELECTION | 修改配置，不启动 |
| 目录不存在/不可访问 | PATH_MISSING / PATH_DENIED | 修复该项；旧验证失效 |
| CLI 未找到/不支持适配 | COMMAND_MISSING / ADAPTER_UNSUPPORTED | 选择真实程序或受支持类型 |
| Terminal 别名缺失 | TERMINAL_UNRESOLVED | 查已注册包或显式位置；不能等同“未安装” |
| CreateProcess 明确失败 | SPAWN_FAILED | 核对 attempt Job，无残留且旧授权撤销后可单项重试 |
| Job 配置/归属失败 | PROTECTION_UNAVAILABLE | 确认自有且未运行的子进程才可回收；无法确认则 Unknown，禁止无保护 Resume |
| 授权撤销、代次不符、摘要不符 | AUTHORIZATION_REVOKED / AUTHORIZATION_MISMATCH | 迟到 Runner 直接拒绝，不重领旧授权 |
| 提交结果不明/回执超时 | LAUNCH_UNKNOWN | 只核对，不能自动重试 |
| 重复 claim | ATTEMPT_ALREADY_CLAIMED | 返回已知状态或 Unknown，不再启动 |

路径拒绝 NUL 等不可传递数据，不拒绝正常中文、空格或合法标点来绕过参数实现。首版自动创建目录限本地文件系统，UNC/离线网络目录需明确标注不支持；目录探测必须可取消/有边界，不让 UI 等待网络返回。

### 5. 正常 / 基准 / 异常例

- 正常：选择 Pi 与 Codex，布局映射为两格，收到两个 Started 回执。
- 基准：四个配置只启用第 1 项；不探测或创建其余三项的 Agent 进程。
- 异常：CLI 已开始，Runner 在写 Started 前退出；恢复显示 Unknown，文件与现有进程保留，不自动重启。

### 6. 必要测试

F01～F08、R01～R05、R13、R14：argv/cwd 精确一致、单次授权最多创建一个 CLI、创建阶段无未归属挂起进程、迟到请求已被撤销、GUI/监控器崩溃不连带结束 Agent、Unknown 不重放。

### 7. 错误与正确做法

错误：`Process.Start("cmd", "/k " + agent + " " + droppedPath)`；10 秒无回执就重新执行。

正确：路径作为 cwd，参数数组经适配器传递；落盘 ID 与 claim 约束尝试；无证据时保留 Unknown。

## C2. 资源预算与并发

### 1. 范围 / 触发

用户把电脑响应性作为首要要求。必须控制本应用的辅助开销，并对受管 Agent/子进程设置可验证预算。以下阈值是保护模式的初始提案，G1 必须测量后锁定策略版本，不能宣称适用于所有电脑和任务。

### 2. 接口签名

```csharp
Task<ResourceSnapshot> SampleAsync(CancellationToken ct);
AdmissionDecision Evaluate(ResourceSnapshot snapshot, BudgetLedger ledger, ResourceRequest request);
Task<Reservation> ReserveAsync(ResourceRequest request, CancellationToken ct);
Task<SpawnGrant> TryBeginSpawnAsync(GrantRequest request, CancellationToken ct);
Task<RevokeResult> RevokePendingAsync(GrantKey key, CancellationToken ct);
Task<ProtectedProcess> SpawnProtectedAsync(PanePlan plan, SpawnGrant grant, CancellationToken ct);
```

当前用户单一监控器通过受当前用户限制的命名管道提供这些操作；协议含 schemaVersion、requestId、launchId、paneId，不接受任意 PID 的结束请求。预算检查与预约在同一串行临界区完成，防止两次并发检查同时通过后超额。

`ResourceSnapshot` 至少包含 sampleTimeUtc、单调采样时间、systemBootId、可见物理内存/可用内存字节数、commitLimit/commitUsed、整机 CPU 区间比例、每个受管 Job 的提交/私有驻留量和数据可信状态；数值缺失不填零。`AdmissionDecision` 返回 allowed、reasonCode、policyVersion、requestedBytes、可解释的缺口及下一步；只有允许时才产生唯一 reservationId。

预算预约与启动授权分开存储。授权包含 grantId、reservationId、launchId、paneId、attemptId、generation、controllerEpoch、systemBootId、planDigest、state。状态为 `Pending → Creating → Running → Settled`，Pending 可进入 Revoked；Creating 必须按 C1 核对后才能进入 Revoked/FailedBeforeStart。没有授权，单凭计划和 claim 不得执行。

- `TryBeginSpawnAsync` 在同一短事务中验证授权仍 Pending、摘要和代次匹配、预约/目录占用有效，再持久化 Creating；重复 requestId 只返回原决定，不新建授权。确认消息丢失时按 Creating 核对，不能另领一份。
- `RevokePendingAsync` 对 Pending 授权持久化 Revoked 并增加代次；撤销成功后释放对应预算/占用。晚到 Runner 即使读到旧 plan，也必须被监控器拒绝。撤销与领取互斥，不能先释放预算再落盘撤销。
- 发现 Creating/ResumeIntent/Unknown 时返回 NeedsReconciliation。只有取得 attempt 锁、核对创建者/Job 并确认旧尝试不会再执行，才可终结授权与释放预算。不能只凭 TTL、PID 不存在或主 CLI 已退出作结论。
- 监控器重启先恢复授权账本、切换 controllerEpoch 并核对活动 attempt；旧连接的授权不能离线使用。Runner 在创建前及写 ResumeIntent 前核对当前授权，监控器不可用则不继续启动。恢复后只能将已核对的原 attempt 绑定到新 epoch，不为同一尝试再授予一次创建权限；旧 epoch 请求返回需要重新核对。
- 以持久化撤销记录拒绝迟到请求；没有活动进程/未解决的 Creating 后，纯历史 Pending 已撤销记录不阻止监控器退出。用户重试使用新 attempt/grant，旧记录不删除。

### 3. 预算、线程与存活合同

保护模式初值：

| 参数 | 初值 / 解释 |
| --- | --- |
| 初次启用项 | 1；用户可选 2～4，需准入 |
| 全部受管负载的 CPU 上限 | Agent、Git 准备及其受管后代合计整机 35%，一个公共外层 Job，`CpuRate=3500`；不是每格/每阶段各 35% |
| 每会话提交内存上限 B | 2 GiB，包含归属到该 Job 的工具子进程 |
| Git 准备预算 | 初始 1 GiB/操作，包含 filters/hooks 后代；所有仓库全局最多 1 个准备操作 |
| 目标盘保留空间 | 初始 `max(2 GiB, 目标卷容量的 5%)`，检出估算另加；不冒充磁盘硬限额 |
| 受管内存预算总上限 | `min(8 GiB, 0.35 × 系统可见物理内存)` |
| 物理内存保留 R | `max(3 GiB, 0.20 × 系统可见物理内存)` |
| 系统提交余量保留 Rc | `max(2 GiB, 0.05 × 系统提交上限)` |
| 可用采样有效期 | 3 秒；过期先重采样 |
| 运行中采样 | 每 2 秒一次，共享一个采样器；优先原生计数器与通知，不周期运行 WMI/PowerShell |
| 恢复等待 | 压力解除连续 5 次采样后才放开新准入；不自动启动旧请求 |

记 `A` 为可用物理内存，`H` 为系统提交余量；当前各会话预算为 `B_i`，实测已提交量为 `C_i`，私有驻留量为 `W_i`。提交余量预约 `Uc=Σmax(0,B_i-C_i)`，物理余量预约 `Up=Σmax(0,B_i-W_i)`；不能拿已换出的提交内存当作已占用物理内存。新请求总预算 `N` 需要同时满足：

```text
全部已预约预算 + N <= 受管预算总上限
A >= R + Up + N
H >= Rc + Uc + N
整机最近一个有效 CPU 采样区间 < 80%
采样、归属与账本均可信
```

这是保守准入，不是未来空闲内存保证；提交内存与驻留内存不同，外部进程仍会变化。2 GiB 是工作负载预算，不是预测每个 Agent 一定占 2 GiB。账本同时包含 Git 准备 Job 与所有 Agent Job，禁止只累计 Agent。批次按整体预算先预约，不足时让用户减少项；进程按顺序受控启动。

Git 准备走同一受保护创建接口，包含检出及继承的 filters/hooks；全局准备上限 1，在其内部再按仓库锁串行。准备预算在活动成员归零、操作结果核对完成后才释放，再重新评估 Agent 准入。既有 Agent 工作时启动新项目，也要把两类负载计入同一总配额。

准备前检查目标卷可写和可用空间，流式读取 Git 已跟踪文件尺寸估算，不扫描所有工作文件或将整个 Git 输出载入内存。基础门槛为“保留空间 + 估算检出大小”；filters、LFS 展开可能大于估算，显示该限制。准备中空间触及保留线则停止新增操作并提示，不自动清理用户目录；当前写操作按 C4 处理。磁盘检查无法保证不耗尽，P09 必须验证真实准备负载下的桌面响应，未通过不得发布。

如果 A 低于 R，或整机 CPU 连续三个 2 秒区间高于 80%，进入 Pressure：禁止新增，发一次可读提示并限频；不靠轮询弹窗。严重压力只建议用户在终端保存并正常结束不需要的任务，不自动强杀正在写文件的 Agent。

进程组：外层 Job 限制全部受管负载的总 CPU；内层会话或 Git 准备 Job 限制内存并提供归属。所有限额在进程创建时关联，并在初始线程恢复前核验；不启用允许后代逃逸的 breakaway 设置。Terminal、GUI、监控器、Runner 不进入该 CPU 限速组，其资源另行预算。已有嵌套 Job/Windows 环境不支持要求时失败返回，不能默认放行。

内存硬限额让超额分配失败，可能导致编译器或 Agent 退出；不会保存所有运行内存。只设置通知上限并不能约束内存，因此产品必须区分“提示阈值”和“硬预算”。不在运行中把内存硬限额缩到当前使用量之下。需要更多预算的任务在退出后重新评估；首版不提供一键解除保护。

根 Job 限额跨会话一致，新增 UI 实例不能重新创建另一个相同配额组绕过总限额。监控器与 Runner 保留必要 Job 句柄；不设置 `KILL_ON_JOB_CLOSE`，配置窗口关闭/监控器退出不会主动杀已有任务。监控器恢复需通过 Job 名称、会话记录、进程创建时间和当前系统启动身份核对；不能只凭 PID 重用旧记录。

如果主 CLI 已退出但子工具仍在 Job 中，状态为“Agent 已退出，工具仍运行”，保留预算和监控；Job 活动进程归零后才释放预约。重启后历史记录不算仍占预算，但新启动前需核对当前系统身份。没有受管进程、未完成提交或 UI 准入请求时，监控器在短宽限期内退出；首版目标 5 秒。

线程约定：UI 一个 Dispatcher；目录/命令探测最多 2，配置写入 1、全局 Git 准备 1（内部同仓库写入 1）、受控进程提交 1。异步不等于创建线程；阻塞调用使用有界执行器，过期结果丢弃。不能为了取消一个无法中断的系统调用无限补开线程。

### 4. 错误矩阵

| 条件 | 返回 | 行为 |
| --- | --- | --- |
| 内存/提交余量不足 | RESOURCE_MEMORY_LOW | 不启动，不改为共享目录规避 |
| CPU 已忙 | RESOURCE_CPU_BUSY | 等待用户重新检查，不自动排队重试 |
| 无可信采样或归属 | RESOURCE_STATE_UNKNOWN | 禁止新准入，允许查看/恢复已有状态 |
| 配额 API 失败 | PROTECTION_UNAVAILABLE | 不恢复未运行子进程，给出平台原因 |
| 会话达到内存上限 | MEMORY_LIMIT_REPORTED | 有 OS 事件才这样归因；保留退出码和工作目录 |
| 外部程序造成压力 | SYSTEM_PRESSURE | 限制新增；不宣称可限制别的软件 |

磁盘和 GPU 并没有被 CPU/内存 Job 配额完全保护。Git 准备已纳入 CPU/内存总量，并进行全局串行和磁盘余量检查；若受支持的常见任务仍导致持续磁盘卡顿，性能验收失败，必须减小默认负载或补充经验证的控制，而不是把问题归给用户。本地大模型、外部常驻服务不属于首版性能保证范围。

### 5. 正常 / 基准 / 异常例

- 正常：同机有足够余量，两会话总预算通过；两个 CPU 压力子进程的合计受 35% 约束。
- 基准：当次只启用一个 Agent，其他配置不产生进程；关闭 GUI 后只有监控器/Runner 的小额辅助开销。
- 异常：本机采样 15.2 GiB 总内存、1.2 GiB 可用，低于 R，应返回 RESOURCE_MEMORY_LOW；不去尝试启动“看看会不会卡”。

### 6. 必要测试

P01～P09、R06～R08、R14、R15：预算并发预约、撤销与迟到 Runner、Git 准备与 Agent 的合计限额、监控器故障、内存超额、正常退出后资源释放。压力测试用隔离环境和受限测试程序，不直接耗尽用户当前桌面。

### 7. 错误与正确做法

错误：每格 CPU 上限 35%，四格相加可以占满机器；最小化 GUI 就报告“内存已释放”。

正确：共享外层总配额；报告 GUI、监控器、Runner、Terminal、Agent/工具各部分成本，UI 关闭前后对照测量。

## C3. Git 工作目录与持久化

### 1. 范围 / 触发

同项目多个写入者、启动中断、配置自动保存和重复启动涉及磁盘状态，必须可核对恢复。

### 2. 接口签名

```csharp
Task<RepositoryIdentity> ResolveRepositoryAsync(string cwd, CancellationToken ct);
Task<WorkspaceResult> EnsureWorkspaceAsync(WorkspacePlan plan, CancellationToken ct);
Task<WorkspaceClaimResult> ClaimWorkspaceAsync(WorkspaceUse request, CancellationToken ct);
Task<WorkspaceReleaseResult> ReleaseWorkspaceAsync(Guid claimId, CompletionEvidence evidence, CancellationToken ct);
Task<SaveResult> SaveDraftAsync(Draft draft, long expectedRevision, CancellationToken ct);
Task<RecoveryReport> RecoverAsync(CancellationToken ct);
```

### 3. 数据与操作合同

仓库身份由 Git 返回的 worktree 根、common Git dir 和路径身份共同解析，不能以目录名或 `.git` 必为文件夹判断。同目录大小写、符号链接/junction、已有 linked worktree 必须识别为相同仓库或明确无法确认。

自动创建模式：固定基线 OID、唯一分支、位于仓库外的目录；用当前用户范围仓库锁串行化本产品的 Git 写操作。不删除 `.git/index.lock`，不修改全局 safe.directory，不自动提交/stash/merge。

从项目子目录拖入时记录相对路径；新 worktree 创建后映射到同一相对子目录。基线中无此子目录则提示使用仓库根或返回调整，不悄悄变成不同 cwd。已有两个不同工作目录使用“使用已准备目录”即可，不反复生成嵌套 worktree。

工作目录占用与 Git 创建锁不同：占用覆盖整个受管任务生命周期和跨批次启动。Git 项目以物理 worktree 根的卷/文件身份为 key，同 worktree 的两个子目录默认视为同一写入范围；非 Git 目录规范化后还要检查包含关系，别名或身份无法确认则阻止独立模式。

- Claim 记录 claimId、worktreeIdentity、launch/pane/attempt、授权代次、模式与 Job 身份；与资源预约在监控器同一短事务中检查/登记。批次内重复路径同样检查，不能先启动第一个才发现第二个。
- `exclusive` 与任何已有活动占用冲突；仅已有占用和新占用都明确为 `shared` 才允许共用。不能通过新请求勾选共享，悄悄降低已有独立会话的保护。第一版不在线转换活动独立会话；可选择新 worktree，或正常结束旧会话后重新配置。
- Unknown/Creating 按仍占用处理。主 CLI 退出但 Job 后代尚在，不释放；撤销 Pending 授权成功或 Job 已清空且创建授权终结后才释放。
- 提交前与进程创建前核对物理身份；路径被重定向/替换导致身份变化则停止启动。恢复后先重建占用表，再允许新请求。
- 只防护本应用已知会话，不声称能阻止外部编辑器、手工 Agent 或恶意进程修改目录；任务备注不等于强制只读。

普通本地 Git 仓库自动创建首版覆盖范围：已有提交、目标路径可写、非 bare、无未验证子模块/特殊稀疏 checkout。未提交修改提供基于已提交版本的明确选择。未跟踪/忽略的 `.env`、依赖、虚拟环境不会自动复制。

写操作先记录 Intent，再运行 Git，再核对 `git worktree list --porcelain`、分支、OID、路径，最后记录完成。Git 已成功但记录未写完时，恢复执行核对而非再创建。准备失败保留已创建目录；不自动删除以模拟回滚。Git 自身 hooks/filters 等既有行为可能触发项目配置，启动器不额外执行“初始化脚本”；不能绝对宣称 Git 不执行任何配置逻辑。

配置/记录位于 `%LOCALAPPDATA%\AgentLauncher\`：

| 路径 | 内容 / 生命周期 |
| --- | --- |
| preferences.json / draft.json | 偏好、最近草稿；带 schemaVersion 和 revision |
| last-edited.json | 最近一次离开人工编辑草稿前的完整快照；模板浏览不覆写，原子保存与备份规则同 draft |
| agents.json / templates.json | 显式自定义 CLI、命名模板 |
| workspaces.json | 仓库/基线/目录/分支记录；不作为进程存活证据 |
| control/ | 由监控器单写的授权/预算/目录占用日志和快照，含代次与系统启动身份；恢复核对后才可准入 |
| launches/<id>/plan.json | 不可变启动计划 |
| launches/<id>/<pane>/<attempt>/ | claim、SpawnIntent、不可变阶段回执；GUI 与 Runner 不竞争写同一文件 |
| logs/ | 轮转日志，最多 5 × 2 MiB；敏感参数/环境变量/对话不写入 |

同一用户一个 GUI 写入者，第二实例只激活已有窗口。监控器与各 Runner 分别写自己的事件/回执，通过 current-user ACL、版本与 ID 校验接收。配置保存使用同目录临时文件、flush、原子替换、上个有效备份；不把多份 JSON 的更新称为跨文件事务。跨文件关联使用稳定 ID，加载时验证完整性。

首版不自动删除计划、claim、Unknown 记录、分支或 worktree；只对普通日志轮转。避免延迟 Runner 需要的计划被“按天数”删除。后续清理必须先定义运行归属证明。

文件读写失败要保留内存中的草稿和最后有效磁盘版本，显示“尚未保存”。未来 schema 拒绝执行且不降级覆写；迁移备份后才替换，崩溃后可回退。并发保存按 revision 串行，旧请求不能覆盖新草稿。

### 4. 错误矩阵

| 条件 | 错误 / 行为 |
| --- | --- |
| 非 Git / 无提交 | GIT_UNSUPPORTED；可单 Agent 原目录或用户准备目录 |
| 脏项目 | GIT_BASELINE_CHOICE；用户处理或明确选择 HEAD |
| safe.directory 拒绝 | GIT_TRUST_REQUIRED；说明原因，不全局绕过 |
| 分支/目录已存在但不归本记录 | WORKSPACE_COLLISION；不复用、不删除 |
| 独立工作目录已被占用 | WORKSPACE_IN_USE；选择其他 worktree 或正常结束旧会话 |
| 占用/物理身份未确认或改变 | WORKSPACE_STATE_UNKNOWN / WORKSPACE_IDENTITY_CHANGED；核对前不启动 |
| 目标盘余量不足 | DISK_RESERVE_LOW；停止新增准备，不自动删目录 |
| 磁盘不足/无权限/锁占用 | WORKSPACE_PREPARE_FAILED；保留已完成项和诊断 |
| 配置损坏 | CONFIG_CORRUPT；恢复备份或明确重置，不静默清空 |
| schema 高于支持版本 | CONFIG_VERSION_UNSUPPORTED；只读拒绝执行 |
| 保存失败/日志写失败 | CONFIG_WRITE_FAILED / DIAGNOSTIC_IO_FAILED；不能因写错误日志再次崩溃 |

### 5. 正常 / 基准 / 异常例

- 正常：两项来自同一 OID，分支不同，修改 A 不改变 B 的文件。
- 基准：已准备 worktree 直接使用，不创建额外副本或安装依赖。
- 异常：第二目录创建失败，第一目录记录并保留；重试核对第一目录后只处理第二项。

### 6. 必要测试

F09～F14、R09～R12、R16：大小写/别名路径、同 worktree 子目录跨批次占用、脏目录、创建后中断、磁盘写失败、配置损坏、schema 升级、多实例冲突。

### 7. 错误与正确做法

错误：路径字符串不同就当成两个仓库；用 `finally` 删除所有新目录；JSON 解析失败直接覆盖空配置。

正确：核对真实 Git 身份和记录；保留可恢复成果；保留损坏样本与有效备份，由用户选择恢复。

## C4. 异常、取消与稳定性约定

- 每个异步入口观察异常并返回结构化错误，除 UI 事件外不用 `async void`。错误转换带 code、stage、retryability、safeMessage、diagnosticId，不向普通 UI 填整段堆栈。
- `DispatcherUnhandledException` 只覆盖相应 UI 线程。Task 和后台线程异常在自身边界处理；不能只注册一个事件就声称防崩溃完成。
- 可恢复错误留在当前步骤；未知致命错误停止新提交，尽力记录后退出相关组件。OOM/StackOverflow/断电不能保证 handler 执行或无损保存，依靠事前持久化。
- GUI 关闭/崩溃不携带结束 Agent 的 Job 句柄策略；监控器故障时配额留在现有 Job 上，新请求阻止。恢复失败显示 Unknown，不启动重复监控组。
- 用户取消先停止未开始的准备，并请求撤销尚未领取的启动授权；只有撤销落盘后释放预算/目录占用。已领取或正在创建的项先核对，已运行 Agent 继续工作。Git 写操作不强杀后删除锁，等待结果或标为需要核对。
- 单次命令探测时间预算 2 秒、整体验证 5 秒；Git 操作 30 秒显示“仍在处理”并允许停止后续项，不把超时等同事务回滚；Runner 回执观察 10 秒。所有等待均异步，数值待 G1 调整。
- 诊断写入也会失败；使用有界、去重的错误出口，不无限重试、不递归记录异常、不无限增加日志或内存队列。
- 监控数据积压时只保留最新采样，状态事件有界落盘；不累计每两秒一份永久快照。
- 更新/卸载不在存在活动受管任务时替换 Runner 文件。首版无自动更新服务，手动发布包给出先完成任务的说明。

## C5. Agent 切换、组合快照与编辑事务

### 1. 范围 / 触发

防止显示的 Agent 与实际执行程序不一致，或模板恢复漏掉目录隔离行为。适用于原型及原生 UI；控件文本不能作为业务状态存储。

### 2. 接口签名

```csharp
Draft SwitchAgent(Draft draft, Guid slotId, string agentId);
EditSession BeginEdit(Draft draft, EditTarget target);
EditResult ApplyEdit(EditSession edit, Draft current);
Draft RestoreTemplate(TemplateSnapshot template, ProjectContext currentProject);
TemplateSnapshot CaptureTemplate(Draft draft, DirectoryBinding binding);
Draft RestoreLastEdited(LastEditedSnapshot snapshot);
```

### 3. 数据与不变量

所有操作围绕一个可版本化 Draft，UI 选择器、摘要、布局和 LaunchPlan 从它派生。字段至少包含：schemaVersion、revision、defaultProject、orderedSlots、layoutPreference、directoryStrategy、directoryBinding、resourcePolicyVersion。

每个 slot 包含 slotId、enabled、selectedAgentId、directoryOverride、taskNote，以及 `profilesByAgentId`。每份 profile 保存对应 agentId 的 executableOverride、arguments 数组、adapter、definitionRevision；不使用单独的 slot.path 跨 Agent 复用。显示名从选中的 Agent 定义派生，不能用改名代替执行器切换。

Agent 切换不变量：

- 保存当前 Agent 的修改到其 profile，再加载目标 Agent 的 profile；无历史配置则用目标默认值，不能继承原 Agent 的覆盖路径/参数。
- 切回原 Agent 时能恢复其配置。失效/缺失 Agent 定义显式报错，不能自动绑定另一程序。
- 发布启动计划前验证 selectedAgentId、resolved executable、adapter、arguments 来自同一 profile 修订；验证中途发生编辑，旧结果失效。

TemplateSnapshot 为完整深拷贝，不共享可变 slot/profile 对象；包含选中项、顺序、Agent 配置、目录策略、目录绑定和布局。`directoryBinding=portable` 只把项目默认目录/覆盖目录转成明确的绑定规则，不删除隔离策略；需要显式独立目录却没有 portable 映射时要求补填，不默认为共享目录。`bound` 保存明确路径。模板恢复后仍需重新校验路径、占用、资源和 CLI，不恢复旧授权/PID。

“上次组合”在首版精确定义为**最近一次切换模板前的人工编辑草稿**。第一次切换离开当前草稿前捕获 LastEditedSnapshot；仅浏览或切换模板不覆盖它；用户实际修改配置后，下一次离开时更新。恢复 LastEdited 不回退为默认单开。没有快照时该选项禁用并说明原因。命名模板、当前草稿、LastEdited 是三种独立对象。

对话框打开时拷贝当前目标与 baseRevision，应用时校验 revision 后原子提交所有相关字段；取消/Escape/关闭只丢弃 EditSession，不改变 Draft 或下次对话框默认值。过期编辑返回 EDIT_CONFLICT，不能覆盖用户已作的新修改。异步旧探测结果不更新新 profile。

### 4. 校验与错误矩阵

| 条件 | 结果 |
| --- | --- |
| Agent 定义缺失/修订不匹配 | AGENT_DEFINITION_MISSING / AGENT_PROFILE_STALE；该项不可启动，不默默替换程序 |
| 模板缺少目录策略等必需字段 | TEMPLATE_INCOMPLETE；拒绝部分应用，允许明确迁移修复 |
| 编辑基线过期 | EDIT_CONFLICT；保留编辑内容，重新载入后由用户选择应用 |
| 没有上次组合快照 | LAST_EDITED_UNAVAILABLE；禁用恢复，不重置第一项 |
| 恢复后的目录被占用 | WORKSPACE_IN_USE；保留已恢复配置，但禁止提交 |

### 5. 正常 / 基准 / 异常例

- 正常：Pi 配置自定义路径后切到 Codex，只加载 Codex 自己的程序/参数；切回 Pi 能恢复 Pi 配置。
- 基准：保存“各自独立”模板，临时改共享后恢复，摘要、单选框、Draft 和最终计划都为独立。
- 异常：模板缺少 directoryStrategy，不能只加载 slots 然后沿用当前共享策略。

### 6. 必要测试

F15～F17、V09：原型与原生分别验证 Agent/profile 往返切换、模板所有字段深拷贝、目录策略恢复、对话框取消、编辑冲突、上次草稿非第一项恢复。除截图之外必须断言 LaunchPlan 的 executable、argv、cwd/mode。

### 7. 错误与正确做法

错误：只执行 `slot.name = "Codex"`；保存模板只包含 slots/layout；从单选框 DOM 读取持久状态；`last` 分支复用默认模板。

正确：按 agentId 切换独立 profile；从统一 Draft 捕获完整快照；事务式应用/取消；LastEdited 有独立持久快照和明确定义。
