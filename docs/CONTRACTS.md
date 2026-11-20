# 启动、资源与恢复合同 v0.6

状态：完整发布合同，技术预览已实现子集；实际接口/差异与证据见 [IMPLEMENTATION.md](IMPLEMENTATION.md)。采用 `trellis-update-spec` 的七段式写法，沿用 docs 目录。下面的设计签名、字段和路径是发布目标，不能声称与当前代码逐字一致。

v0.6 实测补充：所有本产品对同一配置/回执的读写须持同一文件命名互斥锁（有限等待），读端不能以 File.Exists 的瞬时结果判断创建成败；仍使用临时文件 flush、原子替换与备份。只有明确的共享错误可短时重试，损坏/权限错误不能当作空记录。Job 最后一个句柄丢失不等于成员退出；没有 Finished 或可查询的空 Job 证据时保持 Unknown，禁止释放预算。两项都须纳入 R12/R13 回归。

评审补充（CR02～CR12，技术预览接口）：以下边界由现有实现与回归约束；完整发布签名和验收范围仍按各节及 IMPLEMENTATION.md 区分。

- **准备目录**：写 Git/准备记录前，比较所有最终独立目标、同批直接目录和最新状态中的活动工作区。重用目录须同时核对 intent、保存的物理身份、common Git dir 和 worktree 登记；相同分支/OID 不足以证明身份。准备期间新占用仍由 reserve 拒绝。
- **监控连接**：400 ms 探测超时不能单独证明监控器不存在。启动备用 Worker 前，读取有界 PID 提示并核对进程仍存活、程序路径匹配；PID 只用于减少重复拉起，不是授权/资源归属证明。单实例 mutex 仍是仲裁依据；请求字节发出后不因超时自动重放预约。
- **状态与账本**：单帧 1 MiB，连续响应最多 64 帧/16 MiB，全部帧一致且完整后才更新 UI。新计划必须能完整回传才允许落盘。已有单条活动记录超界时，明确报告省略的有界 ID、返回 `Ok=false` 和准入阻止原因，其余记录完整返回并保留撤销入口；异常原记录、预算和目录占用不删改，恢复需人工核对。普通终态账本历史按最旧优先裁剪到 6 MiB，活动项不裁剪；8 MiB 读取拒绝上限不变，已损坏/超界账本不自动修复。
- **取消与检查**：准备/提交停止后，先解锁配置再等待撤销。预约结果未知的补偿清理也先通知 UI 解锁；清理完成前独立门禁禁止启动和单项重试，不能依赖刷新后的 Pending 列表维持门禁。命令检查只有一个 Dispatcher 消费者，所有调用者等待同一任务；最后一次待检查判断至清除运行标志之间不得加入异步等待。
- **编辑恢复**：移除撤销只属于当前项目/草稿，切换项目（含最近项目）或恢复命名/上次组合后失效；同项目选择与普通会话重绘保留。配置读取必须给出实际结构错误，不能由无关缺字段错误遮蔽 null 模板校验。

对应回归：`WorkspaceReviewChecks` CR02/CR12、`RuntimeReviewChecks` CR06/CR10、`UiReviewChecks` CR03/CR07/CR09、`UiChecks` null 模板，以及丢失 reserve 响应的补偿测试。`RequiresDirectory` / `IsPortableTemplate` 的缺省兼容限制仍见 IMPLEMENTATION.md §11，未改变 schema。

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
| schemaVersion | 整数，当前 2；兼容读取首版 1 并经明确迁移升级到 2；未知未来版本只读拒绝执行 |
| launchId / attemptId | GUID；尝试 ID 不复用 |
| createdAtUtc / appVersion | UTC 时间与实际构建版本 |
| windowName | 根据 launchId 生成的唯一 Terminal 窗口名；不操作 `last` 窗口 |
| panes | 1～16 个有效项的不可变、有序数组；不含停用配置 |
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

`Started` 回执只表示受控进程已启动，不表示 Agent 已就绪或任务完成；任务是否完成仍由 CLI 输出、退出码和用户审阅决定。

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

