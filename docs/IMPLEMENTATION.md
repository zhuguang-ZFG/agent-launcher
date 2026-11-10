# 实现与验证记录 v0.6

日期：2026-11-09。结论：**已交付可运行技术预览；未达到完整稳定发布门禁。** 本页区分实现、已测子场景、未测要求。测试由实现者执行；§0 第三轮经三个独立评审代理审查并修复确认项，仍无人类独立可用性测试。

## 0. v0.7 界面重设计实施（2026-11-09 续）

按 [UI-REDESIGN-v0.7.md](UI-REDESIGN-v0.7.md) 执行，未修改资源保护与启动合同。本轮改动：

- 设置对话框改为类型化控件：主题为「跟随系统/浅色/深色」下拉，最小化为复选框，不再要求输入英文枚举和布尔文本；`FormWindow` 新增 choices/bools 字段类型，`Values` 字符串合同不变。
- 配置行固定列：启用、编号、Agent、短命令状态、详情、更多（上移/下移/移除）；详情从菜单移出为固定按钮，菜单项禁用态改为打开时求值。
- 行等高 48 DIP、行间细分隔线替代斑马色块、状态文字省略号截断；阻止原因文字在不可启动时降为辅助色。
- 修复本轮实施前已存在的三处破坏：配置行 Grid 子元素缺少 `Grid.SetColumn`（全部叠在第 0 列）且 `ColumnDefinition` 构造调用无法编译；`MenuButton` 构造函数急切求值 `Owner`（控件未入树时 `Window.GetWindow` 返回 null，`Render` 抛异常）；深色主题自 v0.6 起窗口背景不随主题（Style 内 `DynamicResource` 不随画刷替换更新），现由 `ApplyTheme` 直接赋值窗口 `Background`/`Foreground` 并新增断言回归。

验证（`scripts/test.ps1` 退出 0）：.NET 模型/Win32 11/11；隔离集成 11/11；WPF 断言由 13 项增至 **28 项**（新增：空态不显示错误横幅、错误状态显示横幅、预览编号与配置项一致、配置行固定列位置、更多菜单项集合、菜单打开时禁用态（首项上移禁用）、设置类型化控件、主题资源切换、窗口背景随主题、四种主题×宽度组合下面板实际宽度——窄窗口双面板铺满工作区、宽窗口右栏 320 DIP）；HTML 模型 5/5；Edge DOM 5/5。新截图：[浅色 1040](../artifacts/ui/native-light-1040.png)、[深色 800](../artifacts/ui/native-dark-800.png)、[窄窗口 650](../artifacts/ui/native-light-650.png)、[深色 1040](../artifacts/ui/native-dark-1040.png)（120 DPI）；[断言结果](../artifacts/ui/ui-checks.json)。预览包已重建（`artifacts/preview`，不含测试后门）。

第二轮修复（同轮发现）：窄窗口（<900 DIP）两列均设 1* 而两个面板同处第 0 列，内容只占半宽——现窄模式右列归零，面板铺满；「选择组合」下拉由居中改为左对齐。

第三轮：独立代码评审（三个并行评审代理：App 层、Core/Worker 合同、IPC/进程/命令边界）。确认并修复：

- **S1（Worker）**：损坏/超限 `control.json` 使监控器启动即抛异常退出，GUI 反复拉起形成崩溃循环。现监控器进入持久阻止态：账本结构含 null 条目/Plan 一并视为损坏；阻止期间不做 boot 对账、不写账本（损坏原文与 .bak 字节经两次启动校验不变）、不自行退出，GUI 显示明确人工核对指引。负向冒烟：`--screenshot` JSON `LaunchEnabled=false`、错误文案命中、监控器进程两次启动同 PID。
- **S2（App）**：`configuration.json` 丢失但 `.bak` 存在时此前静默重置为空配置。现进入只读阻止态并提示「恢复备份」入口；负向冒烟确认不创建新配置、启动禁用、文案命中。
- **A1（App）**：`LoadAsync` 捕获过滤器漏 `InvalidDataException`，配置守护自身抛出的异常逃逸导致只读标记未置位。已补捕获；负向冒烟两条：损坏 JSON（JsonException 分支）与**结构合法但 SchemaVersion=2**（命中守护自抛的 InvalidDataException 分支，旧代码会逃逸）均确认只读文案、启动禁用、配置文件字节不变且不产生 .bak 搅动。
- **A2（App）**：重复启动闸门只看修订号，编辑挂起中的启动项后可重复提交。现闸门加入「存在未终结启动项」并给出提示；`LaunchClick` 入口同样拦截。
- **A3**：浏览/拖入单目录现统一经 `SelectProject` 进入最近项目列表（此前仅手动输入记录）。
- **A4**：恢复备份补上与加载一致的 null/结构校验。
- **A5**：行内编辑后按 AutomationProperties.Name 恢复键盘焦点（仅限 SlotList 内，项目输入框不抢焦点）；新增断言「row edit restores keyboard focus」。
- P2 批量：系统主题注册表读取加防护；`RefreshAsync` 失败改走 `UpdateLaunch` 统一状态并跟踪 `commError` 避免状态不一致；命令检查失败兜底（非 OOM 均归一为可见错误而非卡死）；选择/拖放补 `readOnly` 门禁；`FormWindow` 未知选项回退追加当前值项、布尔解析忽略大小写、移除无效 Ctrl+Enter 处理器；TextBox 模板 ScrollViewer 透传滚动条可见性；资源管理器 `Process.Start` 句柄释放；UiChecks 移除死代码并强化面板堆叠断言（列/行位置）。
- 评审驳回项：C3「计划摘要未与账本基线比对」及「shim 空白绕过」不成立；C5「误杀已恢复进程」指控撤回：Runner 在持有 AttemptLock 时先持久化 ResumeIntent 再 Resume，Refresh 获取同锁后重读回执，仅回收 SpawnIntent/CreatedSuspended。仍保留 §6 的 ResumeIntent 已写但 Resume 未执行时暂停进程保留限制；四分屏焦点仍属于未完整验证项。

