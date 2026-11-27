# 多 Agent 启动器规格

版本：v0.6.1 · 2026-11-18 · 已实现技术预览，完整发布门禁尚未通过

本轮修订：按 `main` `6daa7de` 与 IMPLEMENTATION §83–§84，把已经落地、已经有活测记录、以及仍然不承诺的边界写清楚。不宣称 G4。SSH 远程 Linux 主机仍在正式范围（见 3.4 与[远程执行规格](superpowers/specs/2026-11-17-remote-execution-spec.md)）；WSL 仍暂不实现。产品目标、本地 Job 保护与发布门槛不变。

2026-11-18 产品决定「三台同权」（见 3.4，记录于 IMPLEMENTATION §88）：本机、京云、阿里云对共享项目工作区同权。同权要求双向工作区同步，推荐 Mutagen；同步模式 `None` 不是同权。自 2026-11-19 起，主机编辑器保存与启动准备两层拒收 `None`（既有 `None` 主机在下次启动时被拒）；本文按该实现改写。SSH 断开后的操作员确认重新挂接已实现（R33，IMPLEMENTATION §89，fake-ssh，不打开真实 VPS）。远程内存上限已实现为 systemd scope（[规格](superpowers/specs/2026-11-19-remote-memory-limit-spec.md)，R45；真实 VPS 活测待办）；自动重连仍未实现。运行窗格上限为 6，不把远端主机纳入本地内存/CPU 准入，不新增活测，不宣称 G4 或浸泡。

## 1. 阅读入口与状态

| 文档 | 权威范围 |
| --- | --- |
| [SPEC.md](SPEC.md) | 产品目标、范围、技术选择、开发阶段 |
| [UI-DESIGN.md](UI-DESIGN.md) | 页面、控件、文案、设计语言、交互与可访问性 |
| [CONTRACTS.md](CONTRACTS.md) | 启动、并发、资源预算、Git、持久化和异常合同 |
| [ACCEPTANCE.md](ACCEPTANCE.md) | 功能、视觉、性能、故障注入、代码 review 与交付证据 |
| [REVIEW.md](REVIEW.md) | v0.3 问题、风险、规格修复位置及未验证项 |
| [REVIEW-v0.4-followup.md](REVIEW-v0.4-followup.md) | 第二轮 7 项缺陷及 v0.5 防护映射，区分规格与实现状态 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 工程依赖、进程边界、UI 信息来源 |
| [IMPLEMENTATION.md](IMPLEMENTATION.md) | 技术预览运行方法、实现差异与实测证据 |
| [vibe coding 扩展规格](superpowers/specs/2026-11-11-vibe-workbench-spec.md) | 参数预设、任务队列、共享资源池、审查与 Omp 多会话规划；其中部分已在后续波次落地，剩余与差异见 IMPLEMENTATION，不再把整份扩展规格标为未实现 |
| [扩展实施计划](superpowers/plans/2026-11-11-vibe-workbench-plan.md) | 稳定性前置门槛、分阶段改动、验收与回退 |
| [远程执行规格](superpowers/specs/2026-11-17-remote-execution-spec.md) | SSH 远程主机、执行域分域、生命周期、工作区同步与安全边界；三台同权见该规格第 6 节与本文 3.4 |
| [缺口收口计划](superpowers/plans/2026-11-17-gap-close-plan.md) | 对照 `6daa7de` 已收口的远程项与仍开放的发布门禁；三台同权见该计划 2b |
| [同步可靠性设计](superpowers/specs/2026-11-18-sync-reliability-design.md) | 冲突、离线与传播延迟的目标行为。待实现。R34～R44 均为未执行。不改写重新挂接段落 |
| [prototype.html](prototype.html) | v0.6 离线交互稿，3 项状态问题已修复并通过浏览器回归 |

现已有 C# / WPF 原生源码、可运行技术预览、真实 Windows Job/Git/Terminal 测试和 WPF 截图。发布包入口和证据见 IMPLEMENTATION；不能将局部断言等同于 51 项完整验收通过。性能阈值仍是门槛，未测项目和失败数据必须保留。

