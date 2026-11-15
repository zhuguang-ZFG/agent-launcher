# 多 Agent 并发退出：GitHub 上游调查

## 2026-11-14 补充：2 GiB 触顶后退出的替代方案

后续进展：已实现 Qoder 独立插件及 Worker Hook，并在本机已登录的 Qoder 1.1.89 验证真实工具拒绝、恢复、用户 Hook 共存、同一进程继续和子 Agent 拦截。下文的“尚未接入”描述保留为调查时点记录；实现和逐项实测见 `IMPLEMENTATION.md` §56–57、`CONTRACTS.md` 的 Qoder 工具前置保护。插件格式校验、真实调度验收和持续负载验收分别记录，不能互相替代。

结论：**把固定的小额硬限额当成日常调度工具不合适。应在触顶之前限制新增工作，给已开始的工具留出增长余量，硬限额只作最后边界。** 从 2 GiB 改为 4 GiB 是扩容缓解；不等于消除泄漏、不等于可恢复 OOM，也不应作为“长任务稳定”的承诺。

### 本次重新核对的公开依据

| 来源 | 已核对内容 | 对启动器的含义 |
| --- | --- | --- |
| [微软：硬限额](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)及[通知限额](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_notification_limit_information) | Job 的 committed memory 硬限额与只发通知、允许继续提交的通知限额是不同机制。通知到达后需查询 violation 信息才能再次接收通知。 | 收到硬限额拒绝分配事件后才扩容，可能已经赶不上 allocator abort；软通知适合提示和调度，不能单独保证不会 OOM，也不是自动内存回收或节流功能。 |
| [Omp #8324](https://github.com/can1357/oh-my-pi/issues/8324)及[诊断讨论](https://github.com/can1357/oh-my-pi/issues/8324#issuecomment-5263988592) | 本次 API 查询仍为 open；社区讨论区分 allocator fatal OOM 与可恢复工具错误。后续报告出现 error 1455、SQLite/Python/Bun OOM，报告者当时使用固定 4 GiB 分页文件。 | 调度必须同时看整机提交余量和物理余量；不能把该社区机器的分页文件问题套用到本机已确认的 Job 触限事故。扩大分页文件也不会解除 2 GiB Job 上限。 |
| [Omp #16078](https://github.com/can1357/oh-my-pi/pull/16078) | 2026-10-17 合并；对 tree-sitter WASM store 设置处理字节预算并回收；特定复现 RSS 从 1606 MiB 降为 142–242 MiB。 | 对象/缓存生命周期与有界处理才是减少持续增长的办法。上一轮已核对本机 Omp 18.20.0 含该修复，不能再把“升级即可”当作本次新解决方案。 |
| [OpenCode #63030](https://github.com/anomalyco/opencode/pull/63030) | 2026-11-11 合并；分块读文件且限制长行解码，特定 108 MiB 文件复现峰值 1061 MiB → 113 MiB。 | 参考工具执行层减少峰值的办法；这不是 Omp/Qoder 已采用同一实现的证据。 |
| [Qoder 官方 Hook 文档](https://docs.qoder.com/cli/hooks) | `PreToolUse` 可以拒绝工具；可用 `hookSpecificOutput.hookEventName=PreToolUse` 和 `permissionDecision=deny`。command Hook 支持 `args` 数组直接执行；事件 Hook 默认同步，异步 Hook 不适合作为执行前闸门。文档说明三种配置文件的 Hook 合并。 | Qoder 有可用的接入方向。尚需对本机版验证 CLI overlay 与原 Hook 合并、子 Agent 覆盖、超时/错误行为以及真实工具未执行；不能仅凭文档宣称已经保护。 |

访问说明：本次 `web.run` 未提供可读正文，Context7 返回月度配额不足；改用 GitHub 官方 API 与官方文档 HTTP 下载核对。官方页面快照在 `artifacts/cleanup-review-20261114/` 的 `qoder-hooks.html/.txt`、`job-notification.html/.txt`、`job-hard-limit.html/.txt`。`qodercli --version` 本机返回 **1.1.89**，`--help` 确认 `--settings`、`--config-dir`、`--plugin-dir`、`--resume`。Qoder 社区仓库按 memory/crash 搜索本次未返回匹配项；这不代表不存在该问题，也不足以断言其内部 OOM 处理方式。

### 建议的运行方式与当前交付范围

1. **在新工具启动前留余量**。Omp 现有扩展覆盖主/子会话，限制 task 并发最多 2；当任务/池接近硬限额时拒绝新增工具，保留等待已有工具的通路。本轮补齐整机判断：物理、提交可用量均须至少为系统保留线加 `max(512 MiB, 单项硬上限 / 4)`，而不是刚刚高于保留线就放行。2 GiB 配置提前预留 512 MiB，4 GiB 配置预留 1 GiB。这是启发式提前量，多个同时启动的工具没有事务式内存预约，单个工具仍可能突破余量。
2. **将日常预算与紧急硬边界分开**。保留 CPU、Job、共享池与整机保护；给超过 2 GiB 的正常负载配置合理的峰值空间，减少同时活跃的 Agent 和内部子任务。未来若加入“弹性扩容”，必须在启动前记录允许的绝对上限，由 monitor 串行核算所有活跃任务与系统余量，在软阈值阶段决策；不得在已拒绝分配后假装能够救回进程，也不能静默改变冻结队列或现有 Job。此弹性方案本轮**未实现**。
3. **Qoder 接入同步 PreToolUse**。建议复用实时 Job/系统采样与拒绝理由，使用结构化 `command`/`args`，仅注入本次进程配置；不能覆盖用户既有 Hook，不能传出工具参数/对话。低内存返回 deny 而非杀进程；恢复后下一次调用重新判断。等待/取消类能力须按 Qoder 实际工具协议单独验证，不能照抄 Omp 的工具名。目前生产代码**尚无 Qoder 工具拦截**。
4. **退出后保留可核对的现场**。保留工作目录、退出回执与终端；用户核对实际落盘结果和 Agent 会话记录后续接。不能保证崩溃前尚未持久化的对话保留，不能自动重放可能已经写入文件的任务。Qoder 的 `--resume` 仅证明提供恢复入口，不证明能恢复任意 OOM 时的未落盘状态。

验收必须分开：纯策略/命名管道/受控分配夹具可证明保护条件；真实 Agent 带重工具持续运行、超过原 2 GiB、恢复及子任务覆盖才可证明实际生产力改善。本轮结果见 `IMPLEMENTATION.md` §55；不以扩大限额或通过单元测试替代真实长稳验收。

## 2026-11-13 原始调查

核对日期：2026-11-13（Asia/Shanghai）。调查范围为 Omp 和 OpenCode 的 issue、已合并 PR、关闭 issue 的实际提交，以及 Omp `v18.20.0` 的对应源码。浮动 `main` 的辅助核对固定到 `84f5adc3bbfc0c2a7caecbad78b1b6eedf0a2939`；涉及本机版本的结论另查 `v18.20.0`，不以主分支代替已发布版本。

## 1. 别人实际采取的处理

| 来源及状态 | 具体处理 | 能解决的范围 |
| --- | --- | --- |
| [Omp #8004](https://github.com/can1357/oh-my-pi/pull/8004)，2026-08-08 合并 | 子会话 dispose 时释放消息、SSE 调试缓冲和会话日志的内存副本；需要恢复时从磁盘重建。 | 修复已结束子会话的对象图被长期引用造成的增长。不能保证仍在工作的多个会话不超额。 |
| [Omp #16078](https://github.com/can1357/oh-my-pi/pull/16078)，2026-10-17 合并 | 每线程 tree-sitter WASM store 累计解析 4 MiB 源码后重建，保留已编译语法模块。 | PR 的 60 次解析复现从 1606 MiB 降到 142–242 MiB；这是特定原生增长路径的修复。[原 issue #16067](https://github.com/can1357/oh-my-pi/issues/16067) 的 40 GiB 错误分配不能直接作为本次 1 KiB 分配失败的归因。 |
| [Omp a608a9c](https://github.com/can1357/oh-my-pi/commit/a608a9ca07c9443634e80d877d61c602099dab91)，2026-10-12 已合入，并关闭 [#9908](https://github.com/can1357/oh-my-pi/issues/9908) | 同一模型和缓存目录复用一个机器级嵌入服务；服务持有独立推理子进程，推理卡死时回收子进程，空闲 15 分钟退出。 | 减少每会话一份嵌入模型的重复开销；没有因此实现所有会话、工具的统一内存准入。早先 [#13785](https://github.com/can1357/oh-my-pi/pull/13785) 仍开放，**不能只看这条 PR 就判断共享模型尚未实现**。 |
| [OpenCode #58820](https://github.com/anomalyco/opencode/pull/58820)，2026-10-26 合并 | 图像缩放放入每进程共用的 worker，以一个许可排队，空闲 10 秒终止 worker；WASM panic 等请求错误使该请求失败并重建 worker。 | PR 测量空闲后 RSS 从约 326 MB 降到 63 MB。此处是线程 worker 和 WASM 请求错误边界，不是任意 Rust 全局 OOM 的进程隔离保证。 |
| [OpenCode #59205](https://github.com/anomalyco/opencode/pull/59205)，2026-10-26 合并 | 搜索匹配行一收到就截取并复制所需片段，释放原始大字符串，避免先收集全部全文再截断。 | PR 的定向堆测量从约 150 MB 降到 3 MB；不是完整项目的内存基准。 |
| [OpenCode #63030](https://github.com/anomalyco/opencode/pull/63030)，2026-11-11 合并 | 按 256 KiB 块计数与读取所需页面，限制超长行的解码量，避免整文件驻留。 | PR 的 108 MiB 单行文件案例峰值从 1061 MiB 降到 113 MiB，返回内容保持一致。 |

共同做法是限制真正消耗资源的工作、给缓存设预算、及时释放可重建状态，以及在明确的故障边界内返回请求错误。以上案例均不能推出“任意 Agent 在内存不足时仍永不退出”。

## 2. 上游没有承诺可恢复的部分

[Omp #8324](https://github.com/can1357/oh-my-pi/issues/8324) 报告 Windows 并发子任务及工具导致小额原生分配失败。协作者解释，`pi-natives alloc crash` 来自 Rust 全局 allocator 的错误钩子，任意全局 OOM 不是可通过普通异常捕获恢复的工具错误。后续报告包含 Windows error 1455、SQLite OOM、Python OOM 和 Bun OOM，指向整机提交额度耗尽。核对时 issue 仍为 open，带 wontfix 标签；这不表示内存准入增强已经实现，也不证明本机这次一定是系统提交耗尽。

`crates/pi-natives/src/crash_handler.rs` 的当前实现区分可恢复 panic 与 fatal allocation failure：前者在已有 recovery scope 内可以变成 Promise 拒绝，后者的 `set_alloc_error_hook` 保存诊断后调用 `process::abort()`。不能把“panic 可以恢复”误写为“allocator OOM 可以恢复”，也不能把增加诊断的 [#2212](https://github.com/can1357/oh-my-pi/pull/2212) 当作不退出修复。

`task.maxConcurrency` 的上游默认为 32，`task/parallel.ts` 的 Semaphore 按数量准入并支持 resize；这里没有根据 Windows 物理/提交余量自动作出内存准入决定。降低数量上限可抑制并发增长，但不能单独构成整机内存保证。

## 3. 与本机事故对照

- 本机 `omp --version` 为 `18.20.0`。只读执行 `omp config get task.maxConcurrency` 为 **4**，`mnemopi.noEmbeddings` 为 false。这是项目工作目录下本次查询的有效值；特定启动参数的 profile、cwd 或额外 overlay 仍可能产生不同设置，不能反推事故发生时的全部配置。
- 已核对 `v18.20.0` 的 `session/agent-session.ts` 含 `rawSseDebugBuffer.clear()`、`releaseRetainedEntries()`；`pi-ast/.../grammar.rs` 含 4 MiB store 预算；`mnemopi/embed-client.ts`、`embed-server.ts`、`embed-protocol.ts` 含共享服务与 15 分钟 idle 退出。源码已包含这些修复，不证明事故时每条功能路径都实际启用。
- 用户提供的 Job 证据是单项工具树提交峰值 **2053 MiB / 上限 2048 MiB**，并出现内核拒绝分配通知。启动器 `ResourcePolicy.cs` 的默认准入规划是 **768 MiB**，默认 Job 硬限额是 **2 GiB**，物理和系统提交准入目前都使用规划值；软预算确认允许承担更高的硬限额总和。这种策略允许启动后的任务明显超过准入估算。
- 重新读取 PID 12088 的 native-alloc 记录，失败请求为 **1024 bytes、alignment 128**，栈全为 unknown。Job 拒绝分配已经有证据，具体原生调用点和系统提交压力是否同时参与仍没有足够证据；不能由失败申请很小而断言小额 allocator 本身损坏。
- 共享嵌入服务从首次使用它的进程启动。它与独立窗格 Job 的资源归属需要专门验证：`detached: true` 不能直接当作脱离 Windows Job 的证明。这是待测兼容风险，**尚未证明是本次 PID 30024 或 8652 的身份或死因**。

## 4. 本项目后续修复的约束与验收

1. 在分配被拒绝前管理资源。按真实代表性工作负载校准每项规划与上限，区分物理余量和提交额度；多窗格的总预算必须覆盖尚可能增长的活跃任务、未启动预约及共享辅助服务。不能只把 2 GiB 改成另一个未经验证的数。
2. 同时处理窗格外层并发与 Omp 内部子任务/工具并发；本机现值已是 4，不能把“从默认 32 降下来”冒充新修复。若注入设置，采用 Omp 支持的进程级 `--config` overlay，并验证版本、优先级和实际生效值，避免覆盖用户全局配置。
3. 资源不足时阻止或延后新增工作，让已经运行的工作自然排干；仅暂停启动器队列不能限制一个已启动 Omp 内部再开工具。任何内部反馈方案都要说明支持范围、取消和有界等待，不能用反复挂起、杀进程或自动重放写操作来假装稳定。
4. 针对共享服务建立真实 Windows Job 成员和提交量对照；禁止为绕过额度直接启用 breakaway，禁止杀掉其他窗格仍使用的共享服务。S5 同进程多会话宿主仍有独立隔离失败，不能以共享模型替代其验收。
5. 回归至少包含：多个外层 Agent 与内部并发的组合、超过旧 2 GiB 的代表性工具树、低提交余量阻止新增工作、一个工具失败时其他窗格继续、共享服务的首次/后续归属与退出、重复长时运行的内存趋势。受控原生拒绝分配夹具和诊断回归继续保留；不以它们替代真实并发不崩溃的验收。

调查阶段未修改生产行为。后续实现、实际验证与边界记录在 `IMPLEMENTATION.md` §49 和 `CONTRACTS.md` C2；不能把上游 PR 或原生分配夹具当作真实任务长期不崩溃的证明。

## 5. Bug Analysis: 并发工作增长超过启动时估计

### 1. Root Cause Category

- **B / E：跨层合同与隐含假设**。启动器只约束窗格数量及硬限额，Omp 内部仍能产生工具和子会话；规划估计不是后续提交上限。Job 拒绝分配已经证实，原生调用点及系统提交耗尽是否同时参与尚未证实。

### 2. Why Fixes Failed

1. 保留窗格、退出码解释和内核通知改善诊断，不能阻止下一次分配失败。
2. 单独增大默认额度没有覆盖整机增长预留；直接改旧队列额度还会破坏冻结计划。
3. 只降低一个共享全局异步名额可能令等待子任务的父任务占满调度器；执行前 Hook 等待同批工具结束也可能卡住尚未开始执行的批次。

### 3. Prevention Mechanisms

| 优先级 | 机制 | 具体处理 |
| --- | --- | --- |
| P0 | 运行时准入 | 按规划量（requests）预留提交增长，硬上限仅内核兜底；私有驻留量只抵扣物理规划；未知不抵扣 |
| P0 | 工具前置背压 | 当前用户管道查询实测余量；主/子会话 Hook 有界拒绝新工作，保留 wait |
| P1 | 内部调度 | 使用 Omp 自有 task 调度器的进程内 override，最多 2；不改共享 async manager 或全局配置 |
| P1 | 回归 | 核心/内核/真实主会话/真实子会话分别证明计数、拒绝、恢复和隔离 |
| P1 | 发布 | 保护扩展作为 App/Worker 同包必需文件，计划摘要包含其路径 |

### 4. Systematic Expansion

同类风险还存在于单个大型工具、后台嵌入服务、解析缓存及其他 CLI 内部。背压不是对任意第三方分配的事务式预留，已启动工作仍可能超过剩余空间。仍需代表性重负载与长期观测，不以静默终止或自动重放写操作处理。

### 5. Knowledge Capture

已更新项目现有 `docs/CONTRACTS.md` C2 的计算、跨层接口、测试与故障边界，验证记录在 `docs/IMPLEMENTATION.md`。本项目没有 `.trellis/spec/` 或对应生成模板目录，因此使用已有合同文档记录，没有另建一套重复规范。