WPF 断言增至 **34 项**（新增：配置加载可写、监控器健康、行编辑焦点恢复、面板行列位置）。全门禁 `scripts/test.ps1` 退出 0（PASS 29 行）；预览包已重建。损坏场景负向冒烟脚本位于 `D:/Temp/User/smoke-corrupt.ps1`（仓库外）。

第四轮：仓库发布到 GitHub（私有，`zhuguang-ZFG/agent-launcher`）并建立 CI（`windows-latest` + .NET 8 + Node 22 + 真实 Pi CLI `@earendil-works/pi-coding-agent@1.5.0`，完整 `scripts/test.ps1`）。修复 CI 暴露的真实缺陷：`Commands.Resolve` 的 npm/Pi shim 模板常量直接以源码内嵌换行比较，CRLF 检出（Windows runner autocrlf）下模板含 `\r\n` 而 shim 文本被归一化，必然失配——现两侧均归一化，并加 `.gitattributes` 锁定 `.cs/.xaml` 为 LF。集成测试「Real Pi version via recognized adapter」失败消息现携带实际解析路径。CI 最终运行 `completed/success`：模型 11/11、集成 0 失败（含真实 Pi PASS）、WPF 34 断言、原型/浏览器 5/5。

第五轮：修复开放 P2 缺陷（评审残项）。

- **预约超时自动撤销**：`Ipc.ReserveWithCleanupAsync` 在客户端超时/IO 失败时对所有计划 ID 尽力 revoke（服务端可能已提交）。`INTEGRATION_TESTS` 下新增掉包钩子 `AGENT_LAUNCHER_IPC_DROP_AFTER_SEND` 模拟「已提交但客户端超时」，集成测试断言该授权最终为 `Revoked`。
- **管道被占用不再击穿监控器**：`NamedPipeServerStream` 创建移入带捕获的循环，失败时写入 blocked 并在 1s 后重试，不再退出。冒烟：外部持有同名管道 → `--screenshot` 显示启动禁用、错误可见、监控器 3 秒后仍存活且 PID 不变（旧行为立即退出并被反复拉起）；释放后下一轮恢复健康、错误清空。
- **撤销移除不再整快照回滚**：只把被移除项插回原索引且保留其间无关编辑；容量已满（4 项）时拒绝并提示，不触发 `Validate` 异常。同时修复真实崩溃：移除处理器在 `Edit→Render` 之后继续使用 `Window.GetWindow(this)`（控件已被移出树）会抛异常直抵 Dispatcher 导致关停——现在先捕获窗口引用再操作。UiChecks 新增 6 条断言（移除/撤销/满容量拒绝/只读回滚）。
- **准备记录防护**：`PreparationRecord` 增加 `SchemaVersion`；结构校验（Digest/Original/Completed/Baselines/各计划字段与 Guid）；未知版本 fail-closed 且不改写文件；旧版（无版本字段）记录校验后原地升级、保留 `Original` Id/批次/`Completed`/`Baselines` 身份，不重建 worktree；命令可执行文件缺失时保留 worktree 映射、只按草稿顺序重解析命令并回写 `Completed`。集成 F09 扩展覆盖：陈旧命令修复（Cwd 序列不变）、结构损坏拒绝、旧版记录采纳（Cwd 与 Id 身份保留）、版本 2 拒绝且文件字节不变。
- **UI 状态一致性**：`Edit` 返回是否被守卫拒绝；只读时复选框/Agent 选择器回滚到原值；`QueueSave` 增加 loading 守卫；启动提示链补齐只读/未启用/监控器未就绪/命令检查中文案，不再在禁用时显示「检查已通过」。

- **准备记录别名回归**：新建记录时 `plans` 曾直接别名 `record.Original`，worktree 循环回写会把 `Original` 的源目录改写成 worktree 身份，重试时分组消失并跳过基线/分支核对。现 `Original` 保持源路径（`.ToList()`），并新增回归：记录改写检测、已准备 worktree 的 HEAD 漂移必须拒绝、`reset` 后恢复可复用。
- **监控器接受连接生命周期**：待接受连接任务改为跨 2s 采样 tick 保留，避免每次采样都取消并释放监听实例；空闲退出窗口为 30s。曾观察到客户端写入 `Pipe is broken`，但没有故障当时的 PID/存活时序证据，不能断言是空闲退出导致。现有 12 次间隔 500ms 的 status 测试属于基本连通性回归，不保证命中 2s 无连接边界，也不证明所有退出竞态已消除。

回归：`scripts/test.ps1` 退出 0（PASS 28 行：模型 11、集成 12 含新增超时撤销、浏览器/原型 5/5）；WPF 断言 34→**40 项**。

第六轮：准备记录损坏校验闭环（本次继续修复）。