本版本的强制防护：创建时关联受控 Job；可撤销且带代次的启动授权；Git 准备计入资源总量；物理 worktree 跨批次占用；Agent 独立 profile；完整组合快照与事务式应用；独立的最近编辑快照；同时在跑的 Agent 窗格固定上限 8（本机与远程的非终态 Agent 都占名额，见 3.4）；资源读数不足在实用/自定义模式只提示、保守模式仍硬拒。具体责任在 CONTRACTS C1～C5 及后续增补合同（含远程执行 C14），追踪表在 ACCEPTANCE §8 及后续增补项。本地功能矩阵的历史 51 项要求仍然有效；远程执行另有 R23～R29。已记录的实现与活测见 IMPLEMENTATION §83–§84；三台同权的产品决定见 3.4 与 IMPLEMENTATION §88；仍开放的发布条件见[缺口收口计划](superpowers/plans/2026-11-17-gap-close-plan.md)。不能因文档对齐或已记录的子集通过就关闭全部实现问题，也不能据此宣称 G4 稳定发布通过。

## 2. 用户目标与优先级

用户要拖入文件夹、自由选择 Omp、Codex 或 Claude Code，在一个终端窗口中分屏运行；多个 Agent 能处理同一个项目。操作简单，设计稿详细，并考虑代码质量、视觉、功能和崩溃恢复。

**电脑保持可操作是第一性能目标和发布门槛。** 不以更多窗格、更高并发或更低单任务耗时换取桌面持续卡顿。数据完整性同时是不可牺牲的约束：不能靠无提示杀进程、丢工作区或吞异常达标。

明确区分：

- 保存 16 个 Agent 配置，不等于启动 16 个进程。
- 多线程用于避免 UI 阻塞；多个 Agent 是多个进程，仍消耗各自的资源。
- 同项目 worktree 提供文件隔离，不提供自动分工、实时共享修改或无冲突合并。
- 找到 CLI、提交终端命令、启动进程、Agent 完成初始化、完成任务是不同状态。
- 模型走云端时通常不加载本地模型权重，工具服务仍可能成为内存、CPU、磁盘的大头。

## 3. 产品形态与范围

### 3.1 推荐形态

默认采用 **Windows 原生配置窗口 + 应用内终端标签**，也可在设置选择外置 Windows Terminal。拖项目、选 Agent、查看预算后，每项在对应标签或窗格运行。内置模式的预览与切换按标签表达，外置模式显示实际分屏布局。

内置终端提供有界历史、选择复制、输出搜索、安全多行粘贴和重连。关闭执行中视图不会结束 Agent；进程退出后保留输出供已打开视图回看，视图关闭即释放，不持久化聊天输出。具体协议、键盘和生命周期边界见 [内置终端设计](EMBEDDED-TERMINAL.md) 与合同 C15；输入法、真实键盘、资源和长稳结果须分别验收。

### 3.2 第一版必需能力

- Windows 11 x64，本地目录；普通用户权限运行。
- 拖入或选择目录、最近项目、组合保存、逐格 Agent/参数设置。
- 最多保存 16 个配置，本次启动项明确勾选；实际布局支持 1～16 格，但同时在跑的 Agent 窗格最多 6 个（固定并发池，超出由准入拒绝，保存容量不变）。本机与远程的非终态 Agent 都占这 6 个名额，见 3.4。
- 首次默认一个 Omp；启用第二个后按准入模式处理：实用（默认）与自定义模式带提示启动，保守模式需资源满足才启动。原稿“默认双开”由本次资源优先要求替代。
- 只保留 Omp、Codex、Claude Code 三种可启动类型；旧配置中的其他类型保留为只读历史并自动停用，改选受支持类型后才能重新勾选。
- 同一普通 Git 仓库可创建独立 worktree，保存分支和目录记录；检查跨批次的受管目录占用，保留原项目，人工审阅合并。
- 启动前资源检查、受控进程组 CPU/内存预算（包含 Git 准备）、轻量资源监控、可撤销授权、启动回执和异常恢复。
- 键盘完整操作、明暗/高对比度、DPI/文本缩放、可读错误与实际恢复入口。

### 3.3 暂不实现

