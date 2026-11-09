# 多 Agent 启动器规格

版本：v0.6 · 2026-11-09 · 已实现技术预览，完整发布门禁尚未通过  
本轮修订：原生四层架构、真实状态 UI、受控启动与 worktree、原型修复及分层测试证据。

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
| [prototype.html](prototype.html) | v0.6 离线交互稿，3 项状态问题已修复并通过浏览器回归 |

现已有 C# / WPF 原生源码、可运行技术预览、真实 Windows Job/Git/Terminal 测试和 WPF 截图。发布包入口和证据见 IMPLEMENTATION；不能将局部断言等同于 51 项完整验收通过。性能阈值仍是门槛，未测项目和失败数据必须保留。

本版本的强制防护：创建时关联受控 Job；可撤销且带代次的启动授权；Git 准备计入资源总量；物理 worktree 跨批次占用；Agent 独立 profile；完整组合快照与事务式应用；独立的最近编辑快照。具体责任在 CONTRACTS C1～C5，7 项追踪表在 ACCEPTANCE §8。当前共 51 项完整验收要求，已执行子集与未测范围见 IMPLEMENTATION，不能因文档更新或子集通过就关闭全部实现问题。

## 2. 用户目标与优先级

用户要拖入文件夹、自由选择 Pi 或其他 CLI Agent，在一个终端窗口中分屏运行；多个 Agent 能处理同一个项目。操作简单，设计稿详细，并考虑代码质量、视觉、功能和崩溃恢复。

**电脑保持可操作是第一性能目标和发布门槛。** 不以更多窗格、更高并发或更低单任务耗时换取桌面持续卡顿。数据完整性同时是不可牺牲的约束：不能靠无提示杀进程、丢工作区或吞异常达标。

明确区分：

- 保存四个 Agent 配置，不等于启动四个进程。
- 多线程用于避免 UI 阻塞；多个 Agent 是多个进程，仍消耗各自的资源。
- 同项目 worktree 提供文件隔离，不提供自动分工、实时共享修改或无冲突合并。
- 找到 CLI、提交终端命令、启动进程、Agent 完成初始化、完成任务是不同状态。
- 模型走云端时通常不加载本地模型权重，工具服务仍可能成为内存、CPU、磁盘的大头。

## 3. 产品形态与范围

### 3.1 推荐形态

采用 **Windows 原生配置窗口 + Windows Terminal 运行窗口**。拖项目、选 Agent、查看预算在配置窗口完成；终端的输入、输出、滚动、复制由 Windows Terminal 负责。启动后可以最小化或关闭配置窗口。

“一体化工作台，把终端嵌入同一应用”仍是替代方案，不是当前承诺。它需要另行承担 ConPTY、终端渲染、输入法和终端生命周期测试；若选择它，需要重算资源和工期。当前按更符合资源优先目标的轻量方案完成设计，不把普通样式选择变成逐项审批。

### 3.2 第一版必需能力

- Windows 11 x64，本地目录；普通用户权限运行。
- 拖入或选择目录、最近项目、组合保存、逐格 Agent/参数设置。
- 最多保存四个配置，本次启动项明确勾选；实际布局支持 1～4 格。
- 首次默认一个 Pi；启用第二个后，通过资源准入才启动。原稿“默认双开”由本次资源优先要求替代。
- Pi 必须真实验证；其余 CLI 作为预设逐项标注“仅找到命令 / 已验证版本”。自定义命令只承诺已测试的执行类型。
- 同一普通 Git 仓库可创建独立 worktree，保存分支和目录记录；检查跨批次的受管目录占用，保留原项目，人工审阅合并。
- 启动前资源检查、受控进程组 CPU/内存预算（包含 Git 准备）、轻量资源监控、可撤销授权、启动回执和异常恢复。
- 键盘完整操作、明暗/高对比度、DPI/文本缩放、可读错误与实际恢复入口。

### 3.3 暂不实现

自动拆任务、自动合并、自动结束/恢复空闲会话、远端/WSL、嵌入式终端、内置代码编辑器、插件市场、自动更新服务、自动 worktree 清理、自动下载安装或登录 Agent。

不采集聊天记录，不保存 API Key，不额外启动“管理 Agent”。第一版的资源监控是操作系统进程监控，不调用模型。

## 4. 使用流程

```text
拖入项目 → 选择/复用组合 → 勾选本次启动项
                             ↓
                验证路径、CLI、Git 和资源
                     ↙                 ↘
             不满足：原地修复        满足：准备工作目录
                                          ↓
                                创建终端与受控进程
                                          ↓
                               显示逐项回执和异常
                                          ↓
                             在终端工作 → 审阅合并
```

常用路径不要求手输 shell 命令。首次建立组合后，通常只需“拖文件夹、点启动”；资源不足时多一步释放资源或减少本次启动项。

同项目协作示例：两个 worktree 从同一提交出发，Pi 处理界面、另一个 Agent 处理启动逻辑；双方约定接口，最后人工合并并运行集成测试。有依赖的任务分阶段运行，不能凭窗格数量承诺线性提速。

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
| ResourceMonitor | 单实例；预算/授权/目录占用的串行仲裁；2 秒采样；既无受管负载也无待核对提交时退出 |
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
| G1 高风险技术验证 | 真实 Pi、创建时 Job 关联、授权撤销、Git 准备限额、故障回执与内存基线 | R13～R15/P09 的防护可行，交互正常，崩溃不连带运行任务；失败先修，不做大 UI |
| G2 原生交互原型 | 拖拽、组合、独立 Agent profile、完整快照、目录与资源提示 | F15～F17/V09 通过；键盘主流程、真实 WPF 截图与 DPI 验收 |
| G3 功能闭环 | worktree、跨批次占用、单实例配置、部分失败恢复、实际分屏 | F14/R16 及功能矩阵通过，数据和进程身份无歧义 |
| G4 发布候选 | 自包含包、测试/性能/视觉报告、已知限制 | P0/P1 实现问题为零，响应性门槛通过，再评估分发 |

用户已要求推进实现，因此 v0.6 增加有明确验证边界的技术预览。G1～G3 有可运行证据，但尚未完成全部门禁；不以此宣称 G4 稳定发布通过。代码已自审，没有独立 reviewer；实测、缺口与下一步记录在 IMPLEMENTATION。

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