- **根因（隐含假设 / 跨层合同）**：C# 非空类型声明不禁止 JSON 显式 null。旧 `Usable` 直接读取 `Command.Executable.Length`，未核对 Position、Original ID 与 Completed key；后续升级、数组索引及 `Single` 先于完整校验，导致 NRE/越界/关联异常，或把坏旧记录升级写回。
- **校验顺序**：先验证嵌套命令/参数/目录/工作区字段与基线 OID，再对照当前草稿生成的有效计划核对启用项数量、顺序、Position、源目录/物理身份、名称/模式/预算、共同批次和唯一 ID；Completed 的 key、计划 ID、Position、BatchId 必须关联同一 Original。通过后才允许旧记录升级或修复可执行路径。公开 DTO 和 `PrepareAsync` 签名未改变；不把缺失的可执行文件视为结构损坏，仍保留已测的命令重解析路径。
- **失败前 / 修复后**：23 种坏记录分别以无版本字段旧形态及 v1 序列化，共 46 个场景。修改生产代码前运行时全部失败，出现 NRE、越界、`Single` 异常、成功重放或旧记录被改写；同一组场景修复后全部通过，要求抛 `IOException` 且主文件与 `.bak` 字节都不变。另加正常启用第 2/4 项的复用回归，避免把 Position 错当连续下标。证据：[RED](../artifacts/validation/preparation-red.json)、[GREEN](../artifacts/validation/preparation-green.json)。
- **本机完整门禁**：`powershell -NoProfile -File scripts/test.ps1` 退出 0；模型/Win32 11、隔离集成 60、WPF 40、HTML 模型 5、Edge DOM 5，未跳过。集成实际执行 Worker/Job、真实 Pi 版本、临时 Git worktree 准备/重用/HEAD 漂移拒绝。没有覆盖率工具，不声称覆盖率数字。
- **预览隔离**：串行重建正常预览后，对 App/Windows/Worker 三个程序集同时检查 ASCII 与 UTF-16 标识；7 个测试入口/环境标识在对应预览程序集均不存在，而对应测试程序集均存在作为阳性对照。证据：[程序集标识与 SHA-256](../artifacts/validation/preview-hooks.json)。该检查不等同于完整安全审计。
- **operator 验证状态**：已调用 `task_verify run`，返回 `unverified`，原因 `project-or-git-baseline-unavailable`；没有执行 operator 配置检查。仓库现在有 Git，这个返回不能解释为“仓库没有 Git”。本次新增测试矩阵与正常映射回归待用户审阅，实际测试通过不替代固定基线审批。工作流、现有断言及阈值未放宽。

第七轮：深度评审（评审基线 HEAD `5b6b41d`）确认的 10 个缺陷全部修复，公共契约与既有断言未放宽。

- **启动授权泄漏闭环（F1/F2）**：`ControlledCommand` 预约改走 `Ipc.ReserveWithCleanupAsync`；Runner 退出后条目仍为 `Pending`（begin 被拒或从未发送）时尽力 revoke 并抛 `IOException("准备命令未运行：…")`，区分「从未启动」与子命令退出码，`config --bool` 的启动失败不再被误读为键不存在。集成回归：掉包后自动撤销；新增 `AGENT_LAUNCHER_FAIL_AT=BeforeBegin` 故障注入验证 begin 失败释放全局 Git 槽。
- **准备重放命令刷新（F3）**：重放不再仅在可执行文件缺失时修复；每次按草稿重解析并比较 Executable/Arguments/Adapter，漂移即更新记录，仍保留 worktree 映射与 Id/批次身份。集成回归：shim 入口脚本改写后重放产出新 argv。此条取代第五轮「仅缺失时重解析」的行为描述。
- **status 帧字节上限（F4）**：历史由固定 40 条改为 active 全保留、terminal 从新到旧累计 ≤768 KiB，保证响应帧低于 1 MiB 写上限。集成回归：40×28000 字符参数后 status 仍可读且 terminal 条目数受字节约束。
- **启动闸门语义（F5）**：`RefreshAsync` 逐项过滤，只保留仍为 `Pending` 的提交 ID；已确认运行/终态自动放行，清空时重置 `submittedRevision=-1`。修复「已运行会话永久阻塞后续批次」。UiChecks 回归：Pending 保持闸门、Runner 到 Started 后闸门释放、条目最终 Settled。
- **配置 fail-closed（F6/F8）**：`LoadAsync` 结构检查补 `Templates` 值 null（此前 NRE 逃逸）；恢复备份补 null 模板值与 `LastEdited.Validate()`，校验在确认对话框之前，拒绝时主文件字节不变（此前会把损坏主文件轮转入 `.bak`）。UiChecks 回归：null 模板值进入只读阻止态且启动禁用、坏备份恢复被拒且主文件不变。
- **UI 动作生命周期（F7/F10）**：撤销移除在容量已满/条目已存在/`Edit` 被拒时不再消耗按钮，释放容量后重试成功；`Edit` 在 readOnly 拒绝后重渲染，显示与模型重新对齐。UiChecks 回归：拒绝后按钮保持可用、释放容量后恢复成功、只读模式切换被拒且显示回弹。
- **shim node 选择（F9）**：npm shim 保留模板自身的相邻 `node.exe` 优先；pi shim 按其自身 `SET "node=node"` 语义走 PATH，不再绑定相邻 stub。单元回归覆盖两个方向。
- **测试基建新增（待评审）**：Worker 故障注入取值 `BeforeBegin`；`--ui-check` 加重入守卫（检查内调用 `LoadAsync` 不再递归执行整套检查）；F5 UI 回归先把监控器重启到 `AGENT_LAUNCHER_FAKE_SAMPLE` 确定采样（本机可用内存 3.6 GiB 低于 2 GiB 准入门槛，真实采样下预约被正确拒绝），结束后恢复真实采样跑最终健康断言。
- **RED/GREEN 证据**：stash 四个产品修复文件后新回归全部失败（单元 1、集成 4；UI 旧行为证据即评审探针 `artifacts/review/ui-results.json`）；恢复后全门禁退出 0。探针断言已反转为修复后行为并复跑通过（exit 0，6/6）：[ui-results-fixed.json](../artifacts/review/ui-results-fixed.json)。**按轮次锁定政策，本轮全部新增测试标记 unverified，待评审后方可计入基线。**
- **本机完整门禁**：`powershell -NoProfile -File scripts/test.ps1 -SkipBrowser` 退出 0；模型/Win32 13、隔离集成 64（0 失败）、WPF 53 断言、HTML 原型 5/5；浏览器步骤本次跳过，未重验。

未声称完成：人工视觉审美评审、Narrator/高对比度实测、100/150/200% DPI 矩阵。几何与资源断言不冒充视觉验收。

## 0b. 界面文案与视觉精修（2026-11-10）

目标：美观、实用、0 门槛。不改布局结构与公共合同，只做表达层：