自动拆任务、自动合并、自动结束/恢复空闲会话、WSL、内置代码编辑器、插件市场、自动更新服务、自动 worktree 清理、自动下载安装或登录 Agent。

不采集聊天记录，不保存 API Key 或 SSH 密码/私钥内容，不额外启动“管理 Agent”。第一版的资源监控是操作系统进程监控，不调用模型。SSH 远程 Linux 主机已改入 3.4，不再列为暂不实现。

### 3.4 远程执行（正式范围）

2026-11-27 行为补充：SSH 255 仅表示本地连接丢失，保留同步会话和占用，不在析构中 flush/terminate；远程工作 Shell 退出同样不终结原 Agent。操作员重新挂接打开同目录的工作 Shell，不恢复原 Agent 的 PTY/TUI。已支持的 `-p` 续接可反复规范化同一继续参数并替换本次说明；含不同会话选择参数仍拒绝。SSHFS 只挂载远端目录，不上传本地项目或构成双向同步。

允许把单个槽位指向已登记的 Linux SSH 主机，在远程目录启动受支持的 CLI Agent。本地配置窗口仍负责主机与槽位登记、启动预约、状态和回执；终端输入输出继续由 Windows Terminal 承载。远程进程不创建本地 Job，也不按本地内存或 CPU 压力拦截；远程主机负责其 CPU 与内存。

**三台同权（2026-11-18 产品决定）。** 对等的三台机器是操作者的本机 Windows（本机）、京东云 VPS（文档称京云）、阿里云 VPS（文档称阿里云）。文档不写真实主机名、地址、用户名或密钥。同权是工作区对等：任一台上的 agent 都可以修改共享的项目工作区，另外两台必须看到同一批文件。京云与阿里云上的 agent 仍在各自的远端机器上运行。同权的实现方式是必须的双向工作区同步。远端进程没有本机文件系统的直接访问权，也不能不经同步就改本机上的任意文件。

同权模式要求双向同步。推荐机制是 Mutagen。规格里已经允许的 SSHFS（`SshfsMount`）仍是原有挂载备选。不新增其他同步后端，也不把 SSHFS 新写成三台之间的同步机制。同步模式 `None` 下，各机只看见自己的目录，表现得像互相断开，因此不是同权。已实现的是 SSH 远程执行，以及可选的 Mutagen 或 SSHFS；当前实现仍允许选择 `None`。选了 `None` 的这次启动不是同权。本文不把「拒绝 `None`」写成已实现，也不把 §83–§84 已有的单对活测改写成三台同权已活测。2026-11-19 更新：主机编辑器保存与启动准备两层已拒收 `None`（IMPLEMENTATION §92）；本段其余内容保留为 2026-11-18 设计记录。

**6 个窗格名额包含远程条目。** 产品决定与当前代码一致：Worker 的 reserve/begin，以及界面 `ActiveAgentCount`（`UpdateLaunch` 用它决定能否启动），把每一个非终态 agent 计入 `MaxConcurrentAgents = 6`。本机和远程都算。把槽位放到 VPS 不腾出本地窗格名额。`Disconnected` 在操作员确认释放之前继续占一个名额。内存与 CPU 准入仍然不把远程批次算进本地压力，远程进程也不创建本地 Job。同权不改变这两条：远端主机的内存与 CPU 限额并不因此与本机相等。

认证使用 `ssh-agent` 或配置中的私钥路径，并以 `BatchMode=yes` 禁止交互输入密码。本机可以对已登记主机做免密连接自检、版本检查，以及显示确切命令并二次确认后的 Agent 升级（IMPLEMENTATION §83）。配置只保存主机地址、端口、用户名和可选密钥路径，不保存密码或私钥内容。每个槽位有独立的「运行位置」下拉，列出本机和已登记主机；Agent 下拉只列当前位置的程序（UX14，§84）。选择主机后绑定该主机，远程目录为空时按 `~/work/<项目名>` 派生，把运行位置改回本机则解除绑定。删除远程多余文件必须显式确认。同步会话名写入 `attempts/<id>/sync-session.json`，供同步徽章在配置窗口重启后继续匹配（§84）。槽位可以只读查看同步回本机的改动；详情可以只读查看远端资源。