窗格美化走 Windows Terminal 官方 JSON fragment 扩展点（`%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments\AgentLauncher\`），只写本产品自有 fragment 文件，从不读写用户 settings.json；fragment 仅在 Terminal 进程启动时加载，因此仅当所有运行中的 Terminal 进程都晚于 fragment 写入时间启动才在窗格命令携带 `--profile AgentLauncher`，否则本次启动不带 profile（行为与此前一致），并在启动提示中声明美化延后生效。profile 缺失或解析失败必须表现为窗格外观回退默认，绝不能导致启动失败。`closeOnExit: graceful` 只改变窗格收尾：退出码 0 自动关闭，非 0 保留现场；不改变任何准入、回执或资源语义。

选择器解析只检查受控 PATH/PATHEXT 与显式程序位置，排除将项目当前目录当作隐式命令来源。PowerShell 函数/别名不属于可执行程序支持范围。

`.exe` 使用结构化参数；`.cmd` 只支持经验证的 npm shim 适配，必须保留其正确调用语义。不能只靠 `ArgumentList` 宣称已经解决 CMD、Windows Terminal 分号和脚本多层解析。任意 `.cmd/.bat/.ps1` 不在首版通用支持范围，不静默用 shell 执行。Node 运行时选择镜像各模板自身语义：npm shim 模板优先相邻 `node.exe`，Pi shim 模板（`SET "node=node"`）走 PATH；适配器不得统一替换其一。

命令发现与可执行适配分开：存在同名文件只显示“找到命令”，启动前仍需 `Resolve` 验证执行类型。PATH 项先去外围引号再判断绝对路径，候选按 Windows 大小写规则去重；不隐式搜索项目当前目录。Terminal 可用当前用户注册包路径补足失效的执行别名，查询不通过 shell 执行。

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
- 基准：16 槽配置只启用第 1 项；不探测或创建其余 15 项的 Agent 进程。
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

`ResourceSnapshot` 至少包含 sampleTimeUtc、单调采样时间、systemBootId、可见物理内存/可用内存字节数、commitLimit/commitUsed、整机 CPU 区间比例、每个受管 Job 的提交/私有驻留量和数据可信状态；数值缺失不填零。`AdmissionDecision` 返回 `Kind`（`Allow`/`Deny`）、`Advisory`、reasonCode、policyVersion、规划量/硬上限加总、可解释的缺口及下一步；只有允许时才产生唯一 reservationId。`Advisory=true` 表示实用/自定义模式的非阻断提示：调用方按允许处理，但必须展示原因；协议与账本没有 waiver、确认记录或有效期字段，提示不跨批次复用。

预算预约与启动授权分开存储。授权包含 grantId、reservationId、launchId、paneId、attemptId、generation、controllerEpoch、systemBootId、planDigest、state。状态为 `Pending → Creating → Running → Settled`，Pending 可进入 Revoked；Creating 必须按 C1 核对后才能进入 Revoked/FailedBeforeStart。没有授权，单凭计划和 claim 不得执行。

- `TryBeginSpawnAsync` 在同一短事务中验证授权仍 Pending、摘要和代次匹配、预约/目录占用有效，再持久化 Creating；重复 requestId 只返回原决定，不新建授权。确认消息丢失时按 Creating 核对，不能另领一份。
- `RevokePendingAsync` 对 Pending 授权持久化 Revoked 并增加代次；撤销成功后释放对应预算/占用。晚到 Runner 即使读到旧 plan，也必须被监控器拒绝。撤销与领取互斥，不能先释放预算再落盘撤销。
- 发现 Creating/ResumeIntent/Unknown 时返回 NeedsReconciliation。只有取得 attempt 锁、核对创建者/Job 并确认旧尝试不会再执行，才可终结授权与释放预算。不能只凭 TTL、PID 不存在或主 CLI 已退出作结论。
- 监控器重启先恢复授权账本、切换 controllerEpoch 并核对活动 attempt；旧连接的授权不能离线使用。Runner 在创建前及写 ResumeIntent 前核对当前授权，监控器不可用则不继续启动。恢复后只能将已核对的原 attempt 绑定到新 epoch，不为同一尝试再授予一次创建权限；旧 epoch 请求返回需要重新核对。
- 以持久化撤销记录拒绝迟到请求；没有活动进程/未解决的 Creating 后，纯历史 Pending 已撤销记录不阻止监控器退出。用户重试使用新 attempt/grant。终态控制账本记录可按上述大小预算裁剪，旧 ID 缺失后返回不存在/过期，绝不能据此自动重建授权。

### 3. 预算、线程与存活合同

保护模式初值：

| 参数 | 初值 / 解释 |
| --- | --- |
| 初次启用项 | 1；用户可选 2～16，需准入 |
| 全部受管负载的 CPU 上限 | Agent、Git 准备及其受管后代合计整机 35%，一个公共外层 Job，`CpuRate=3500`；不是每格/每阶段各 35% |
| 每会话提交内存上限 B | 默认 2 GiB；可配置范围 1～8 GiB，包含归属到该 Job 的工具子进程 |
| Git 准备预算 | 规划 256 MiB/操作、Job 提交上限 1 GiB；包含 filters/hooks 后代；所有仓库全局最多 1 个准备操作 |
| 目标盘保留空间 | 初始 `max(2 GiB, 目标卷容量的 5%)`，检出估算另加；不冒充磁盘硬限额 |
| 受管内存预算总上限 | `min(8 GiB, 0.35 × 系统可见物理内存)` |
| 物理内存保留 R | `max(1.5 GiB, 1/12 × 系统可见物理内存)` |
| 系统提交余量保留 Rc | `max(1.5 GiB, 1/24 × 系统提交上限)` |
| 可用采样有效期 | 3 秒；过期先重采样 |
| 运行中采样与轮询 | 监控循环 1 秒接收/刷新节拍，需要重采样时最短间隔 2 秒并共享一个采样器；UI 状态轮询按进行中 1 秒、有活动 2 秒、空闲 30 秒自适应；优先原生计数器与通知，不周期运行 WMI/PowerShell |
| 恢复等待 | 压力解除连续 5 次采样后才放开新准入；不自动启动旧请求 |

记 `A` 为可用物理内存，`H` 为系统提交余量；当前各会话硬上限为 `B_i`、提交规划量为 `P_i`，实测 Job 提交量为 `C_i`，私有驻留量为 `W_i`。准入按 Kubernetes requests 模型预约规划增长：提交余量预约 `Uc=Σmax(0,P_i-C_i)`（用量未知的会话按全额 `P_i` 计，不抵扣），物理余量预约 `Up=Σmax(0,P_i-W_i)`；不能拿已换出的提交内存当作已占用物理内存。硬上限 `B_i` 仍由内核 Job 对象强制执行，是兜底而不是预约额度；按「剩余硬上限全额」预约曾使一台 16 GiB 的机器在 3 个空闲会话下无法再启动任何 Agent。新请求的规划总量 `Np`、硬上限总量 `Nc` 需要同时满足：

```text
全部已预约硬上限 + Nc <= 软预算（保守模式超出即拒绝；实用/自定义模式只提示）
A >= R + Up + Np
H >= Rc + Uc + Np
整机最近一个有效 CPU 采样区间 < 80%（保守模式拒绝；实用/自定义模式提示）
采样、归属与账本均可信
```

这是保守准入，不是未来空闲内存保证；提交内存与驻留内存不同，外部进程仍会变化。2 GiB 是硬上限，预约只按规划量估算增长，不是预测每个 Agent 一定占 2 GiB。账本同时包含 Git 准备 Job 与所有 Agent Job，禁止只累计 Agent。批次按整体预算先预约，不足时拒绝文案给出缺口 GiB 数并指引改用「按资源依次启动」；进程按顺序受控启动。各项 Job 上限加总若溢出（饱和至 ulong 上限）则直接拒绝，两种模式都不能用提示放行。

**准入模式（2026-11-15）：** 实用（默认）与自定义模式把物理/提交余量、整机 CPU 与软预算的读数不足降级为提示：`Evaluate` 返回 `Allow` 且 `Advisory=true`，文案说明缺口并声明「实用模式不拦截，进程内存硬上限仍然生效」。保守模式保留全部硬拒（物理保留 R、提交保留 Rc、CPU ≥ 80%、共享池规划、Job 上限加总超软预算）与原有可执行文案。两种模式都拒绝且不产生授权：采样无效或过期、策略校验失败（含保留线非法）、各项 Job 上限加总饱和溢出。`begin`/`resume` 按控制记录里冻结的模式判断（`PolicyFromEntry`），不因用户随后切换模式热放宽已预约批次；提示也不写入账本作为可复用凭据。参考：Ray 的按资源限制并发运行任务、Kubernetes requests 准入与 limits 内核执行分离、earlyoom 约 10% 空闲内存阈值；只借鉴模式，不引入依赖。

**同时运行 Agent 窗格上限（2026-11-15）：** 固定并发池 `MaxConcurrentAgents = 8`。`reserve` 与 `reserve-task` 在写入资源池、账本条目或任务领取之前，统计「非终态且 Kind 为 agent（旧记录缺省同类）」条目，加上本次请求中的 agent 项；合计超过 8 即拒绝并提示「同时运行的 Agent 窗格最多 8 个；请先结束或撤销部分 Agent 后再启动。」。本次请求已覆盖的账本条目（原 Id 的查询式重试）不双计；Git 准备项不计入名额，其全局最多 1 个准备的串行限制不变。上限在保守与实用/自定义模式都生效，与资源读数提示无关，也不消耗/复用任何确认；保存容量 `MaxSlots = 16`、单批 panes 1～16、`Draft.Validate` 与配置持久化契约保持不变，超限只阻止新增而非改动已保存配置。本机与远程的非终态 agent 都计入这 8 个名额，含尚未操作员释放的远程 `Disconnected`；见 C14。内存与 CPU 准入仍然不把远程批次算进本地压力。

共享池的提交增长按池内成员规划量之和减去整个池的实测提交量计算（不足为零），在已有项和新请求之间只计一次；不能累计叶 Job 的私有提交代替池读数。未知池读数按成员规划全额计，不给抵扣，空 Job 不使用仍由退出进程句柄保留的残余记账；请求自行携带的用量不获信任。Job 读数前后取系统可用量较小值，保持采样有效期，不把依次读取的计数器称为原子预留。`status.Budgets` 是短期预览数据，所有分页必须一致；未变化响应仍更新该数据，旧 Worker 缺少字段时按未知用量保守处理。若用量字段挤占原本可传输的任务记录或分页总预算，完整省略用量字段并按未知处理，不能裁掉任务记录或只传部分抵扣项。最终授权重新查询，不把界面预览当作授权。

### Omp 工具前置保护（2026-11-13）

- 新的 Omp 命令解析时把随发布包的 `omp-memory-guard.mjs` 加入结构化 argv，因此它参与不可变计划摘要；不在 Runner 中偷偷改参数，不改写旧排队记录。Runner 在恢复初始线程前创建当前用户限定的独立管道，只给该子进程传入管道名，不修改父进程或用户全局环境。相同组件移到另一个完整发布目录仍可核验；缺失或不同内容要求重新入队。
- 根会话及 task 子会话都使用 Omp `tool_call` Hook，在工具启动前读取当前 Job、共享池和整机余量。未知/断连/无效读数阻止本次新工具；单次连接等待上限 1.5 秒，协议最多 4 KiB。Hook 不等候同一工具批次中的工作结束，避免参数准备阶段死锁；`wait` 仍可收取已有任务结果。
- 当项或池余量低于 `max(512 MiB, 对应上限 / 4)`，或整机物理/提交余量不足以覆盖本次冻结策略的保留值加单项工具提前量，返回可重试的工具错误；已有工具继续运行。Omp `task.maxConcurrency` 通过支持的运行时 registry 限到最多 2，保留更低值。不要缩小共享 `async.maxJobs`：父任务等待子任务时可能占着同一调度器名额。
- 保护目前验证 Omp 18.20.0。必须测真实 agent-loop 和真实 task 子会话；直接 RPC `bash` 不是该 Hook 的替代验收。运行时 override 不写配置。资源设置提供显式的 4 GiB 独立上限 / 1536 MiB 规划预设，用户应用后只作用于新批次；不迁移活动/已排队任务的额度，也不热放宽 Job。
- 该机制属于提前背压。多个已获准工具、一个工具突发大分配、模型/扩展内部的非工具工作仍可能跨越剩余空间；不能将 Hook、固定并发数或一次不崩溃测试宣称为任意任务永不 OOM 的保证。共享嵌入服务仍需单独核对 Job 归属；不启用 breakaway，不杀其他窗格的服务，不自动重放工具写操作。
- 加载经同一管道握手确认：扩展加载成功后立即发送 1 字节 `0x02` 通告，Runner 在恢复初始线程后最多等待 3 秒；未确认时只在运行记录中明示「Omp 内存保护未确认加载：工具不受提前限流（硬上限仍生效）」，不终止会话、不重放工具写操作。
- 版本探测执行剥离守卫参数的裸命令（`Detach`）：`--version` 不加载扩展，脚本类 CLI 的位置参数语义不被探测改变；程序身份指纹仍覆盖含注入参数的启动计划。
- 旧账本条目未冻结保留值时，守卫按固定 2 GiB 物理/提交余量执行提前背压；新预约始终冻结当次策略值。

### Qoder 工具前置保护（2026-11-14）

- 新命令通过结构化 `--plugin-dir` 加入独立插件，参与冻结计划摘要；保留原参数、其他插件、settings 与权限。版本探测剥离此参数。插件位于启动器自己的 `memory-guards/`，身份含 Worker 绝对路径及程序集构建身份；Runner 在恢复初始线程之前原子生成并验证内容，不改写 Qoder 或项目配置。旧计划绑定的构建/位置不符则要求重新入队。
- 插件只注册同步 `SessionStart` 和 `PreToolUse`，以 `command` + `args` 直接执行随包 Worker 的 `qoder-memory-hook`，不经过 shell。SessionStart 发送 `0x02` 握手，PreToolUse 发送 `0x01`，复用同一 Job/池/整机策略。管道只含单字节请求和内存决定，不含工具参数。
- Worker Hook 的总输入/连接/读取期限 2 秒，输入最多 1 MiB、JSON 深度 32、响应最多 4 KiB。内存不足、断线、超时、无效输入均返回 `hookSpecificOutput.hookEventName=PreToolUse` 和 `permissionDecision=deny`，进程返回 0 让 Qoder 解析决定；资源充足只返回 `{}`，不得用 `allow` 绕过用户授权或其他 Hook。
- Qoder 宿主未加载插件、无法启动 Hook 子进程或宿主先触发 Hook 超时，可能按非阻断错误继续；此时不能宣称完整 fail-closed。Runner 最多等 3 秒握手，未确认则在回执明示保护未确认，保留硬上限。握手仅证明 SessionStart 到达，不证明全部工具/子会话覆盖；尚未验证的版本不承诺兼容。
- 本机 Qoder 1.1.89 的插件格式、重复 `--plugin-dir` 共存、Worker 协议已验证；隔离真实会话在 Hook 前要求登录，真实 PreToolUse 调度和子 Agent 覆盖尚未验收。未臆测 Qoder 的等待/取消工具名，不设置豁免，也没有修改其内部并发；已有进程继续运行，用户仍可手动中断。该 Hook 不能阻止已启动工具、模型上下文或宿主分配突然触顶。

Git 准备走同一受保护创建接口，包含检出及继承的 filters/hooks；全局准备上限 1，在其内部再按仓库锁串行。准备预算在活动成员归零、操作结果核对完成后才释放，再重新评估 Agent 准入。既有 Agent 工作时启动新项目，也要把两类负载计入同一总配额。

准备前检查目标卷可写和可用空间，流式读取 Git 已跟踪文件尺寸估算，不扫描所有工作文件或将整个 Git 输出载入内存。基础门槛为“保留空间 + 估算检出大小”；filters、LFS 展开可能大于估算，显示该限制。准备中空间触及保留线则停止新增操作并提示，不自动清理用户目录；当前写操作按 C4 处理。磁盘检查无法保证不耗尽，P09 必须验证真实准备负载下的桌面响应，未通过不得发布。

如果 A 低于 R，或整机 CPU 连续三个 2 秒区间高于 80%，进入 Pressure。保守模式禁止新增并给出可读提示；实用/自定义模式只提示不拦截（Job 硬上限仍生效）。提示限频，不靠轮询弹窗。严重压力只建议用户在终端保存并正常结束不需要的任务，不自动强杀正在写文件的 Agent。

进程组：外层 Job 限制全部受管负载的总 CPU；内层会话或 Git 准备 Job 限制内存并提供归属。所有限额在进程创建时关联，并在初始线程恢复前核验；不启用允许后代逃逸的 breakaway 设置。Terminal、GUI、监控器、Runner 不进入该 CPU 限速组，其资源另行预算。已有嵌套 Job/Windows 环境不支持要求时失败返回，不能默认放行。

内存硬限额让超额分配失败，可能导致编译器或 Agent 退出；不会保存所有运行内存。只设置通知上限并不能约束内存，因此产品必须区分“提示阈值”和“硬预算”。不在运行中把内存硬限额缩到当前使用量之下。需要更多预算的任务在退出后重新评估；首版不提供一键解除保护。

根 Job 限额跨会话一致，新增 UI 实例不能重新创建另一个相同配额组绕过总限额。监控器与 Runner 保留必要 Job 句柄；不设置 `KILL_ON_JOB_CLOSE`，配置窗口关闭/监控器退出不会主动杀已有任务。监控器恢复需通过 Job 名称、会话记录、进程创建时间和当前系统启动身份核对；不能只凭 PID 重用旧记录。

如果主 CLI 已退出但子工具仍在 Job 中，状态为“Agent 已退出，工具仍运行”，保留预算和监控；Job 活动进程归零后才释放预约。重启后历史记录不算仍占预算，但新启动前需核对当前系统身份。没有受管进程、未完成提交或 UI 准入请求时，监控器在空闲 15 秒后退出；任何已接受的连接都会重置空闲计时。

线程约定：UI 一个 Dispatcher；目录/命令探测最多 2，配置写入 1、全局 Git 准备 1（内部同仓库写入 1）、受控进程提交 1。异步不等于创建线程；阻塞调用使用有界执行器，过期结果丢弃。不能为了取消一个无法中断的系统调用无限补开线程。

### 4. 错误矩阵

| 条件 | 返回 | 行为 |
| --- | --- | --- |
| 内存/提交余量不足 | RESOURCE_MEMORY_LOW | 保守模式不启动，不改为共享目录规避；实用/自定义模式允许启动并提示（`Advisory`），硬上限仍生效 |
| CPU 已忙 | RESOURCE_CPU_BUSY | 保守模式等待用户重新检查，不自动排队重试；实用/自定义模式提示后允许启动 |
| 无可信采样或归属 | RESOURCE_STATE_UNKNOWN | 所有模式都禁止新准入，允许查看/恢复已有状态 |
| 同时运行 Agent 窗格超过 8 个 | RESOURCE_CONCURRENCY_LIMIT | 拒绝并提示结束或撤销部分 Agent；Git 准备不计入，查询式重试不双计；保守与实用/自定义模式都生效 |
| 配额 API 失败 | PROTECTION_UNAVAILABLE | 不恢复未运行子进程，给出平台原因 |
| 会话达到内存上限 | MEMORY_LIMIT_REPORTED | 有 OS 事件才这样归因；保留退出码和工作目录 |
| 外部程序造成压力 | SYSTEM_PRESSURE | 保守模式限制新增；实用/自定义模式只提示；不宣称可限制别的软件 |

磁盘和 GPU 并没有被 CPU/内存 Job 配额完全保护。Git 准备已纳入 CPU/内存总量，并进行全局串行和磁盘余量检查；若受支持的常见任务仍导致持续磁盘卡顿，性能验收失败，必须减小默认负载或补充经验证的控制，而不是把问题归给用户。本地大模型、外部常驻服务不属于首版性能保证范围。

### 5. 正常 / 基准 / 异常例

- 正常：同机有足够余量，两会话总预算通过；两个 CPU 压力子进程的合计受 35% 约束。
- 基准：当次只启用一个 Agent，其他配置不产生进程；关闭 GUI 后只有监控器/Runner 的小额辅助开销。
- 异常：本机采样 15.2 GiB 总内存、1.2 GiB 可用，低于 R；保守模式应返回 RESOURCE_MEMORY_LOW 并拒绝，实用/自定义模式放行但显示缺口提示且 Job 上限不变。两种情况都不去宣称“启动后不会卡”。

### 6. 必要测试

P01～P09、R06～R08、R14、R15、R18～R22：预算并发预约、撤销与迟到 Runner、Git 准备与 Agent 的合计限额、监控器故障、内存超额、正常退出后资源释放、两种模式下的提示与拒绝、并发窗格上限、压力仅保守阻塞。压力测试用隔离环境和受限测试程序，不直接耗尽用户当前桌面。

### 7. 错误与正确做法

错误：每格各给 CPU 上限 35%，多格相加可以占满机器；最小化 GUI 就报告“内存已释放”。

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

Git 创建命令为 `git worktree add -b <branch> <targetArg> <oid>`，工作目录固定为已解析的物理源仓库根。`targetArg` 采用 `Path.GetRelativePath(root,target)` 与绝对 `target` 中更短的表示；跨卷不能相对化时保留绝对路径。只改变命令参数写法，落盘目标、碰撞检查和身份核对仍使用原绝对目录。Git 会把该参数形成的 `<targetArg>/.git` 传给检出子进程；不必要的长绝对参数可触发 `'$GIT_DIR' too big`，`core.longpaths=true` 不足以消除这个限制。失败仍保留原有诊断与准备状态，不修改用户 Git 配置、缩短实际目录或拆成可破坏恢复语义的额外 reset。

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

首版不自动删除计划、claim、Unknown 记录、分支或 worktree；普通日志轮转，控制账本只按上述大小预算裁剪终态历史。避免延迟 Runner 需要的计划被“按天数”删除。其他清理必须先定义运行归属证明。

明确的用户发起 worktree 清理（2026-11-13，参考 BloopAI/vibe-kanban 的编排器自有 worktree 回收，取保守变体）：「工作目录」窗口对每条记录提供「清理目录」。门禁：无活动会话占用该路径（占用判定必须用 monitor 实时完整状态——轮询显示缓存可能是预览/过期，monitor 不可用或条目不完整一律拒绝；与监控条目按 PhysicalDirectory 物理身份比对，子目录锚定或路径拼写差异不影响检出），且 `git status --porcelain`（含 `--ignored=matching`）在该目录完全为空（任何已跟踪修改、未跟踪文件或被 .gitignore 忽略的内容——如本地 .env、构建产物——都阻止；Git 自身的移除检查看不到被忽略内容，不设此门禁会被静默删除）；清理与启动互斥（工作目录窗口打开期间队列泵停止派发，有启动/派发在途则拒绝清理），确认后删除前再次实时核验占用与目录状态，期间变化即取消。执行用不带 `--force` 的 `git worktree remove`，git 自身再次拒绝脏树作为第二道防线。只移除检出目录与启动器记录；分支与全部已提交内容始终保留，不做分支删除、不做强制删除、不做定时清理。无法确认 Git 状态时一律拒绝并提示手动核对。

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

CR12 合法兄弟目录及非重叠活动目录还须覆盖长临时根（与 Windows CI 路径长度相当），验证原物理目标、分支/OID与隔离身份保持不变；不能以缩短测试根或跳过 Git 来获得通过。

### 7. 错误与正确做法

错误：路径字符串不同就当成两个仓库；用 `finally` 删除所有新目录；JSON 解析失败直接覆盖空配置。

正确：核对真实 Git 身份和记录；保留可恢复成果；保留损坏样本与有效备份，由用户选择恢复。

## C4. 异常、取消与稳定性约定

- 每个异步入口观察异常并返回结构化错误，除 UI 事件外不用 `async void`。错误转换带 code、stage、retryability、safeMessage、diagnosticId，不向普通 UI 填整段堆栈。
- `DispatcherUnhandledException` 只覆盖相应 UI 线程。Task 和后台线程异常在自身边界处理；不能只注册一个事件就声称防崩溃完成。
- 可恢复错误留在当前步骤；未知致命错误停止新提交，尽力记录后退出相关组件。OOM/StackOverflow/断电不能保证 handler 执行或无损保存，依靠事前持久化。
- GUI 关闭/崩溃不携带结束 Agent 的 Job 句柄策略；监控器故障时配额留在现有 Job 上，新请求阻止。恢复失败显示 Unknown，不启动重复监控组。
- 资源组句柄丢失（创建者中断、Job 内核对象已不存在、回执不能证明后代全部退出）时，监控器保留对应预算并暂停新启动。恢复不只有系统重启：界面在监控器报告此类阻塞时提供「核对并释放占用」，经用户明确确认后由监控器逐条做 fail-closed 证明——资源组内核对象不存在（Win32=2）、创建者 gate 不在运行、且满足以下任一退出证据：回执为 `Finished`（运行器已按 Job 成员枚举证明全部受管进程退出）或 `FailedBeforeStart`（进程从未运行），或回执记录的进程启动时间早于本次系统启动（重启后的进程不可能由重启前的进程派生，整树必已随上次重启结束）——全部满足才将该记录标记为已结束并释放预约。仅父进程退出的记录不算数：产品 Job 不设 KILL_ON_JOB_CLOSE，句柄销毁后派生进程可能仍在运行且无法枚举核对，此类记录与进程存活的记录一样保持阻塞并写明拒绝原因。该操作不结束任何进程、不删除任何记录；无法证明的记录仍只能由系统重启兜底（重启后启动时间证明生效，核对释放即可放行）。
- 用户取消先停止未开始的准备，并请求撤销尚未领取的启动授权；只有撤销落盘后释放预算/目录占用。已领取或正在创建的项先核对，已运行 Agent 继续工作。Git 写操作不强杀后删除锁，等待结果或标为需要核对。
- 命令发现只做路径/PATH/PATHEXT 与适配类型解析，不执行 CLI 版本探测；找到命令不代表版本或登录已验证。Git 已开始的写操作允许完成，取消在操作边界生效，不把取消当回滚；Runner 回执观察 10 秒。所有等待均异步，具体超时待 G1 调整。
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
- 参数数组直接由逐项控件读取，不通过 `Join("\n")` / `Split('\n')` 往返；空参数、含换行的单项和顺序均属于执行合同。

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

## C6. 快速启动转资源等待队列

- 「按资源依次启动」是显式授权入口，不是降低内存门槛。确认前冻结有序启用槽、原始 profile 参数、解析后的 CommandSpec、程序指纹、物理工作区身份与目录目标；取消不保存任务、不准备目录、不创建进程。当前批次所有任务在同一次任务文件事务中加入；重复点击或相同快照仍等待/运行时不得重复导入。
- 确认框默认仅执行新批次，并发 1，保存 `LastDispatchTaskIds`，不改全局并发；其他等待项不由本窗口派发。明确选择「整个队列」才清除范围并把全局并发设为 1。仅新任务副本改为独立内存额度，单项上限、规划、CPU 与系统余量保持原值；原快启配置与活动池不改。排队不等于预约，仍由 monitor 每次重新核对硬保护；界面出现过的提示不能作为排队项通过准入的依据，也不保存任何确认凭据。
- `tasks.json` schema 2 增加 `QuickLaunchSource`（导入 Key、来源槽/位置、物理工作区 Key、冻结 worktree 根、是否强制独立目录），schema 3 增加归档、暂缓与执行范围。schema 1/2 只在内存中迁移，正常修订写入才保存为 3；未来/损坏版本拒绝覆盖。旧程序必须拒绝 schema 3，不能忽略新调度语义执行。
- 目录准备之前检查资源。原同仓库多槽的 isolated 语义不能因拆成单项而丢失；每项获准后用 `forceIsolated` 创建自己的 linked worktree。源身份再次核对，脏源不自动确认，只能人工处理后继续。成功派发把任务绑定到实际 prepared 目录供后续审查/续接使用，保留导入来源 Key；失败和取消不删除已经创建的目录。
- 队列使用单独的 `reserve-task` 操作与 `TaskEntryId`。monitor 从持久化任务文件读取资源快照，核对队列项/修订、程序/参数/指纹、工作区与并发，再走原资源准入、预约、begin/resume 与 Job 保护；不信任客户端传来的资源额度。没有新操作的旧 Worker 拒绝请求。当前根 CPU 与冻结配置不符时等待，不热改 Job。
- 暂停/关闭停止后续派发并等待本次准备/预约收尾；已运行任务继续。只在确认 Revoked 时才把未启动尝试标为取消；预约/提交/撤销结果不明则暂停并保留记录，禁止自动重放。
- 队列为空且没有非终态尝试时，不请求 status、不启动 monitor，并停止队列计时器。有运行项或 Unknown 证据时继续核对，不能为节省轮询丢失回收责任。重新打开窗口仍是暂停状态。

### 队列领取权：预约先于 TaskRun

1. **范围 / 触发**：`reserve-task` 在 App 调用 `StartRun` 前已经产生预约；两个客户端或 monitor 重启都不能为同一队列意图再生成一个尝试。客户端互斥与 `TaskBook.Attempts` 不是这个窗口的领取权威。
2. **签名**：`TaskReservationClaim(Guid EntryId, Guid AttemptId)`；`Ledger.TaskClaims` 是 `control.json` schema 3 中的列表；`ReplayTaskClaim(Request, LaunchItem[])` 读取原领取，`Save()` 同时发布领取与预约 Entries。旧 Worker 拒绝 schema 3，schema 1/2 仍可迁移。
3. **数据合同**：EntryId / AttemptId 均非空且各自唯一；新预约必须在写入资源池、Entries 或 TaskClaims 之前校验，不能只靠重启读取时检查。保留的 AttemptId 不能经 `reserve-task` 或普通 `reserve` 复活；原记录仍在的合法同计划查询式重试保持幂等。完整计划与资源策略决定同一尝试重试是否相同。领取不是展示历史：Revoked、终态剪裁、boot rollover 都不清除；保留最多 `MaxAttempts + MaxQueueEntries`（1200）条。达到边界停止新预约，不删除证据以放行。schema 3 起的原始 JSON 必须显式包含非 null 的 `TaskClaims`；schema 1/2 缺省字段才允许迁移为空列表，迁移只发生在内存。新执行意图必须由明确重新入队产生新 EntryId。
4. **校验 / 错误矩阵**：

   | 条件 | 结果 |
   | --- | --- |
   | 已领取 EntryId + 新 AttemptId | 拒绝，指出原尝试；不修改账本 |
   | 原 AttemptId + 相同计划/策略，原记录仍在 | 返回原记录，不创建预约，StartRun 是否已消费队列不影响幂等 |
   | 原 AttemptId + 不同计划/策略 | 拒绝陈旧或篡改重试 |
   | 原尝试历史已裁剪 | 明确拒绝重执行，保留领取 |
   | ledger 原子替换失败 | 预约与领取的内存增量一起回滚，无成功响应 |
   | 旧版同任务预约尚未记入任务历史 | 无法证明领取身份，阻止新领取而非猜测 |
   | 空 / 重复身份、超量领取、未来 schema | fail-closed，禁止覆盖或执行 |
   | schema 3 缺失 / 任意版本显式 null 的 TaskClaims | 拒绝读取，不改写主文件或备份；缺失字段不等于没有领取 |
   | 队首已领取但尚未写入 TaskRun | 命令解析与目录准备前暂停并停 timer，保留队列和领取；显示原尝试及状态 |
   | 已确认 Revoked，但 TaskRun 未落盘 | 暂停并提示「原因与恢复」；核对身份后由用户明确重新入队，不自动换 EntryId |
   | 原尝试状态 / 历史未知或已裁剪 | 不把重新入队视为安全；先核对回执与作业树，禁止自动重放 |

5. **正常 / 基准 / 异常例**：正常为两个客户端竞争仅一个计划获准；基准为原计划查询式重试取回同一 Pending/Revoked 记录；异常为持久化后响应丢失、monitor 重启仍不得另建计划补发。
6. **回归断言**：QQ-C01/C02 用两个独立 pipe 客户端，故意不写 TaskRun，验证唯一预约与重启；QQ-C03 实际阻止 File.Replace，验证无幽灵领取；QQ-C04 验证撤销、历史剪裁与 boot 变化；QQ-C05/C06 覆盖旧记录不确定性与身份/数量校验。QQ-C07～C12 从真实 IPC / 原始 JSON 入口验证拒绝先于写入、下一合法预约仍可用、重启可读和普通 reserve 不能复活裁剪身份。WPF `RunQueueClaimRecoveryChecksAsync` 验证暂停发生在准备之前、任务文件/领取不变、明确移出再入队产生可接受的新身份。均不调用真实模型或启动用户 Agent。
7. **错误与正确做法**：错误是“TaskBook 暂无运行项，所以换新 plan.Id 再 reserve”，或“JSON 初始化为空，所以执行权威字段缺失也安全”；正确是先核对持久 EntryId 领取和字段存在性，再按原 AttemptId 取回结果或明确拒绝。App 读取账本只用于保守阻止准备，不代替 monitor 的预约权威；IO 在后台完成，失败暂停。这里保证至多一次预约归属，不承诺业务任务跨崩溃必定成功。

## CR-N01～N04：激活、任务恢复与程序身份（2026-11-12）

- `begin` / `resume` 都必须拒绝配置权威不可读、账本写失败锁定或恢复核对阻止态。恢复已冻结的 Creating 授权不重新扣除资源预约（提示不消耗额度），不因新的配置 CPU 目标而改写已有 Job；持久化阻止态不能被正常的 epoch/digest 校验绕过。
- `tasks.json` 缺失且仍有 `.bak` 时，Load / Update 都拒绝，Update 的修改回调不能执行，主文件与备份均保持原状。只有主文件及备份都不存在才视为首次使用；人工核对并恢复有效主文件后才可继续。不得自动恢复、清空或覆盖保留证据。
- 普通 `reserve` 的 `RetryOf` 只能重试可证明为普通快启的未启动记录。沿祖先链核对 `TaskClaims`、TaskRun.AttemptId 与 SourceSlotId 对应的任务；缺失祖先、循环引用或无法读取任务文件都不能证明为普通快启。任务失败要从任务卡明确重新入队，生成新 EntryId / AttemptId，继续遵守 MaxRunning 与尝试历史。UI 隐藏此类「仅重试此项」，执行入口重新读取任务元数据，Worker 独立拒绝绕过 UI 的调用。
- 通过 `node-shim` 解析的程序指纹使用版本化结构摘要，包含适配器、规范化运行时路径及原指纹、入口脚本路径及内容摘要，以及向上最多 8 层找到的最近 `package.json` 路径及内容摘要（或明确不存在）。入口文件上限 64 MiB，包文件上限 1 MiB；超限、不可读、入口缺失或不支持的适配器一律拒绝身份核对，不退回运行时指纹。原生 exe 的原指纹算法保持兼容。
- 用户参数与会话标识不属于程序身份，仍由独立结构化 argv / 冻结命令核对。快启转队列的缓存按运行时、适配器、入口区分，不能将共享 node.exe 的不同 CLI 混用指纹。版本探测前后核对同一身份，变化则拒绝记录该能力。
- `reserve-task` 核对完整程序指纹；携带持久任务领取的 `begin` / `resume` 从对应运行记录或尚未消费的原队列项取得冻结指纹并再次核对。身份缺失、错配或升级都在创建 Job / 返回恢复授权前拒绝并保留预约；不能靠重新读取当前程序来重写旧授权。外部安装器在核对放行后继续替换文件仍不属于跨进程原子事务，本合同不承诺锁定整个依赖目录。
- 老版只绑定 node.exe 的任务与会话指纹不自动迁移为新身份。普通排队项须人工移出再入队；旧会话不能用新程序身份登记或续接，须重新核对并新建任务。迁移不降低未知占用保护，不删除旧尝试或领取记录。

| 回归 | 必须拒绝的触发 | 拒绝后保留 |
|---|---|---|
| CR-N01 | 真实 File.Replace 失败后激活，包括下一轮 Refresh | 原磁盘字节、Pending / Creating 授权 |
| CR-N02 | 有备份的主任务文件丢失 | 备份字节，且不调用 Update 回调 |
| CR-N03 | 任务领取、历史任务记录或祖先来源的普通重试 | 队列、账本、任务文件与 Job 所有权 |
| CR-N04 | Node 脚本/包升级、旧运行时指纹、预约后的升级 | 冻结队列、任务尝试及原预约 |

## C8. 任务可用性与显式恢复

| ID | 可执行合同 |
| --- | --- |
| UX01 | 活动卡片最多 200 张，总保留卡片最多 1200 张。归档仅隐藏已结束或未执行的卡片，保留 ID、修订、依赖与尝试；排队、非终态 TaskRun 或非终态启动记录拒绝归档。恢复需要活动名额，不入队、不重放。 |
| UX02 | 任务说明与验收要求不作为自动提示词发送。表单与复制窗口明确告知，由用户预览并粘贴到对应终端；复制不包含启动参数。 |
| UX03 | 关闭运行中的队列时，即使此刻无创建操作，也显示等待数量、关闭后暂停和重开需继续。取消关闭保留派发；重开默认暂停，继续只读取上次持久执行范围。显式执行整个队列才清除范围。 |
| UX04 | 恢复页只读核对任务/领取/启动身份/结果归档/回执，单独回执不证明已退出。新排队意图须持 queue gate、重新加载、验证原尝试终态与任务身份，再核对当前程序及目录；保留原 Sequence、所有领取和尝试，不自动执行。续接项仍走会话核对流程。任务备份恢复仅允许主文件不可读；同一 tasks mutex 内核对预览 hash、解析同一份备份字节、保留原文件与 .bak、原子发布并推进修订，拒绝陈旧写入；不恢复控制账本，不放开未知占用。 |
| UX05 | 快启默认只执行本批次，并发 1；范围与导入在同次事务持久化，全局 MaxRunning 保留。明确选择整个队列才放入其他等待项并改全局并发为 1。续接默认只执行本任务，明确选择整个队列才扩展范围。原跨窗口领取、全局并发和资源保护继续核对。 |
| UX06 | 暂缓只接受可读取账本且尚未领取的排队意图；保留 EntryId、Sequence、冻结命令与资源。调度跳过暂缓项，后续独立任务可检查，依赖仍须完成。恢复保留顺序。Worker reserve-task 与 begin 独立拒绝暂缓项，不能用旧快照激活。 |
| UX07 | 等待列表直接按调度的优先级降序、Sequence 升序呈现，显示当前范围、暂缓与等待原因；卡片排序不充当执行次序。 |
| UX08 | 历史页列出保留的 TaskRun、控制记录、领取与可验证终态归档，可按名称、中文/原始状态、完整尝试 ID 搜索，归档卡片历史仍可访问。最多扫描 10000 个目录并明确提示截断；缺失或损坏保持未知。单条诊断与列表导出不含 argv、会话 ID 或环境变量，单条可能含工作目录；当前控制记录损坏不遮蔽可核验的历史归档，但归档不授权释放占用。 |
| UX09 | 登记/续接提供各 CLI 会话标识来源、应填值和错误示例；任务 ID 不充当会话 ID。程序版本/指纹/工作目录仍使用原核对，帮助文字不扩大已验证能力。 |

`tasks.json` schema 3 对 `Archived`、`Deferred`、`LastDispatchTaskIds` 定义新语义；旧版本须拒绝。schema 1/2 缺省的新字段只在内存迁移，未成功保存不得改写文件。达到保留上限停止新增，不能删除历史或领取来绕开容量。

队列在准备目录、预约和消费等待项之前核对 Terminal 入口。缺失时保留原等待项，提示设置已安装入口；后续提交仍可能失败，不能把入口存在当作终端已经接收。


## C9. 会话通知、变更摘要与对比组（2026-11-16）

- 会话状态通知是客户端本地行为：只在运行态迁移（首次观察仅建立基线，不回放启动前已有状态）触发，同事件去重、同类两秒限频；通知文本不得包含参数、完整路径或会话标识，只给类别、名称与安全摘要。点击通知仅激活已有窗口并滚动到对应行，不启动、不关闭任何会话。设置项 `NotifyOnSessionEvents` 缺省视为开启（缺省字段=true），关闭后完全不投递；通知不改变任务、队列或监控状态。通知只在 App 窗口存活期间投递：App 关闭后 Worker 继续看管会话，但不会再弹出通知，重新打开 App 也不补播关闭期间的事件。
- `TaskCard.Note` 为可选备注（旧 tasks.json 无此字段，反序列化为 ""）；对比组标记写入 Note 前缀且不得吞掉原备注，搜索命中 Note 与名称/项目/Agent 同一语义。复制对比组只在原任务非归档、不在队列、无未确认尝试时开放；副本清空 QuickLaunch 与依赖、重置修订，逐张走正常入队路径，不绕过准入与预约。
- 运行记录中已结束会话的「查看变更」只读展示 git diff 摘要（分支、基线、领先/落后、未提交数、diff --stat），非 git 或缺失目录给出可读错误，不得写工作目录。推送与创建 PR 不新增旁路：一律进入任务卡的审阅窗口（钉住已核对 SHA 后才推送/建 PR），无关联任务的快启会话不提供该入口。

## C10. 压力下缩减空闲会话工作集（2026-11-17）

- 只处理账本内 `Started` 的非 Git 项，持有可查询的原 item Job。连续 30 秒未观察到整棵进程树 CPU 累计量变化才视为候选；使用单调时间，不以 IPC 请求次数计时。CPU 变化、计数倒退、缺失或无效系统采样重置观察窗口。这是观测空闲，不承诺判断用户/网络活动，也不能消除观察后进程重新活跃的竞争。
- 同时满足既有压力锁、有效新采样、可用物理内存低于当前策略保留线且账本正常才尝试回收。仅 CPU 或提交压力不触发。每项尝试间隔至少 120 秒（失败也计入），全局至少 5 秒且须有比上次尝试更新的系统采样，避免一次旧低读数清空所有会话工作集。
- 对枚举得到的 PID 打开最小必要权限的进程句柄，并用同一固定句柄再次核对 Job 归属后调用 `K32EmptyWorkingSet`；退出、PID 复用导致不属于原 Job、权限拒绝均跳过。优化失败不能使 Monitor 退出或改变启动授权、释放预算。无管理员权限、全机清理、结束进程或修改硬限额操作。
- 工作集减少不表示 commit 下降，也不证明页面已写到分页文件。前后私有工作集采样都有效且减少至少 32 MiB 才报告实测差值，十分钟后淡出；提示明确再次使用时按需载入。原数据完整性和重新访问后继续执行须由原生夹具验证，代表性 Agent 长稳另行验收。

## C11. 快捷导航与项目切换（2026-11-17）

- 窄窗口也保留当前项目与快捷操作入口。最近项目切换先核对目录可访问，再使用既有草稿编辑/保存流程；不存在、权限失败或在核对期间发生新编辑时不替换当前项目。最近记录按路径忽略大小写去重，上限仍为 8，成功切换后立即更新控件。
- `Ctrl+K` 打开可搜索列表，内容只来自当前已加载配置、账本和队列；不扫描会话目录、不探测 CLI、不采集对话。支持多词 AND、中文和大小写不敏感匹配，空结果禁止打开。方向键移动选择，Enter 返回所选动作，动作在关闭对话框之后执行，Esc/取消无动作。不得在搜索文本或结果展示启动参数。
- 快捷列表提供导航和已有设置/恢复入口，不提供自动启动、自动重试、自动派发命令。项目修改动作在执行前重新核对只读/忙碌状态；已有服务仍负责原有校验。主窗口快捷键忽略长按产生的重复触发，模态窗口打开时不触发主窗口命令。
- C12 增加的新增、复制和重命名配置动作同样先关闭搜索窗口，再重新核对容量、目标项及锁定状态。
- Agent 切换仅更新选中编辑区和侧栏选中态，保留待确认项目输入，不改变勾选、配置修订或活动进程。侧栏摘要及快捷搜索按 attempt/queue ID 定位记录，展开、滚动并移动焦点；记录消失时提示并保留当前列表，不启动或恢复它。队列摘要区分暂缓、范围外、暂停与等待调度，不把所有等待都标为资源不足。

## C12. 多 Agent 配置复用与选择（2026-11-17）

- 新增或复制后自动选中新项，将键盘焦点放到 Agent 类型选择。新项默认不勾选，现有启动、目录隔离、资源检查保持生效，最多保存 16 项。
- `Slot.Copy()` 继续用于保留身份的快照；`Duplicate()` 必须分配新 ID、深复制全部 Agent profile 与参数数组，保留目录、`RequiresDirectory` 和备注，取消勾选。App 将副本插在源项后并分配可区分的副本名称；复制不准备目录、不创建任务、不启动进程。
- 所有 Agent 都可使用现有 `CustomName` 字段设置显示名称，空白回退到 Agent 类型名称；`AgentId`、对应 profile 与实际命令不随名称改变。名称用于后续新计划，已运行或排队的冻结计划不改写。重命名不探测 CLI，取消无写入、修订已变化则拒绝旧表单。
- 侧栏复选框和行导航为独立的同级控件。复选框与详情共用受保护的编辑事务，UIA TogglePattern、键盘和鼠标使用相同路径；编辑后恢复焦点，改变勾选不切换详情、不启动或结束会话。只读、忙碌、关闭中拒绝修改并还原显示；操作执行时重新检查，旧快捷列表不构成授权。

## C13. 任务交付、结果与恢复（2026-11-17）

- 任务 schema 4 增加 `DeliveryMode` 和会话程序重新核对记录；1–3 仅在内存迁移，未设置的交付方式为手动。交付选项为手动、发送后交互、非交互执行；自动方式只支持通过本机帮助检查的 Codex、Claude、Omp、Pi、Qoder，不追加跳过权限检查的参数。说明与验收要求作为单个 argv 元素，入队后冻结；修改自动交付的名称/说明/验收要求/交付模式会提升修订并取消旧排队。勾选验收不变更发送内容。续接不重发初始任务。完整 Windows 命令行超限则拒绝入队。
- 非零退出码产生失败任务，保留原诊断；0 退出仍需人工验收，未知退出码明确标注未知。主 Agent 退出而子进程尚在时，不宣布整树结束；后代退出后再通知。旧终态诊断中的明确退出码可以用于判定。
- Runner 在启动 Agent 前保存 attempt/workspace 绑定的 Git HEAD。摘要优先使用该提交，提交缺失不得静默切换比较对象；未跟踪文件单列，不因 diff --stat 为空声称没有改动。共享目录及启动前已有修改仍可能进入比较；旧记录无基线时必须说明比较边界。
- “后台运行”先成功创建托盘图标再隐藏窗口，保持现有队列范围和暂停状态；双击、再次启动或点击通知恢复。关闭仍暂停后续派发，关闭提示提供后台入口。Explorer 重启后重建图标，重建失败恢复窗口。
- 新终端标题包含完整批次 ID，窗格编号随实际创建顺序持久化。聚焦先找到唯一现存窗口，再向命名窗口请求标签页/窗格，并核对结果标题；目标丢失、被移动或前台权限拒绝均不得报告成功。旧记录没有可靠窗格身份时给出手动定位指引。
- 详情表单立即显示，版本检查异步执行；关闭可取消检查，路径改变后旧结果不可启用预设。
- 会话候选仅读取有界的本地元数据，过滤工作目录物理身份，不展示对话内容。扫描上限/损坏记录要披露；自定义会话位置可手工填写。程序升级后重新检查当前安装的续接参数，并经登记表单确认绑定当前指纹，保留原启动指纹；再次升级、目录变化、活动会话冲突仍拒绝。
- Runner 创建 Agent 之前由独立 `job-custody` 进程确认持有 root/pool/item 句柄；托管只有幂等保留操作，没有执行授权。监控/runner 崩溃后可按名称重新打开原 Job。守护只在账本终态且实际成员为空时放开 item；pool 还须无活动预约且实际为空。账本不可读保留句柄，不杀进程、不清账本。全部占用释放后守护退出。已有全部句柄丢失的旧池仍无法证明为空，不自动清空；多个保护进程同时丢失也不承诺恢复。
- 清理被文件阻止时显示 NUL 分隔 Git 清单，区分已跟踪修改、未跟踪与忽略内容，转义换行等特殊路径，支持刷新、复制和打开目录。忽略的 .env/本地数据不能默认视为可删除。实际清理仍双重核对占用、分支、索引与内容，并使用无 force 的 worktree remove。

## C14. 远程执行域、生命周期与同步（2026-11-17；2026-11-18 按 `6daa7de` 对齐实现状态；同日记三台同权）

### 1. 范围 / 触发

槽位引用已登记的 Linux SSH 主机，或启动计划携带 `RemoteExecution` 时适用。本地槽位、WSL、远程 Windows 和远程桌面不在本合同。本合同定义执行域分域、校验、本地 SSH 生命周期、工作区同步和操作员释放；远程主机内部的 CPU/内存管理不在本合同保证范围。8 个窗格名额包含远程条目，含尚未操作员释放的 `Disconnected`。

2026-11-18 产品决定「三台同权」写入本节。对等机器是操作者的本机 Windows（本机）、京东云 VPS（文档称京云）、阿里云 VPS（文档称阿里云）。文档不写真实主机名、地址、用户名或密钥。同权是工作区对等：任一台上的 agent 都可以修改共享的项目工作区，另外两台必须看到同一批文件。京云与阿里云上的 agent 仍在各自的远端机器上运行。同权的实现方式是必须的双向工作区同步。远端进程没有本机文件系统的直接访问权。同步模式 `None` 不是同权。操作员确认的断线重新挂接已实现（R33），用来在 SSH 断开后恢复同权所需的本地挂接，并在原来启用了双向同步时重新建立它；自动重连不在本合同。8 窗格名额和本地内存/CPU 分域不因同权改变；远端主机的内存与 CPU 限额并不因此与本机相等。

### 2. 接口签名

- 配置：`Configuration.RemoteHosts` 保存 `RemoteHost`（稳定 `Id`、名称、地址、端口、SSH 用户、Agent 用户、可用 Agent 类型、可选 `SshKeyPath`、默认同步模式）。
- 槽位：可选 `RemoteHostId`、`RemoteDirectory`；二者在启用远程时必须能解析为合法主机与合法远程目录。
- 计划：`LaunchItem.RemoteExecution` 为 `RemoteExecutionSpec`。准备/预约前调用 `Validate()`。
- 运行：Worker 使用 `SshRunnerBackend.Create` 只构造 `ProcessStartInfo`，`Start` / `StartWithSyncAsync` 才启动本地 `ssh`。
- 核对：监视器用本地 SSH 的 PID 与创建时间判断存活；退出码 255 视为连接断开。
- 释放（R28，已实现）：仅当条目为远程且状态为 `Disconnected` 或 `Reattaching`（本地 SSH 已结束）时接受操作员记录。判断为 `Gone`、原因非空且不超过 2048 字、并带二次确认时，写入 `attempts/<id>/operator-release.json`，把条目标为 `Settled` 并释放账本。判断为仍在（`Running`）或无法判断（`Unknown`）、缺原因、未确认、本地 SSH 仍存活、或尝试锁未释放时拒绝，账本不变。本操作不重连、不重新挂接，也不结束远程主机上的进程。确认释放不代替同权所要求的操作员确认重新挂接。系统重启后的既有 boot 证明仍走原核对分支。
- 重新挂接（R33，已实现）：协议 `reattach-remote` 与 `reattach-begin`，证据 `attempts/<id>/operator-reattach.json`。仅当操作员 `Confirmed` 为真，且条目为远程、状态为 `Disconnected` 或 `Reattaching`、回执已是 `Started` 或 `Disconnected`、本地 SSH 已不在时，监视器才用另一次免密 SSH 核对 `AGENT_LAUNCHER_ATTEMPT` 标记的远程进程。进程仍在则同一条账本改为 `Reattaching`，由 `pane <id> reattach` 恢复本地 SSH；原来的同步模式是 `Mutagen` 或 `SshfsMount` 时重新建立同步并更新 `sync-session.json`，不写表示已经收敛的 `sync-final.json`。模式为 `None` 或未配置同步时不建立同步，也不写成已同步；选择 `None` 的这次启动不是同权。进程已不在、回执尚未进入已启动、未确认、探针超时或结果含糊时拒绝，账本不变，不自动释放。本地挂接结束后回到 `Disconnected`，不转为 `Settled`。本操作不释放预算，确认释放也不会调用它。没有自动重连。

### 3. 数据与状态合同

- 名称和地址在主机列表中各自唯一（忽略大小写）。槽位只通过 `Id` 引用主机；删除主机前必须核对应槽位引用，存在引用则拒绝删除。
- 认证只使用 `ssh-agent` 或私钥路径，启动参数包含 `BatchMode=yes`。配置、账本和普通日志不得保存密码或私钥内容。
- 远程目录必须是绝对路径或以 `~/` 开头。远程参数不得包含 Windows 盘符或 UNC 路径，否则准备/预约拒绝，提示「远程参数包含 Windows 本地路径」。
- 当前允许的远程 Agent 类型为 `pi`、`kimi`、`qodercli`、`omp`、`codex`。`custom` 和未知类型拒绝。远程内存上限已实现（2026-11-19 [规格](superpowers/specs/2026-11-19-remote-memory-limit-spec.md)）：主机级 `MemoryLimitMib`（256–65536 MiB，留空不限制）在准备层换算为 `RemoteExecutionSpec.MemoryLimitBytes`，启动命令以 `systemd-run --scope -p MemoryMax=<bytes> -p MemorySwapMax=0 -p OOMPolicy=continue --uid=<agent> --gid=<agent> -- sh -c <inner>` 包裹内层命令并先守卫 systemd-run 可用性，缺失即以可读错误拒绝启动（fail-closed）；scope 内不用 su（pam_systemd 会迁出 scope），MemorySwapMax=0 使超限触发 OOM、OOMPolicy=continue 使 agent 留存；出界值在 `Validate()` 拒收。远程启动命令另注入固定 `NODE_OPTIONS=--max-old-space-size=1024`，只约束 Node 老生代堆；非 Node 进程忽略该变量。该注入仍是护栏，不是内存上限合同本身。重新挂接不新建限制，存活进程保持启动时的限制。真实 VPS 上 scope 生效、超限 OOM 终止与非 root 拒绝路径已于 2026-11-19 活测通过（IMPLEMENTATION §94，证据归档忽略目录，不进 CI；R45 的命令形状与边界校验随原生套件运行）。
- 远程条目不创建本地 Job 或 custody，不按本地内存/CPU 压力拦截。同一监视器上的本地计划仍受原准入约束。账本、授权代次和程序身份等共享安全闸门对两域同样生效。本地 `Budget=0` 的拒绝不得被远程豁免扩散。
- 8 窗格并发上限包含每一个非终态 agent，不论本机还是远程。Worker 在 reserve 与 begin、界面 `ActiveAgentCount`（`UpdateLaunch` 用它决定能否启动）按 `MaxConcurrentAgents = 8` 计数。把工作放到 VPS 不腾出本地窗格名额。远程 `Disconnected` 在操作员确认释放之前保持非终态，继续占一个名额；确认释放并进入终态后才让出名额。内存/CPU 准入不把远程批次算进本地压力，这与 8 窗格名额是两件事。
- 状态映射：SSH 存活且 PID/创建时间可核对 → `Started`；SSH 退出码不是 255 → `Settled`，可释放；退出码 255 → `Disconnected`，预算不释放；Started 之前的创建中断 → `Disconnected`，预算不释放。`Disconnected` 与重新挂接进行中的 `Reattaching` 都不是终态，继续占一个窗格名额。为维持同权，SSH 断开后的操作员确认重新挂接已实现（R33，见上条）。自动重连不在本合同，也未实现。`Disconnected` 在操作员确认释放或系统重启后的既有 boot 证明成立前，保持非终止。
- 结束本地 SSH 客户端，不能单独把条目改为 `Settled`。
- 同权模式要求双向工作区同步。推荐机制是 `Mutagen`。`SshfsMount` 是已经允许的 SSHFS 挂载备选。本合同不新增其他同步后端，也不把 SSHFS 新写成三台之间的同步机制。`None` 下各机只看见自己的目录，表现得像互相断开，因此不是同权。自 2026-11-19 起「拒绝 `None`」已实现：主机编辑器保存时拒收 `None`，启动准备层对同步模式为 `None` 的主机拒收并给出改法说明；既有 `None` 主机在下次启动时被拒。重新挂接对既有 `None` 条目保持只读容忍（不恢复同步，也不视为已同步）。
- 若启用工作区同步，同步远程路径必须等于启动目录。`Mutagen` 结束时先 flush，失败或超时必须保留会话并写入回执，不得直接 terminate。删除远程多余文件必须显式 opt-in。

### 4. 校验与错误矩阵

| 条件 | 结果 |
| --- | --- |
| 主机、端口、用户名非法，或远程目录/参数非法 | 拒绝保存或拒绝准备，配置不变 |
| 名称或地址与其他主机冲突 | 拒绝保存，说明冲突对象 |
| 删除仍被槽位引用的主机 | 拒绝删除，列出引用槽位 |
| 远程参数含 Windows 本地路径 | 准备/预约拒绝，提示固定文案 |
| 远程 Agent 类型不受支持 | 校验失败，不产生授权 |
| 远程内存上限出界（<256 MiB 或 >64 GiB）或非整数 MiB | 拒绝保存或拒绝准备，说明边界 |
| 同步模式为 `None` 的主机请求启动 | 准备拒绝，说明三台同权改法 |
| 本地压力锁定时提交纯远程批次 | 远程 reserve/begin 不因本地内存/CPU 压力拒绝；本地计划仍拒绝。8 窗格名额仍然包含该远程条目 |
| 非终态 agent（含远程与未释放的 `Disconnected`）加上本次请求超过 8 | 拒绝，并提示「同时运行的 Agent 窗格最多 8 个；请先结束或撤销部分 Agent 后再启动。」把工作放到 VPS 不腾出名额 |
| SSH 仍在 | 保持 `Started` |
| SSH 退出码 255 或 Started 前中断 | `Disconnected`，不释放，给出核对说明 |
| 操作员无法判断远程进程是否仍在却请求释放 | 拒绝释放，账本不变 |
| 未确认、远程进程已不在、或回执尚未进入已启动时请求重新挂接 | 拒绝，账本不变，不自动释放 |
| 重新挂接的本地 SSH 结束 | 回到 `Disconnected`，不转为 `Settled`，不写 `operator-release.json` |
| 同步 flush 失败 | 保留同步会话，回执记录错误，不得假装已收敛 |
| 同步模式 `None`（编辑器保存或启动准备） | 拒收，说明三台同权与改法（mutagen/sshfs）；既有 `None` 主机在下次启动被拒 |
| 远程主机无 `systemd-run` 而计划带内存上限 | 该次启动拒绝（退出码 97），agent 未启动，错误行进入窗格输出 |

### 5. 正常 / 基准 / 异常例

- 正常：登记主机、槽位指向该主机，reserve/begin 后本地 SSH 存活，账本为 `Started`，无本地 Job。
- 基准：fake-ssh 正常退出转为 `Settled` 并释放；255 转为 `Disconnected` 且预算保留。
- 异常：创建中断、密钥不可用、远程目录不存在、同步未就绪超时、操作员在无法判断时请求释放。均不得产生重复远程进程或静默释放。
- 已实现（2026-11-19，隔离同步分支，IMPLEMENTATION §93）：按 [同步可靠性设计](superpowers/specs/2026-11-18-sync-reliability-design.md) 提供每链路七态、30 秒观测有效期、安全相对路径冲突清单（最多 50 行及剩余计数）、two-way-safe 新建会话和一次只读 `sync list --long`（10 秒超时、512 KiB 上限、15 秒自动节流、单飞）。缺失连接信息、解析失败、过期、截断均不能声称当前已收敛；端点匹配包含本地目录、主机、SSH 用户/端口与远程目录，旧端点缓存和回执不能证明新端点健康。详情按同一工作区的独立端点聚合：先评估全部会话，再按七态优先级合并；重复配置 ID 不算多台机器，也不能隐藏同端点其他会话的故障。冲突按端点/会话去重，不把终态 `synced` 当成当前三台已收敛。普通离线、暂停和探针失败不 terminate/create；flush 失败或超时保留会话并写回执。没有 pause/resume、冲突处理或 reset 入口，既有 two-way-resolved 会话不批量迁移。R41 的 None 按更晚的拒收决定执行。R34～R43 的 fake 验收及无跳过完整门禁已通过，证据见 IMPLEMENTATION §93；R44 仍未授权或执行。不以 fake 或既有单对活测替代三机活测，不改操作员重新挂接合同。

### 6. 必要测试

- 已有记录（次数与日志以 IMPLEMENTATION 原文为准，本合同不重跑、不改写计数）：`RemoteHostEditorChecks`、`RemoteExecutionChecks`（含 R27）、`RemoteExecutionIntegration`（R23～R25 与 R28 的 fake-ssh 回归）、分域 R26、工作区同步集成，以及 opt-in 的 `RemoteLiveChecks`（不进 CI）。R28 不打开真实 VPS 的回归记在 §83：未确认不释放、无法判断不释放、确认后写回执并终态化。R29 的双 VPS 活测与发货预览包复验记在 §83–§84，证据只引用路径，不作为 CI 门禁，也不是浸泡。R30 探针挂死与 R31 桌面启动 ssh-agent 1058 的回归记在 §84。
- §84 记录的最近一次完整门禁是 `scripts/test.ps1` 退出 0；该节单独写出的隔离 `--ui-check` 为 397/397。含远程 UI 的后续改动仍须跑完整 `scripts/test.ps1`，不得只用 `-SkipUi -SkipBrowser` 关闭远程合同。
- 仍未实现：SSH 断开后的自动重连。内存上限的 systemd scope 硬顶在真实 VPS 上的生效/超限 OOM/非 root 拒绝路径已于 2026-11-19 活测通过（IMPLEMENTATION §94，不进 CI）。「拒绝同步模式 `None`」已在 2026-11-19 实现（编辑器保存与启动准备拒收）。操作员确认的重新挂接（R33）已实现，fake-ssh 回归不打开真实 VPS；2026-11-19 另在真实 VPS 活测通过（§94），仍不宣称 G4。8 窗格名额包含远程、未释放的 `Disconnected` 和 `Reattaching`，上限仍是 8。同权不把远端主机纳入本地内存/CPU 准入。本节不新增测试次数，不把 §83–§84 的活测改写成三台同权已活测。

- 同步可靠性（2026-11-19）：`SyncReliabilityChecks` 覆盖状态、脱敏、端点匹配与真实本地进程捕获边界；`--sync-reliability-integration` 用三个本地目录模拟 R35–R38/R42，不打开真实 VPS；UiChecks 验证导航、观测有效期、端点变更、详情与冲突清单。`--sync-review-checks` 已接入完整门禁，实际次数与结论见 IMPLEMENTATION §93，不改写历史测试记录。

### 7. 错误与正确做法

错误是「SSH 已经断开，所以远程进程一定结束，可以释放或立刻重试」；或「远程批次不受本地内存限制，所以 8 窗格名额和共享安全闸门也可以跳过」；或「云上 agent 不经同步就能直接改本机任意文件，所以三台已经同权」。正确是只把本地 SSH 的存活和退出码当作能直接证明的事实，把远程进程是否仍在交给操作员核对；共享账本和授权规则对远程条目仍然生效。远程批次跳过本地内存/CPU 压力，同时仍占 8 个窗格名额之一，未释放的 `Disconnected` 也占着。同权是双向同步生效后的同一项目工作区：远端进程仍在远端运行，`None` 不是同权。操作员确认的重新挂接已实现（R33），自动重连不在本合同。本合同不承诺远程进程在本地 SSH 结束后一定退出，也不承诺跨网络的 exactly-once。