- 全面口语化：本次 Agent→要一起启动的 Agent；x/y 项启用→已选 x 个，共 y 个；终端布局/配置示意→启动后的终端样子/实时预览；两项排列 左右/上下→两个窗格的排法 左右并排/上下叠放；工作目录 各自独立等→文件夹怎么用 各自独立（互不影响）/用已准备好的目录/共用同一个文件夹；资源预算→电脑资源；未启用→未勾选；选择组合→套用组合方案…。
- 0 门槛空态：无项目时项目面板显示三步引导（选文件夹→勾选 Agent→点启动）；启动按钮旁始终用大白话写明禁用原因。
- 准入原因口语化（Core ResourcePolicy 五条），保持语义精确。
- 视觉：右栏间距收紧使资源区更易露出；主按钮字重 SemiBold。等宽终端预览格保持不变（业务含义）。
- 无测试断言改动：所有被替换文案均无测试钉住（已 grep 核实）；门禁 53 条 UI 断言原样通过。

## 1. 运行入口与架构

运行 [AgentLauncher.App.exe](../artifacts/preview/AgentLauncher.App.exe)，保留同目录所有文件。当前为 framework-dependent Windows x64 包，需要 .NET 8 Desktop Runtime。当前机已安装 SDK 8.0.419；没有擅自安装全局 SDK。正式分发仍待 .NET 10、自包含打包及复验。

工程：Core 配置/规则 → Windows 系统接口 → Worker 监控器和 Runner → App WPF。具体依赖、并发与 UI 信息来源见 [ARCHITECTURE.md](ARCHITECTURE.md)。Git、Terminal、Node 和 Agent 使用本机已有程序，不随包复制。

已实现：

- WPF 中文窗口、目录选择/输入/多目录拖入、最多四项配置、启用/新增/移除/上移、逐项程序与参数/目录/备注、组合完整快照和 LastEdited。
- 明暗主题、最近项目、有效窗格预览、真实命令探测、资源数值和采样时间、保存状态、逐项启动/退出/未确认状态、撤销尚未领取项。
- 资源预算、共同 CPU Job 35%、子 Job 内存限额、创建时关联 Job、单次授权、暂停创建阶段回执、物理目录占用和恢复核对。
- 受控 Git 准备、固定基线、流式检出大小估计、磁盘余量检查、独立 worktree、持久准备记录和同配置核对重用。失败保留目录；不自动合并、清理或复制依赖。
- 配置/回执跨进程读写互斥、临时文件 flush/原子替换/备份、配置损坏恢复入口；工作目录查看、打开文件夹、复制分支。
- Windows Terminal 命名窗口和分屏提交；调用只传 Worker 与 GUID，用户参数不经过 Terminal shell 拼接。

## 2. UI 信息实际来源

| 信息 | 来源 / 行为 |
| --- | --- |
| 项目与覆盖目录 | 当前草稿；输入/选择/拖入后保存；启动前校验物理身份 |
| Agent 名称与程序 | agentId 对应 profile；后台仅检查启用项；缺失明确显示，不伪装已登录 |
| 参数 | 每行一个 argv，不要求 shell 引号；取消编辑无副作用 |
| 布局 | 只显示启用项，保留原位置编号；三项左一右二，四项四格 |
| 内存/CPU | Win32 采样，显示时间；失败不填零、不显示通过 |
| 占用/启动结果 | 监控账本、Job 成员和 Runner 回执；不是示例数据 |
| 保存状态 | 实际原子写入结果；失败保持“尚未保存” |
| 分支/基线 | 实际准备记录；中断标为待核对，不能误称准备完成 |

HTML 仍是示例数据；真实资源读数只在原生应用中显示。

## 3. 最终回归结果

执行入口：`scripts/test.ps1`。最终完整运行退出 0。

| 测试层 | 最终结果 | 覆盖与限制 |
| --- | --- | --- |
| .NET 模型与 Win32 | 11/11 通过 | profile/快照、低内存拒绝、备份、缺字段拒绝、80 次替换与 200 次并发读取、目录身份、暂停创建归属、精确 argv/cwd、内存分配拒绝、句柄保留、CPU 配额查询 |
| 隔离集成 | 11/11 通过 | 授权撤销/迟到、跨批次冲突、回执、受控 Git、Pi 版本、监控器重启、3 个 Runner 崩溃阶段、worktree 隔离/重用、真实 Terminal 双窗格 |
| WPF 控件与布局 | 13 项断言通过 | 实际控件事件、模式/profile 切换、三/四格坐标、三种窗口下启动按钮边界、可访问名称；没有替代 Narrator 和人工视觉评审 |
| HTML 模型 | 5/5 通过 | Agent 切换、深快照、目录策略、取消/冲突、LastEdited |
| Edge 实际 DOM 点击 | 5/5 通过 | 原型三项缺陷的实际点击往返，非最小 DOM 替身 |

这些数量是测试断言/测试用例，不是将 ACCEPTANCE 的 51 个完整场景全部判定通过。

隔离集成的环境和证据：

- Windows 11 10.0.26200、Ryzen 7 7735H、16 逻辑处理器、约 15.2 GiB 可见内存。
- Terminal 1.24.12741.0；Pi `--version` 实测 **1.5.0**。仅版本和执行入口验证，不等于中文输入、流式模型输出和交互确认都通过。
- 真实内存从约 6.8 GiB 降至 1.37～2 GiB 时，生产准入拒绝新增任务，保留系统余量。
- 协议集成使用 `INTEGRATION_TESTS` 编译常量下的模拟准入采样，以少量短时辅助进程验证真实 Job/管道/崩溃/Git/Terminal；**不作为真实资源准入或压力成绩**。
- 模拟采样、故障注入、截图/自测入口和临时根目录覆写均不编入 preview 正常构建。
- 原始结果：[集成测试](../artifacts/integration-results.json)、[WPF 断言](../artifacts/ui/ui-checks.json)、[浏览器断言](../artifacts/prototype-browser-results.json)。