远程内存上限已实现（2026-11-19 [规格](superpowers/specs/2026-11-19-remote-memory-limit-spec.md)，R45）：主机级 `MemoryLimitMib`（256–65536 MiB，留空不限制）在准备层换算为 `RemoteExecutionSpec.MemoryLimitBytes`，启动命令以 `systemd-run --scope -p MemoryMax=<bytes>` 包裹并先守卫 systemd-run 可用性（缺失即 fail-closed）；出界值拒收。界面可以宣称该限制，但必须连同「远程主机需有 systemd-run」一并说明。远程启动命令另注入固定的 `NODE_OPTIONS=--max-old-space-size=1024`（1024 MiB，只作用于 Node 老生代堆，非 Node 进程忽略该变量）；该注入仍是护栏，不是内存上限合同本身。真实 VPS 上的 scope 生效、超限终止与非 root 拒绝路径仍是 opt-in 活测待办。

`Disconnected` 保持非终止，直到操作员在「核对并确认释放」中判断远程进程已不在、填写原因并二次确认（协议 `release-remote`，回执 `operator-release.json`，R28，§83）。判断为仍在或无法判断时不释放。这条回归使用 fake-ssh，不依赖真实 VPS。为在实际使用中维持同权，操作员可以另用「核对并确认重新挂接」（协议 `reattach-remote`，回执 `operator-reattach.json`，R33，§89）：只有确认后才核对远程进程；仍在则把同一条账本挂回同一主机、同一目录的本地 SSH，原来启用了 Mutagen 或 SSHFS 的会重新建立同步，同步模式为 `None` 时不会写成已同步，这次启动也不是同权；已不在、尚未启动或无法判断则拒绝，账本不变，不自动释放。确认释放不发起重新挂接，重新挂接也不代替确认释放。重新挂接不释放预算，也不把尝试标成已结束。自动重连不在这项要求内，也未实现。确认释放只结束本地账本占用，不承诺结束远程主机上的进程。

真实双 VPS 活测和发货预览包复验已记在 IMPLEMENTATION §83–§84，文档只引用证据路径。R29 是 opt-in 记录，不进 CI，也不是浸泡或长稳。§84 记录的最近一次完整门禁是 `scripts/test.ps1` 退出 0；该节对隔离 `--ui-check` 写出 397/397，对完整门禁的 UI 条数没有另写数字。本文档对齐没有重跑门禁，不新增通过数，也不补写完整门禁的 UI 条数。

上述能力仍是技术预览。下面这些不因本节关闭：G4；ACCEPTANCE §11 浸泡；独立人类可用性；远端 CI；配置窗口完全退出后的会话通知补播（托盘后台可以继续）；同标题终端召回可能选错窗口；句柄全部丢失的旧池仍须重启后再核对；工作集回收不证明 commit 下降。

远程执行的状态、分域、校验、同步和安全合同见 [远程执行规格](superpowers/specs/2026-11-17-remote-execution-spec.md) 与 CONTRACTS C14。已收口项与仍开放的发布条件见[收口计划](superpowers/plans/2026-11-17-gap-close-plan.md)。冲突、离线与传播延迟的目标行为见 [同步可靠性设计](superpowers/specs/2026-11-18-sync-reliability-design.md)。该设计尚未实现；现有单链路徽章和 §83–§84 的单对活测仍保持原记录，不构成三台当前已收敛。

## 4. 使用流程

普通任务的独立目录策略对单项队列派发同样生效，实际 worktree 与尝试绑定。新任务继承已配置 CLI 路径/参数并可编辑程序位置；Node.exe 手工入口脚本保留在版本、交付、续接探针中，升级会改变身份。任务及远程主机表单在原窗口校验和保存，失败保留草稿。槽位查看改动按实际启动尝试的 Cwd 展示，不能改用当前项目目录。

任务文件当前为 schema 5，旧 1–4 只在内存迁移。写入前保存不可变内容快照；缺主文件但有备份/快照时停用队列。活动卡片 200、保留卡片 1200、活动尝试 1000 的上限不变；显式历史迁移每批最多 200 条已核实终态的旧尝试，保留最近尝试、依赖完成证据、验收中及续接引用，先核验归档再移出活动文件。单份归档最多 8 MiB、引用最多 10000；控制领取与原始证据不删除，归档仍可搜索，恢复/迁移后暂停队列。恢复工具不代表缺失的真实任务历史已经恢复。

```text
拖入项目 → 选择/复用组合 → 勾选本次启动项
                             ↓
                验证路径、CLI、Git 和资源
                     ↙                 ↘
      路径/CLI/Git 不满足：原地修复   满足：准备工作目录
                     （资源读数不足时实用模式提示后继续，保守模式要求先修复或等待）
                                          ↓
                                创建终端与受控进程
                                          ↓
                               显示逐项回执和异常
                                          ↓
                             在终端工作 → 审阅合并
```

常用路径不要求手输 shell 命令。首次建立组合后，通常只需“拖文件夹、点启动”；资源读数不足时，实用/自定义模式带提示直接启动，保守模式多一步释放资源或改用「等待资源后启动」。

同项目协作示例：两个 worktree 从同一提交出发，Omp 处理界面、Codex 处理启动逻辑；双方约定接口，最后人工合并并运行集成测试。有依赖的任务分阶段运行，不能凭窗格数量承诺线性提速。

## 5. UI 技能、语言和风格

- **视觉设计语言：Windows Fluent 原则，偏紧凑工具界面。** 使用原生交互习惯、低饱和中性色、系统强调色、明确焦点和有限动效。WPF UI 可提供接近 Fluent 的控件，但不把它称为微软官方 WPF Fluent 2 实现。
- **界面语言：简体中文。** Agent 名称、命令、路径保留原文；资源文本用用户能理解的词，例如“本次启动”“内存不足，暂缓启动”。
- **实现语言：C# / WPF XAML；当前技术预览以本机已安装的 .NET 8 编译，正式分发仍计划迁移 .NET 10 LTS。** 配置使用 JSON；脚本只用于开发测试，不作为产品主要 UI。
- **设计技能：`unslop` 用于方向、间距、色彩、文字层级和实际截图审查；`trellis-update-spec` 用于可测试合同。** 按原生应用适配，不照搬网页大标题、hero、图片生成或网页版尺寸。
- 文档排版、PPT、社交卡片类 skills 不用于本应用 UI；当前也没有一个可直接代替 WPF 可访问性测试的专用 skill。

详细五行设计方向、token、逐屏规格及状态见 [UI-DESIGN.md](UI-DESIGN.md)。设计完整不靠堆装饰，而靠常态、空态、失败、等待、禁用、恢复都定义清楚。

## 6. 技术架构与线程

```text
WPF 配置窗口（可关闭）
       │ 结构化配置、启动计划
       ▼
启动/工作目录准备层 ── 全局准备串行，独立 Job 计入总量
       │
       ├─ Windows Terminal：显示与交互
       └─ 轻量资源监控进程：仅有受管任务时运行
                  │ 资源准入、进程组、回执
                  ▼
            每格小型 Runner
                  ▼
       Agent 及可归属的工具子进程
```

| 组件 | 责任与限制 |
| --- | --- |
| WPF UI | 单个 UI Dispatcher；只呈现状态，不能同步等待进程/磁盘 |
| AgentCatalog | PATH/PATHEXT 与显式路径解析；后台探测上限 2 |
| LaunchPlanner | 不可变计划、有效格顺序、实际布局；纯逻辑可单测 |
| WorktreePreparer | 仓库身份、基线、分支、目录；全局准备 1，同仓库写入 1，跨批次目录占用 |
| TerminalLauncher | 仅结构化 Terminal 操作，不模拟按键，不依赖固定 sleep |
| ResourceMonitor | 单实例；预算/授权/目录占用的串行仲裁；1 秒接收节拍，受管采样最短间隔 2 秒；UI 状态轮询按 1/2/30 秒自适应；既无受管负载也无待核对提交且空闲 15 秒后退出 |
| PaneRunner | 原生控制台入口、逐格回执、受控创建和等待，不加载 WPF |