## 4. 原生截图和轻量实测

截图在实际 WPF 窗口上生成，120 DPI（125%）：[浅色 1040](../artifacts/ui/native-light-1040.png)、[深色 800](../artifacts/ui/native-dark-800.png)、[窄窗口 650](../artifacts/ui/native-light-650.png)。当前模型无法直接查看图像像素，因此只声明截图生成与几何/控件断言通过，不宣称已完成视觉审美/像素审查。

[60 秒空闲原始采样](../artifacts/ui-idle-60s.json)：30 个样本，每 2 秒采集一次；没有清空工作集或手工强制 GC。

- GUI Private Bytes 约 **85.1～96.0 MiB**，整机口径平均 CPU **0.144%**（16 逻辑处理器）。这是一次真实低负载空闲观测，符合该窗口的 P02 数值目标。
- 同轮 Working Set 约 154～165 MiB，单独报告，不与 Private Bytes 混用。
- 多次编辑/截图的短测曾达到约 101～103 MiB Private Bytes，保留为峰值观察，不能用单次空闲值声称所有场景低于 100 MiB。
- 单个监控器在集成结束时曾测得约 16.5 MiB Private Bytes；监控器 + 两 Runner 的正式 60 秒 P03、实际双 Agent 压力和 2 小时存活测试尚未执行。

60 秒测量之后增加了每文件互斥和减少无变化控件重建。它是相关版本基线，最终二进制没有重跑完整性能矩阵；不能称为发布性能认证。

## 5. 本轮发现并修复的实际问题

| 问题 | 修复 / 证据 |
| --- | --- |
| 原型切 Agent 残留程序/参数 | 独立 profile；模型与 Edge 点击往返通过 |
| 模板漏掉目录策略、上次组合重置 | 完整深快照、LastEdited；模型/浏览器/WPF 对应断言 |
| Win32 创建进程后 .NET Process 无法读取退出码 | 创建时打开并保留 Process Handle；原生测试通过 |
| 最后一个 Job 句柄关闭后不能假设可按名重开 | monitor/Runner 分别持有句柄；双失联不能证明退出则保持 Unknown，禁止释放；不是误判任务已结束 |
| WPF Closing 内再次 Close 重入 | Dispatcher 后续关闭；原生测试退出码 0 |
| 高速双击在异步保存前可重复提交 | 在首个 await 前冻结启动状态；同版本未确认计划不重复提交 |
| Windows ReplaceFile 与并发读取竞态 | 每文件跨进程互斥 + 有界共享错误重试 + 不先 File.Exists 读；新增并发测试，最终整套通过 |
| 重试准备可能重复生成目录 | 草稿摘要绑定准备记录，固定基线，核对重用目录，启动授权 ID 每次新建；集成覆盖 |
| 多项布局偏好残留影响三/四格 | 三/四项强制对应拓扑；原生预览坐标断言，双格真实 Terminal 通过 |
| 连续编辑可能积压保存/探测任务 | 保存与探测各保留最新请求，单消费者；会话显示无变化不重建控件 |

## 6. 完整发布仍受阻的事项

这些不是已经完成的项目，不应删掉门禁或调整阈值掩盖。

| 范围 | 当前边界 / 后续要求 |
| --- | --- |
| 实际双 Agent 压力 | 当前桌面余量不足，未绕过正常准入运行真实工作负载；P05～P09 的 60 秒/2 小时、磁盘与桌面交互仍需资源充足环境 |
| 原生交互与视觉 | 尚未完整测试 Pi 中文输入/确认、所有 CLI、所有分屏布局、100/150/200% DPI、Narrator、高对比度全控件与独立可用性 |
| 极端恢复 | ResumeIntent 写完而 Resume 未执行时保守保留 Unknown；monitor/Runner 同时丢失全部 Job 句柄仍需人工/重启核对。不能宣称所有崩溃窗口均无挂起残留 |
| 工作目录 | 创建前/后有身份核对，但恶意路径替换的竞态和外部编辑器不在强制防护内；bare/复杂子模块/网络盘不提供自动准备 |
| 配置与 UI 完整性 | 已补逐项参数、通用组合、搜索/最近 CLI、名称、拖动/单项目录、文字缩放、诊断导出和管理入口；单项重试只允许确认未运行的记录，具备持久去重。物理拖动/完整读屏和极端小屏仍需独立桌面验证 |
| Git 大负载 | 全局准备锁、受控 Job、流式估算已实现；filters/LFS 展开、磁盘保留线运行中监测及受控重负载仍待验证 |
| 资源策略 | 实现采用更保守的完整预算预留，尚不使用每 Job 实测驻留/提交量抵扣；可能比公式版本更早拒绝，不会因此扩大预算 |
| 规模与长期记录 | 当前 JSON 控制账本受 8 MiB 读取上限约束，没有历史归档；达到上限需停止新增并保留记录，不能自动删 Unknown；长期门禁尚未完成 |
| 分发 | 已生成 .NET 8 win-x64 自包含候选目录与普通预览，Worker 启动 smoke 和测试标记缺失检查通过；候选 GUI 未在干净用户环境运行。.NET 10 LTS、签名、AOT 辅助层及完整发布包验收仍未完成 |

## 7. 第二轮：真实使用驱动与本机 CLI 自主发现

历史真实使用驱动（pywinauto UIA pattern-only，`artifacts/real-use/drive4.py`）跑过配置旅程，issues=0；其启动阶段因准入拒绝未执行，不能称为完整启动旅程或分屏可见性通过。

| 问题 / 需求 | 修复 / 证据 |
| --- | --- |
| 弹窗深色主题白底白字 | FormWindow 显式 opt-in 隐式 Window 样式（派生类不自动继承）；`shots4/06-detail.png` 目检 |
| UIA TogglePattern 勾选不生效（Narrator/自动化用户） | 复选框改挂 Checked/Unchecked（含防重入与拒绝回滚）；UiChecks 回归 2 断言 |
| 撤销按钮看不见 | 插入撤销前自动展开会话面板；驱动验证「移除→撤销」摘要往返 |
| 标题/正文窄窗被裁切 | Caption/Heading 显式样式补回 TextWrapping=Wrap |
| 支持 omp / kimi code / qoder | Agents.All 增 3 预设；文档与原型同步 |
| 自主搜寻本机 Agent CLI | `Commands.DiscoverInstalls`：PATH + npm/pnpm/yarn/scoop/bun 全局目录，有界不递归；FindExe 同路径集合 |
| 手动写路径 0 门槛化 | 详情对话框「程序路径」变为可编辑下拉框：列出发现的本机安装，仍可自由输入或「浏览」；驱动实测 pi/codex 均出现下拉并成功写入 cmd 探针 |

测试新增（按政策待评审）：单元 `R-discovery searches install dirs beyond PATH`；UiChecks 4 断言（TogglePattern ×2、发现下拉 ×2，均为行为断言、未弱化既有断言）。

历史门禁：单元 14/14、集成 64 测试通过（67 条含 3 条信息记录）、WPF 57 断言、原型 5/5。真实资源拒绝已观察；没有因此证明可见分屏或实际双 Agent 工作负载通过。

## 8. 多轮 spec 补齐与边界修正（2026-11-10）

目标仍为完整 SPEC/UI/CONTRACTS/ACCEPTANCE，不将下表子场景提升为 51 项完整验收。以下为当前源码和实际执行记录；新增测试、测试输入与 `INTEGRATION_TESTS` 钩子依用户要求保持待评审。

| 范围 | 当前实现与证据 |
| --- | --- |
| 本机 CLI | Qoder 从通用 `qoder` 分发脚本改为 `qodercli`；真实 `Commands.Resolve` 验证 pi/codex/omp/kimi/qoder 的 exe/入口存在，不执行任意版本命令。带引号 PATH 正确解析，候选大小写去重 |
| 参数与手动路径 | 参数数组直接读写，新增/移除/排序；空项、单项换行、中文空格和尾部反斜杠实机打开→应用保持一致。旧 Join/Split 的实际 RED 与修后 GREEN 已留证。手动路径始终为可编辑下拉，合并当前配置与发现候选 |
| 主界面 | 已找到/未找到的目录级发现状态、名称搜索、最近选择排序；未勾选项不参与启动阻塞检查。自定义名称进入预览与计划；Terminal 标题和 Worker 路径的分号按 WT 解析规则转义 |
| 组合 | 通用组合继承新项目但保留目录策略/参数/勾选；需明确目录的项以 `RequiresDirectory` 阻止静默继承。命名/管理取消是事务；用户名称 `none`/`last` 与内部动作使用不同类型，回归 RED→GREEN |
| 可访问性与视觉 | 100/125/150% 应用文字/控件大小，窄屏操作换行；主题资源、输入边框/焦点和高对比度映射。`artifacts/ui/native-text-125.png`、`native-text-150.png` 为 120 DPI 下 WPF 原生内部渲染，不是 OS 全 DPI/读屏验收 |
| 目录操作 | 单项拖入只改对应目录，混入文件整次拒绝，输入/草稿改版废弃迟到结果；菜单重排之外提供拖动，明确继承按钮及复制分工。工作目录位置可设置，只在实际自动创建时验证；源仓库内目标拒绝，显式默认物理根复用原准备记录 |
| 诊断与管理 | 诊断先预览、默认隐藏路径，始终省略对话/参数/环境；最近项目删除只删记录，组合可重命名/移除。UIA 已验证取消/应用不丢当前草稿 |
| Terminal | 当前用户注册包通过 Win32 API 查询，不运行 PowerShell；本机发现执行别名及 `WindowsTerminal_1.24.12741.0_x64__8wekyb3d8bbwe/WindowsTerminal.exe` |
| 创建资源边界 | `begin` 对全部已预约预算再次使用完整策略；提交余量/物理余量不足、NaN CPU、过期样本由旧 Creating 改为拒绝并保留 Pending，四类隔离 RED→GREEN；不是实际压力负载验收 |
| 恢复与重试 | 同步 ResumeThread Win32 失败仅回收可证明未恢复的自有进程（故障注入 RED→GREEN）。崩溃不确定窗口仍保留 Unknown。仅 FailedBeforeStart/Revoked 可单项重试，RetryOf 校验不可变原计划且一个旧记录只能派生一次新尝试 |