多线程支持有边界：优先 `async/await` 的异步 I/O；确有阻塞的操作才用有界后台执行。应用级并发限制为探测 2、配置写入 1、全局 Git 准备 1（内部同仓库写入 1）、进程提交 1。禁止 UI 上 `.Wait()`/`.Result`、每窗格一个无休止轮询线程、对整个项目递归扫描。

资源监控/Runner 拟用 .NET 10 原生 AOT 控制台组件，是否满足控制台兼容和资源预算要在首个技术阶段验证；WPF UI 不承诺 NativeAOT。若不能达标，先调整辅助层实现，而不是放松预算或假装它不占资源。

**与 v0.3 的重要调整：** 持续资源保护需要轻量常驻组件，因此取消“完全无后台”的承诺。它只跟随受管进程存活，不是开机服务；配置窗口关闭后仍单独报告其开销。

## 7. 性能与保护原则

详细公式和状态以 [CONTRACTS.md](CONTRACTS.md) 的 C2 为准。

- 首次默认一个 Agent；双开或更多必须通过当前可用内存、提交余量与 CPU 压力检查。
- 使用一个受管进程组的总 CPU 配额，Agent 与 Git 准备合计，不给每个窗格或阶段各一份整机配额。
- 默认每个受管会话给有界内存预算。硬内存上限会使超额分配失败，可能让 CLI/工具报错或退出；界面明确解释，不能同时承诺无限工具负载、极低内存和永不崩溃。
- 资源不足时阻止新增任务、保留工作目录，不自动重试造成启动风暴；不自动强杀已经在写文件的 Agent。
- Terminal 与配置窗口不放入 Agent 的 CPU 限额组，尽量保留交互响应。
- 保护范围是通过本应用创建、成功加入进程组的进程及受约束子进程。外部已有浏览器、服务、手工启动 Agent、GPU 驱动和磁盘负载不在完全控制范围内。
- 无法应用保护时显示原因并阻止该次受保护启动，不静默变成无限制运行。

本机只读采样（2026-11-09）：Windows 11，16 个逻辑处理器，AMD Ryzen 7 7735H；系统可见物理内存约 15.2 GiB，当时可用约 1.2 GiB。它是瞬时快照，不代表空闲基准。按暂定准入规则，此时应阻止新增 Agent；本轮没有启动压力测试或关闭任何用户程序。

目标为：启动器温启动 P95 ≤ 1 秒、拖入后的可见反馈 P95 ≤ 100 ms、UI 空闲 CPU 平均 < 0.5%；关闭 UI 后监控器和两个 Runner 合计 Private Bytes ≤ 64 MiB、空闲 CPU 平均 < 0.2%。这是待实测预算，替代 v0.3 未考虑持续监控的 30 MiB 假设。完整测量方法和桌面响应性门槛见验收文档。

## 8. 崩溃与数据边界

- 预期错误在功能边界处理；未知致命错误退出相关组件，保留最后有效配置和启动记录。不能用全局 `catch` 后继续执行掩盖损坏状态。
- 启动器崩溃不应结束已运行 Agent；监控器故障不自动终止其进程组。明确禁止由配置窗口持有的 kill-on-close 控制链。
- 没有启动回执只能判定“未确认”，不能自动视为“未启动”重试；尚未领取的请求可以持久撤销，已领取/正在创建则先核对，不直接释放预算。
- 配置原子替换、保留上个有效版本；版本迁移先备份；多实例同用户只允许一个配置写入者。
- Git 与进程创建存在跨进程崩溃窗口，用可追溯操作记录和核对恢复处理，不声称实现全局事务或 exactly-once。
- 不保证断电、系统崩溃或 Windows Terminal 被关闭后 Agent 继续运行；保留的是配置、Git 文件和可验证记录，不是运行内存。

## 9. 实施顺序与门禁