当前完整仓库命令 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1`（未跳过阶段）执行成功：**17 单元、72 集成、66 WPF、5 原型、5 Edge 浏览器检查**。集成仍使用既定隔离/fake-sample 协议环境，不能充当实际资源压力结论。当前 Pi 版本输出 **1.5.0**。JSON：`artifacts/integration-results.json`、`artifacts/ui/ui-checks.json`、`artifacts/prototype-browser-results.json`。

60 秒空闲子场景（30 个样本）：GUI Private Bytes 峰值 **99.453125 MiB**，整机口径 CPU 平均 **0.155127%**；原始数据 `artifacts/spec-smoke/AgentLauncher-tests-performance-a0b1b5ef59cd40c4b2d53860e786afa7/idle.json`。这是当前机器测试构建的子场景，非自包含包完整 P02，也非双 Agent/2 小时测试。

最新实际 UIA 旅程 **17 项通过**：发现/手动路径、参数取消与往返、150% 深色、诊断路径 opt-in、通过原生“另存为”导出且内容等于脱敏预览、最近项目取消/应用、组合重命名、通用组合跨项目恢复以及 `none` 名称恢复。证据 `artifacts/spec-smoke/AgentLauncher-tests-journey-48324892918641f19229a4f9d3e28aeb/journey-result.json`；全程 Agent 槽禁用。外部驱动读取配置改用生产 `Store.Read`，避免自建不完整共享/锁协议。

普通预览入口 `artifacts/preview/AgentLauncher.App.exe`；自包含候选入口 `artifacts/spec-candidate/AgentLauncher.App.exe`，内含 .NETCore/WindowsDesktop 8.0.25。`artifacts/spec-candidate-evidence.json` 记录四个自有程序集 SHA-256、测试标记未发现、Worker 无参数用法 smoke（预期 exit 2）。未在用户真实配置下试跑候选 GUI，不将此称为干净环境分发验收。

独立源码审阅来自 `ReviewUiIteration` / `ReviewRuntimeIteration` 两个只读代理，不是人类可用性验收；有效问题已加入修正/回归：模板名称冲突、settings 重入、drop 覆盖待提交输入、disabled 探测阻塞、workspace 缩放、Terminal 分号与 worktree 根三条边界。

**测试变更待评审**：单元新增 Qoder/PATH/通用组合三项；集成新增创建再准入、ResumeRejected、单项重试、worktree 根四类、Terminal 标题共八项；UI 新增参数/设置应用/脱敏/缩放/目录事务/模板名称行为。删除了固定控件数量/列号/“无发现必须 TextBox”等实现细节断言，改为实际输入输出和保留数据行为；没有改测试运行脚本、资源阈值、跳过策略或既有协议断言。新增 ResumeRejected 仅编入测试构建。

`task_verify run`：**unverified / project-or-git-baseline-unavailable，checks=[]**。手动仓库门禁成功不替代 operator-configured 验证；这些测试改动需用户评审并在后续回合建立新基线。

**完整目标未关闭**：F01 的真实 Pi 中文流式/交互、F03 所有布局实机可见性、V05/V06/V07 系统 DPI/高对比度/Narrator 全矩阵、独立可用性、P01/P03/P05～P09（包括真实双 Agent、磁盘压力、2 小时）与签名/.NET10 仍缺完整证据。当前桌面锁屏；外部 pywinauto 截图为锁屏，已弃用为视觉证据。未关闭用户应用、未绕过准入、未向真实 Agent 发送任务。

## 9. 验证命令

```powershell
# 构建不含测试后门的正常技术预览
powershell -NoProfile -File scripts/build.ps1

# 纯模型 + 受控原生验证
 dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release