| 阶段 | 交付 | 必须通过的检查 |
| --- | --- | --- |
| G0 规格 | 本文、详细设计、合同、验收、review | 无未处理的规格冲突；产品形态为当前提案 A |
| G1 高风险技术验证 | 真实 Omp/Codex/Claude、创建时 Job 关联、授权撤销、Git 准备限额、故障回执与内存基线 | R13～R15/P09 的防护可行，交互正常，崩溃不连带运行任务；失败先修，不做大 UI |
| G2 原生交互原型 | 拖拽、组合、独立 Agent profile、完整快照、目录与资源提示 | F15～F17/V09 通过；键盘主流程、真实 WPF 截图与 DPI 验收 |
| G3 功能闭环 | worktree、跨批次占用、单实例配置、部分失败恢复、实际分屏 | F14/R16 及功能矩阵通过，数据和进程身份无歧义 |
| G4 发布候选 | 自包含包、测试/性能/视觉报告、已知限制 | P0/P1 实现问题为零，响应性门槛通过，再评估分发 |

用户已要求推进实现，因此 v0.6 / v0.6.1 是有明确验证边界的技术预览。G1～G3 有可运行证据，但尚未完成全部门禁；不以此宣称 G4 稳定发布通过。代码已自审，没有独立 reviewer。已记录实现见 IMPLEMENTATION §83–§84；仍开放项见[缺口收口计划](superpowers/plans/2026-11-17-gap-close-plan.md)。

远程执行是 v0.6.1 的范围增补。原 G1～G3 的本地证据仍然单独有效。R28 操作员确认释放和 R29 活测记录已经写入 IMPLEMENTATION。三台同权是 2026-11-18 的产品决定；操作员确认的重新挂接已写入 IMPLEMENTATION §89（R33，fake-ssh）。远程内存上限已实现为 systemd scope（[规格](superpowers/specs/2026-11-19-remote-memory-limit-spec.md)，R45；真实 VPS 活测待办）；自动重连仍未实现。远程执行仍然是技术预览；远端 CI、§11 浸泡和 G4 仍待单独评估。

## 10. 参考和来源

| 项目/资料 | 用途与限制 |
| --- | --- |
| [microsoft/terminal](https://github.com/microsoft/terminal) | 直接复用终端；MIT；本机已安装 1.24.12741.0，实际激活方式需测试 |
| [herdrdev/herdr](https://github.com/herdrdev/herdr) | 参考会话组织；本轮前序索引的 LICENSE 为 Apache-2.0，采用前再核具体版本 |
| [lepoco/wpfui](https://github.com/lepoco/wpfui) | 可选原生 Fluent 风格控件，MIT；不替代可访问性验收 |
| [hermes-hq/hermes-ide](https://github.com/hermes-hq/hermes-ide) | 产品交互参考；BSL 1.1 源码可见，非本项目代码基底 |
| [asheshgoplani/agent-deck](https://github.com/asheshgoplani/agent-deck) | 会话 UX 参考，Windows 安装路线依赖 WSL/tmux |
| [Git worktree](https://git-scm.com/docs/git-worktree) | 隔离目录、分支和共享元数据边界 |
| [Windows Terminal CLI](https://learn.microsoft.com/en-us/windows/terminal/command-line-arguments) | 分屏、命名窗口与参数传递 |
| [WPF 线程模型](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model) | Dispatcher、有界异步、避免 UI 等待 |
| [Windows 可访问性清单](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-checklist) | 键盘、文本、对比度；WPF 行为需实际验证 |
| [WPF 异常事件](https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.dispatcherunhandledexception) | UI 线程异常不覆盖所有后台异常 |
| [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects) | 进程组、继承、生命周期 |
| [创建时关联 Job](https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812) | 避免先创建再关联的孤儿窗口；多 Job 失败/回执恢复仍需单独验证 |
| [CPU 配额](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_cpu_rate_control_information) | 总 CPU hard cap、嵌套限制 |
| [内存限制](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_limit_information) | 硬上限让超额内存提交失败，不是无损压缩 |
| [.NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy) | 选择 .NET 10 LTS；本机目前只确认 SDK 8.0.419，尚未安装新 SDK |

来源核查使用官方文档与项目搜索索引，没有本地运行第三方参考项目或进行其代码审计；直连抓取受当前网络地址校验限制。采用前锁定版本、许可证和依赖。上述推荐不以 GitHub 星数作为性能证据。