# 隔离协议、工作目录、Terminal、WPF 和浏览器回归
powershell -NoProfile -File scripts/test.ps1
```

测试只操作新建临时目录；没有修改用户项目、关闭用户应用或发送任何消息。测试夹具及未确认记录保留以便复核，不自动删除。

## 10. 合并后评审修复（2026-11-10）

本轮获准修复 CR01～CR12，仍保持技术预览和完整 spec 未验收状态。测试、runner、阈值在回合开始时固定；新增回归单列待评审，手动执行结果不代表 operator 验证通过。

### CR01：持久化执行快照的字段存在性

- 预期：缺失执行字段应拒绝读取，不修改损坏文件或有效备份；显式空值/default 与新建配置仍可使用。实际旧行为：单独移除 `Id`、`Enabled`、`AgentId`、`Directory`、`Profiles`、`Executable`、`Arguments`、`Project`、`Revision` 都被接受。
- 首个分歧在 JSON 反序列化：默认初始化值掩盖缺失字段；不是 `Restore` 才开始丢数据。保留新建对象的默认值，仅用既有 `[JsonRequired]` 约束持久化边界，不引入新 schema 或隐式迁移。
- 新增 `ConfigurationReviewChecks`，覆盖当前草稿、LastEdited、命名组合，以及完整默认/portable 快照往返。相同测试对旧程序集返回 exit 1（`Draft.Id silently accepted`），对修正后 Core 返回 exit 0、两项通过；原文件与 `.bak` 不变断言保留。
- 证据：`artifacts/review-config-red.txt`、`artifacts/review-config-green.txt`；隔离配置根 `artifacts/AgentLauncher-tests-review-402438ff50a74b8bb83d552deee4f36f`。旧三项目录冲突也已运行确认，原始结果为 `artifacts/AgentLauncher-tests-review-0b5cd4d73fb649f9a082192c54659dfa/boundary-result.json`；该问题的修后证据见下表。

根因类别：跨层合同和隐式假设。防复发边界是逐字段的真实反序列化回归，而非仅检查空对象/顶层 schema。新增测试文件和 `Program.cs` 调用均待评审；未改 runner、阈值或既有断言。

### CR02～CR12：恢复、协议与交互边界

| 评审项 | 修复边界 | 当前直接证据 |
| --- | --- | --- |
| CR02 准备目录替换 | 重用前比较保存的物理 key/cwd，核对 intent、common Git dir 及 worktree 登记；不同目录不覆盖旧身份 | 同一永久回归在旧版接受替换而失败，修后拒绝且原记录/备份不变；正常复用、命令刷新、Intent 崩溃恢复均通过 |
| CR03 停止请求 | 停止经监控器持久撤销冻结批次 Pending；提交前后和确认等待检查取消，保留已领取项；只锁配置控件，不锁会话撤销入口 | 同源 UI 探针旧版 Pending 不变、修后 Revoked，迟到 begin 被拒绝；WPF 新回归覆盖忙时可访问性 |
| CR04 撤销落盘失败 | State 与 Detail 在同一个异常分支恢复原值，失败不宣称已撤销 | 文件共享冲突旧版留下成功详情，修后原状态/详情保留；后续 begin 仍可正常领取 |
| CR05 活动记录 | 所有活动/未确认项可见，仅历史取最近 12 条 | 旧 Pending 被 12/13 条新历史挤出的同源探针旧失败/新通过 |
| CR06 超长状态 | 同一管道事务发送有界多帧，完整收到最终帧才返回；各帧上限仍为 1 MiB，单条不可表示的计划在持久化前拒绝 | >1 MiB 整体状态保留全部活动 ID/参数；旧版断流、新版通过；截断、代次混合均拒绝 |
| CR07 前端准入 | 有效工作目录参与异步检查；未应用文本/过期检查不能放行；`AdmissionBlockedReason` 独立于 `Ok` 核对健康状态 | 不存在目录、压力恢复锁存加健康样本均旧失败/新通过；恢复后清除原因，完整 WPF 原有空项目提示不退化 |
| CR08 保存恢复 | 保存失败保留最新草稿与待写快照，提供「重试保存」，确认成功前不启用启动 | 文件锁释放后明确重试保存最新版本，清除失败；只读配置仍拒绝写入 |
| CR09 撤销移除 | 撤销记录独立保存，任何会话重绘重建入口；支持多条与容量不足后的重试 | 旧版会话变化使入口消失，修后保留；空会话签名使用 null 无效值避免缓存误命中 |
| CR10 空闲退出 | 无活动义务/Job/核对阻塞且无轮询时约 4 秒退休；预约只在发送任何请求字节之前连接失败时启动监控器，不重放未知请求 | 同源运行时回归旧版 5 秒仍活跃，修后 4.293848 秒退出；轮询、Pending 保活通过 |
| CR11 目录说明 | 单项明确使用原项目/指定目录，不承诺副本；多项按同一工作区分组策略说明 | `artifacts/ui/native-dark-1040.png` 原生渲染显示单项原目录说明；现有单项准备回归保持原 cwd |
| CR12 全批次冲突 | Git/准备记录写入前比较最终目标与全批直接目录；不误拒绝同一存放根下的不重叠兄弟目录 | 同一永久回归旧版先成功准备而失败；修后无新增记录/分支/工作树；兄弟目录通过 |

协议实现说明：`Response.More` 仅表示同一请求、同一管道内的连续帧，调用者收到完整 `Response` 后才更新状态。客户端校验所有帧的健康状态/消息/采样/epoch/准入原因一致，最多 64 帧、16 MiB；这些是传输防护，不增加原 1 MiB 单帧限制，也不删除活动计划。所有二进制需整目录一起更新。

### 运行证据与失败修正

- 最终原命令 `powershell -NoProfile -File scripts/test.ps1`（bg_74，exit 0，156.61 秒）：**19 模型/原生、83 集成、94 WPF、5 原型、5 浏览器检查通过**。未使用 SkipUi/SkipBrowser；runner、30 秒 WPF 超时、资源预算及既有断言未变。证据：`artifacts/integration-results.json`、`artifacts/ui/ui-checks.json`、`artifacts/prototype-browser-results.json`；完整集成根 `D:/temp/User/AgentLauncher-tests-integration-4e20931d33e548048759d5f828bc69cb`。
- 同源六项 UI 边界探针：旧版根 `artifacts/AgentLauncher-tests-review-a050a9c8c4284293b13918f4915c31f9/ui-boundary-result.json` 全部预期失败；修后根 `artifacts/AgentLauncher-tests-review-8ba6862288fc412ba8d972da0a7ae895/ui-boundary-result.json` 全通过。运行时三项同源对照：旧根 `D:/temp/User/AgentLauncher-tests-review-f31b1580c9544d67a17b9005ae265d76`，新根 `D:/temp/User/AgentLauncher-tests-review-eaa45a580a1f410e8dc869a00a4544f4`，均有 `runtime-boundary-result.json`。
- WorkspaceReviewChecks 对旧程序集：`D:/temp/User/AgentLauncher-tests-review-44ac0a5b3b014780a183639e6e813b9f/workspace-boundary-result.json`，目录替换/全批次冲突两项预期失败，另三项通过；修后五项均在完整集成通过。首次更深 artifacts 根触发 Git `$GIT_DIR too big`，该环境失败不计 RED，改用正常测试根深度后取得上述有效结果。
- 第一轮 WPF 超时并非门禁太短：只读采样发现 `commandsRunning=true`、任务已完成、Dispatcher 心跳停止。等待者循环饿死完成者；改为单消费者返回共享任务，并由 worker finally 清运行标志。后续依次修正空项目提示优先级、空会话撤销缓存失效值。新 readiness 夹具补上「等待保存完成」前置条件；没有削弱 launch-ready 断言。最终原 30 秒内 WPF 94 项通过。
- 本轮外部 UIA 在当前桌面不可访问，调试器无 netcoredbg；未继续重复不可用操作。原生图是 120 DPI 的 WPF 内部渲染/局部布局证据，不是 OS DPI、Narrator 或人工完整验收。

### 待评审测试和未验证范围

- 新增 `ConfigurationReviewChecks.cs`（两项、九字段 × 三种快照）、`WorkspaceReviewChecks.cs`（五项）、`RuntimeReviewChecks.cs`（六项）、`UiReviewChecks.cs`（28 项），并在现有 Program/Integration/UiChecks 中各接入调用。未添加运行时故障注入 hook；沿用隔离 root/fake sample。新夹具清理只针对自己的进程/目录，Monitor 清理核对测试根、PID 和启动时间。
- 根因防复发：持久化字段必须区分「缺失」与显式默认；路径/提交/分支名不能代替物理仓库身份；恢复操作必须同时验证状态、持久化与用户可达入口；多消费者等待共享任务不能用尚未复位的标志自旋。
- `task_verify run` 当前仍为 **unverified / project-or-git-baseline-unavailable，checks=[]**；没有 operator-configured passed/failed 项。上述手动通过结果与新测试待评审状态分别报告。请评审新增回归和调用接入，在后续用户回合建立新基线。
- 完整 spec 目标未关闭：第 6、8 节中真实 Pi 交互、所有分屏布局、系统 DPI/Narrator、高对比度全矩阵、真实压力/长时稳定性和正式签名/.NET10 分发仍未完整验收。本轮没有补做命令超时、结构化错误码、终端定位等额外功能，不将 12 项修复当作全部 spec 完成。

