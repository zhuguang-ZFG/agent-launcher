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
- 后续评审修正 `UiChecks` 的 null 模板夹具：从完整 `Configuration` 序列化生成 JSON，仅将 `Templates["broken"]` 设为 null，避免 CR01 的必需字段校验先行失败而遮蔽模板保护。原 fail-closed 断言保持不变；WPF 窄检查在原 30 秒内 exit 0、94 项通过，证据 `D:/temp/User/AgentLauncher-tests-review-764354e62f3c40daac21ec0df34f7e5d/report/ui-checks.json`。本次仅修改夹具和记录，仍待评审；未重跑其他阶段，不把上一轮完整门禁当作本次执行。
- 根因防复发：持久化字段必须区分「缺失」与显式默认；路径/提交/分支名不能代替物理仓库身份；恢复操作必须同时验证状态、持久化与用户可达入口；多消费者等待共享任务不能用尚未复位的标志自旋。
- `task_verify run` 当前仍为 **unverified / project-or-git-baseline-unavailable，checks=[]**；没有 operator-configured passed/failed 项。上述手动通过结果与新测试待评审状态分别报告。请评审新增回归和调用接入，在后续用户回合建立新基线。
- 完整 spec 目标未关闭：第 6、8 节中真实 Pi 交互、所有分屏布局、系统 DPI/Narrator、高对比度全矩阵、真实压力/长时稳定性和正式签名/.NET10 分发仍未完整验收。本轮没有补做命令超时、结构化错误码、终端定位等额外功能，不将 12 项修复当作全部 spec 完成。


## 11. 评审后续修正（2026-11-10）

承接第 10 节，处理评审提出的三项残余：CR12 只覆盖同批、CR06 账本无上界、CR01 策略布尔未披露。runner、阈值和既有断言未修改；以下结果均为手动执行，operator 基线仍未建立。

### CR12：活动会话工作目录纳入预检

- 问题：第 10 节的预检只比较同一批次内的目标。对已运行会话占用的工作目录，仍由 monitor 在 reserve 阶段拒绝，而那时 worktree、分支和准备记录已经写完——正是 CR12 要消除的顺序。
- 修复：`WorkspacePreparer.PrepareAsync` 增加可选 `activeWorkspaces`；App 从最近一次 status 取所有非终态 agent 的 `Plan.Workspace` 传入。`EnsureBatchTargetsDistinct` 在写任何 Git/准备记录之前，把每个独立目标与活动工作区做路径包含比较（与 monitor 的 `Overlaps` 同一判据）。直接目录计划不写 worktree，仍由 reserve 拒绝，不重复检查。
- 回归：`CR12 active session workspace also blocks the batch before Git writes`——活动工作区等于存放根时，无新增准备/工作目录记录、worktree 列表与 launcher 分支不变、目标目录无写入；活动工作区不相交时同一 draft 仍能准备两项。
- 残余：这是准备开始时的快照比较；准备期间新出现的会话仍由 reserve 兜底（reserve 在任何进程创建之前）。

### CR06：账本上界

- 问题：`EnsureEntryRepresentable` 允许单条接近 1 MiB，账本终态记录从不裁剪，而 `Store.Read` 在 8 MiB 处拒绝整份文件；一旦触发，`ledgerBroken` 会永久拒绝启动，只能手工修 `control.json`。这与 CONTRACTS C4「状态事件有界落盘」相悖。
- 修复：`Monitor.Save()` 先 `PruneLedger()`，按最旧优先裁剪**终态**记录直到序列化后 ≤ 6 MiB；活动记录永不裁剪。reserve 最多 4 个 Agent + 1 个 Git 计划、每条 ≤ 1 MiB，因此活动部分本身留在预算内，8 MiB 读取上限留出 2 MiB 余量。
- 回归：`CR06 ledger stays readable while active reservations survive pruning`——120 条终态（每条 60 000 字符参数）+ 1 条 Pending，序列化后超过 6 MiB；`Save()` 后发布文件 ≈5.98 MiB（本次运行 6 271 575 字节，随夹具路径长度略变），`Store.Read<Ledger>` 可读回，Pending 预约参数逐字保留，最新终态记录未被裁掉，记录数确实减少。
- 残余：已经超过 8 MiB 或已损坏的历史账本无法自动恢复（读取阶段就失败），仍需人工处理；本轮只保证今后不再增长到该边界。

### CR01：两枚策略布尔仍可静默缺省（已披露，未改 schema）

- `Slot.RequiresDirectory` 与 `Draft.IsPortableTemplate` 使用 `WhenWritingDefault`，缺失时静默取 false。它们不能简单加 `[JsonRequired]`：旧配置文件本来就不写 false 值，加了等于破坏性 schema 变更。
- 影响边界：手工删字段的 portable 组合会从「必须重新指定目录」退化为「继承新项目」，通用组合会退化为「绑定组合」。这与 CR01「不能靠对象初始化器改变执行语义」属于同一类残余。
- 处理：本轮只披露、不扩大改动。要关闭需升 schema 并做显式迁移（把缺失值当作迁移事件，而不是默认值）。

### 当时未处理（本轮处理结果见第 12 节）

- `EnsureMonitorAsync` / `SendCoreAsync(ensureMonitor)` 仍把 400 ms 连接超时一律当作「监控器不存在」；单实例管道在忙时会空拉一个立即退出的 Worker。
- 取消后的撤销在 `SetBusy(false)` 之前完成，每项 8 秒超时，最坏情况配置区锁定约 32 秒。
- 撤销移除记录不随模板恢复/项目切换失效，可能把旧项目的 slot 插回新配置。
- `CheckCommandsAsync` 的正确性仍依赖「最后一个循环判断到 finally 之间没有 await」这一约定，只有实现者知道。
- 单条活动记录不可表示时整个 status 失败，UI 侧没有撤销入口。

### 验证

- `dotnet build AgentLauncher.sln -c Release`：0 警告 / 0 错误。
- `powershell -NoProfile -File scripts/test.ps1`：**19 模型/原生、85 集成、94 WPF、5 原型、5 浏览器检查通过**，未使用 SkipUi/SkipBrowser。证据：`artifacts/integration-results.json`（88 条证据，85 项带断言）、`artifacts/ui/ui-checks.json`、`artifacts/prototype-browser-results.json`、`artifacts/review-followup-20261110.json`；集成根 `D:/Temp/User/AgentLauncher-tests-integration-9daf63b4bf274645bbb5a54e6d924c8e`。
- `artifacts/preview` 已按本轮源码重新发布，哈希见 `review-followup-20261110.json`。
- 新增两项回归仍待评审；本轮只覆盖上述条目，不改变第 6、8 节的未验收范围，也不把手动门禁称为 spec 验收。

## 12. 评审清理与取消交互补齐（2026-11-10）

继续处理第 11 节列出的五项后续工作，同时收拢重复的目标目录核对、完善错误诊断并同步 CONTRACTS。所有改动留在当前工作树，未提交或发布到远端。

### 行为与边界

| 条目 | 实现与回归 |
| --- | --- |
| CR10 忙碌管道 | 两条监控启动路径在探测超时后，先读取最多 32 字节的 PID 提示并核对存活 Worker 路径；确认已有 Worker 时继续等待连接。真实单实例管道被占住 1.2 秒时，并发 status/reserve 都成功，测试日志证明未尝试额外启动。PID 提示不参与授权，实际单实例仍由 mutex 保证；启动/退出竞态不保证零冗余进程 |
| CR06 异常活动记录 | 超界单条只从本次 status 省略，消息使用数量与最多四个 ID，避免超长名称再次撑爆响应。返回 `Ok=false` 和准入原因，服务端 reserve/begin 同样拒绝新启动；其他完整记录仍能回传和撤销。新计划的单帧检测预留 4 KiB 状态头余量，1 MiB 帧上限不变。回归用超长中文名称/参数验证真实编码、解码、准入拒绝、同伴撤销和原账本不被改写 |
| CR03 取消后的界面 | 启动与单项重试统一通过 `FinishLaunchAsync`：停止后续提交后，先解锁配置，再等待撤销；不在 try 分支重复等待同一轮取消撤销。reserve 未确认后的补偿也通过通知先解锁。token/finalization 门禁独立于 Pending 列表，清理过程中即使刷新清空 ID、修改草稿，也不能发起下一次启动或重试；测试用真实占用管道验证等待期间可编辑、清理后授权 Revoked |
| CR09 撤销移除作用域 | 手动/选择/拖入项目均走 `SelectProject`，补上最近项目入口；实际项目变化、命名组合和 LastEdited 恢复清除旧 undo 并刷新入口。同项目选择及无关会话重绘保留 undo。新增五项 UI 断言 |
| CR07 检查消费者 | 在代码与 CONTRACTS 中记录单 Dispatcher 消费者、共享任务以及末次循环判断至 finally 无 await 的不变量；一次性测试暂停让草稿确定在检查过程中修改，验证同一等待任务最终消费最新目录，完成后还能启动新检查 |
| 可维护性与诊断 | 两处目录采纳判断共用 `EnsureTargetAdoptable`，原有目录替换/恢复回归继续约束行为；null 模板断言要求实际结构错误；反射夹具缺成员时抛带字段/方法名的异常，避免空引用遮蔽失败原因 |

已省略的超界记录自身仍没有 UI 撤销入口，需人工核对 `control.json` 与备份；本轮解决的是异常单条拖垮其他记录的可见性/撤销能力。不会把异常记录裁短后当作完整执行计划，也不会释放其预算/目录占用。大于 8 MiB 的旧账本仍不能自动读取/修复；CR01 两枚布尔的 schema 兼容问题仍按第 11 节披露。

### 验证与证据

- 正常 Release 与 `INTEGRATION_TESTS` 构建均通过，0 警告 / 0 错误。原命令 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1` **exit 0，167.35 秒**：**19 模型/原生、87 集成、108 WPF、5 原型、5 浏览器检查通过**；未跳过阶段，新增 2 项集成、14 项 WPF 断言。集成报告含 90 条证据、87 项断言、0 失败。
- 证据：`artifacts/review-cleanup-20261110.json`、`artifacts/integration-results.json`、`artifacts/ui/ui-checks.json`、`artifacts/prototype-browser-results.json`；完整日志 `D:/Temp/review-cleanup-gate.log`，集成根 `D:/Temp/User/AgentLauncher-tests-integration-663ae20fbc084f4a9864cf5ad24df521`。本轮结果覆盖旧的通用报告，§11 的 `review-followup-20261110.json` 保留为上一轮记录。
- `scripts/build.ps1` 重新发布 `artifacts/preview`；四个自有程序集哈希及测试标记扫描结果存入本轮证据。Worker 无参数 smoke 返回预期 exit 2。没有用真实用户配置打开 GUI；WPF 图像仍只是 120 DPI 内部渲染，不是完整系统 DPI/读屏验收。
- 主动 LSP 探测 8 个修改文件未取得可确认结果（5 unavailable、3 inconclusive），不能以零诊断宣称 LSP 通过；静态/编译依据是上述成功构建和原仓库测试。
- 新增两个测试专用观测点：`AGENT_LAUNCHER_MONITOR_SPAWN_LOG` 记录启动尝试，`commandCheckPause` 确定性暂停一次目录检查；两者仅在 `INTEGRATION_TESTS` 编译。不修改 runner、原 30 秒 WPF 超时、资源预算或既有断言来获得通过。
- 新增回归仍待评审；本轮没有可调用的 operator `task_verify` 工具，沿用上一轮 unverified 状态，手动门禁不替代 operator 基线或完整 spec 验收。会话 UI 拆分到独立 partial 文件属于后续组织性重构，本轮未做。

## 13. 启动门禁与关闭收尾五项修复（2026-11-10）

- `MainWindow.xaml.cs`：状态响应只有 `Ok=true` 且明确报告同一 ID 已离开 Pending，才释放本地提交记录；缺失 ID、失败响应不会丢弃撤销所有权。正常启动和单项重试共享可等待的 `launchTask`，显式停止保留 `cancelTask`；被拒绝的重复入口不覆盖原任务。关闭时禁止新提交、防重复关闭，等待启动补偿、显式撤销、命令检查与配置保存；不终止已运行 Agent。
  **后续修正：本段的“必须明确看到终态”策略不再适用，现由第 15 节的提交代次与完整活动快照协调取代。** 终态可合法省略/裁剪，不能依赖永久可见。
- 项目路径 Enter 与失焦共用提交和目录校验，保留焦点，非法目录阻止启动；Ctrl+Enter 快捷键保持原义。收尾提示分别表达取消、普通启动收尾、预约失败补偿及关闭等待，不再把禁用按钮说成可以启动。CLI 查找完成按当前 busy 状态恢复按钮。
- 根因：把“状态快照缺少 ID”当成完成证据；把“配置已解锁”当成操作已结束。准备目录及内部准备记录可复用，但 `WorkspacePreparer.PrepareAsync` 返回前会生成新的 attempt/batch ID；reserve 的同 ID 幂等和单次 begin 授权只约束同一尝试，不能据此排除重复调用 Prepare 后的重复批次风险。本轮用提交代次保护入口，未执行真实重复 Agent 场景。
- 一次性隔离 WPF 探针使用真实 MainWindow、路径键盘事件与真实 IPC 客户端，由受控命名管道服务器固定迟到响应和撤销时序；不启动 Agent，不提交 Terminal。固定探针在修改前 **4 通过 / 12 失败**，修改后 **16 通过 / 0 失败**。证据：`artifacts/five-fixes-red/results.json`、`artifacts/five-fixes-green/results.json`；当前两张 WPF 渲染图已打开检查。首次探针有一个串联状态夹具问题，修正为独立条件并加强关闭事件时序断言后才记录上述红/绿结果，未弱化断言。
- 原 WPF `--ui-check`：**exit 0，108 项通过，11.31 秒**，未修改原 30 秒限制、既有测试、runner 或资源阈值；报告 `artifacts/five-fixes-ui/ui-checks.json`，120 DPI。测试专用 App/Worker 发布及正常 `scripts/build.ps1` 发布均成功；`artifacts/preview` 已更新。
- `task_verify run`：**unverified / project-or-git-baseline-unavailable，checks=[]**；operator passed/failed 均无执行项，手动通过不替代基线。临时探针源码归档 `artifacts/five-fixes-probe.zip` 供评审，不接入固定测试集；新增验证输入本身仍待评审。
- 本轮未跑完整原生/集成/浏览器门禁，未做系统 DPI/Narrator 全矩阵、真实 Agent 压力或两小时稳定性验收。没有改布局、XAML 或菜单文案；额外的“更多”和重试按钮交互建议不属于本次批准的五项。

## 14. 实际分屏外观、Omp 发现与低开销验证（2026-11-10）

### 实现与不变量

- 新增 Windows 层 `TerminalAppearance`，由原 `Commands.SubmitTerminal` 入口设置当前窗格的内置 `One Half Dark` 配色、蓝色标签、稳定的「序号 / Agent / 项目」标题和正确工作目录。两项显式等分，多项窗口最大化，完成布局后焦点回第一项；原 1/2/3/4 项布局逻辑、结构化参数、批次 ID、claim、Job 与资源预算不变。
- 不写用户 `settings.json`，不安装 Terminal profile fragment；官方 Terminal 的 fragment 目录不热加载，不能用新 profile 已立即可用作假设。系统高对比度或无法确认配色模式时，不覆盖 Terminal 配色。窗口标题过滤控制字符，分号保持 Terminal 转义；真实目录仍按原值传给受保护进程。
- Runner 在创建 Agent 前只打印一次 UTF-8 加载头部，不输出启动参数或密钥，不运行额外 shell，不持续重绘/抢输入，不对 Agent 自身 TUI 做包裹或改写。Agent 自身配色、登录、初始化仍由对应 CLI 管理；加载提示不声称 Agent 已就绪。
- `Agents.All` 原本已有 Omp，本轮没有重复添加。验证实际发现 `C:/Users/zhugu/.bun/bin/omp.exe`，`omp --version` 为 **omp/18.18.6**；默认空程序路径通过原 `Commands.Resolve("omp", new())` 自动得到该 exe。配置窗口现在列出已找到 CLI 名称，预览明确说明“启动时自动加载”；不自动运行未勾选项、下载软件或登录账户。

### 实际界面与验证

- Windows Terminal **1.24.12741.0** 注册包可用，PATH 中没有 wt；既有注册包发现路径成功启动。真实左右/上下双分屏均截图检查，中文、空格、分号与 `&` 工作目录逐项一致；标题与初始焦点正确。图中明确标记为**受控轻量程序，不是真实 Agent**，不拿它冒充 Omp 对话验收。
- 左右分屏场景 **15 项通过**，上下分屏 **13 项通过**：实际 Worker/IPC/Job 受保护自动加载、目录传递、完成回执、预约终结与空闲 Monitor 退出；高对比度分支不注入配色参数，控制字符不能进入窗格标题。报告和截图：`artifacts/terminal-vertical-verified/`、`artifacts/terminal-horizontal/`。
- 首次终端夹具错误地在 Runner `Started` 后立即要求子程序初始化文件存在；现场截图和随后写出的两份文件证明此时程序已启动。修正夹具为独立等待子程序初始化报告（总等待仍限 15 秒）后重跑，既有断言不弱化；原失败保留在 `artifacts/terminal-vertical/`。该证据同时重申“进程 Started 不等于应用初始化已完成”。
- 最终源码原 WPF 窄检查 **108 项通过、exit 0、11.59 秒**，仍使用原 30 秒限制；报告 `artifacts/terminal-ui-checks/ui-checks.json`。正常 `scripts/build.ps1` 已重新发布 App/Worker 到 `artifacts/preview`；没有编辑既有测试、runner 或策略。临时 Terminal 探针已移除，固定源码存于 `artifacts/terminal-probe.zip`，作为待评审验证输入，不并入固定测试集。

### 资源测量（各 60 秒 / 30 样本，16 逻辑处理器）

| 场景 | 峰值 Private Bytes | 整机口径平均 CPU | 判读 |
| --- | ---: | ---: | --- |
| Monitor + 两 Runner（不含 Terminal、受控子程序或真实 Agent） | 28.85 MiB | 0.0498% | 本次受控 P03 子场景低于 64 MiB / 0.2% |
| GUI 静置，不做 UIA 操作 | 99.81 MiB | 0.1632% | 本次 P02 子场景低于 100 MiB / 0.5%，余量较小 |
| GUI 含 UIA 查询、聚焦与截图 | 104.52 MiB | 0.2182% | 超过 100 MiB；保留交互峰值，不覆盖或冒充纯空闲结果 |

原始数据：`artifacts/terminal-vertical-verified/result.json`、`artifacts/terminal-gui-quiet/idle.json`、`artifacts/terminal-gui-idle/idle.json`。Working Set 单独保存，不与 Private Bytes 混算。Terminal 共享进程单次观测 Private Bytes 68.05 MiB / Working Set 103.00 MiB，非本窗格独占值，不计入上述协调层数值。美化没有增加常驻进程、WebView 或刷新动画；这些样本不证明真实 Agent/工具峰值、长期稳定性或相对旧版本的性能改善。

### 未验证范围

- 本机可用物理内存采样约 2.47 GiB；单 Agent 仍需系统保留 `max(3 GiB,total/5)` 加 2 GiB 预算（本机约 5.04 GiB 可用）。真实 Omp 配置界面正确显示资源阻塞，启动按钮禁用，证据 `artifacts/terminal-gui-idle/surface.json` 和 `omp-auto-discovered.png`。未用测试 fake sample 绕过真实 Agent 门禁，**真实 Omp 交互、登录及任务执行尚未验证**；模拟采样仅用于隔离的轻量程序夹具。
- 本轮实际 Terminal 画面覆盖两种双分屏；未新增三/四 Agent 实机运行、系统高对比度全矩阵、真实压力或两小时稳定性证据。前五项修复的独立红/绿证据保留在第 13 节。
- operator `task_verify` 缺少项目/基线，手动验证与新增探针评审状态分开报告；不宣称完整 spec 验收完成。

参考：[Terminal 命令行参数源码](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalApp/AppCommandlineArgs.cpp)、[fragment 加载实现](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalSettingsModel/CascadiaSettingsSerialization.cpp)。

## 15. 终态省略与提交代次协调（2026-11-10）

- 问题：第 13 节仅接受正面终态证据，忽略 `Worker.Status` 的终态 768 KiB 总限额及 `PruneLedger` 裁剪。合法单条计划可超过历史限额但小于 IPC 帧上限；如果首次查询前已结束，UI 永远看不到该 ID，导致 `submittedAttempts` 永久不清空。
- 修正：成功预约后，正常启动与单项重试均递增本地 `submissionGeneration`；`RefreshAsync` 发出请求前捕获代次，返回或报错时若代次已变化，整份旧结果丢弃，不覆盖当前状态/撤销所有权。同代次的 `Ok=true`、最终完整分页且非 null 的 Entries 是完整活动快照：仅保留明确仍为 Pending 的提交 ID，允许通过缺失 ID 确认不再待领取；终态历史是否可见不再影响放行。失败响应、缺失 Entries 不释放门禁；不增加自动重试或改变 Worker 协议/历史上限。
- 独立 WPF 探针直接调用真实 `Worker.Status()`/`PruneLedger()` 生成 800 KiB 合法大条目、历史省略、账本裁剪及混合 Pending 场景。迟到响应场景实际走正常启动与单项重试的 reserve 成功路径，随后移除夹具 Terminal 文件阻止创建真实终端/Agent，再检查旧响应丢弃、取消请求和后续快照放行。夹具初稿在 reserve 响应前取消及串联残留 ID，均纠正为独立边界后固定断言，不改产品或弱化断言去获得红态。
- 固定探针：旧实现 **14 通过 / 8 失败**（`artifacts/status-generation-red-isolated/result.json`），修复后 **22 通过 / 0 失败**（`artifacts/status-generation-green/result.json`），exit 0、3.68 秒。原 WPF 检查 **108 项通过、exit 0、11.46 秒**，仍为原 30 秒上限，报告 `artifacts/status-generation-ui/ui-checks.json`。绿色界面截图已检查，省略终态后启动按钮重新开放。
- 正常 App/Worker 已通过 `scripts/build.ps1` 重新发布到 `artifacts/preview`。没有改既有测试、runner、阈值、Terminal 外观或用户配置；新一次性探针是待评审验证输入，不替代 operator 基线。
- 当前桌面已创建 `D:/Desktop/多 Agent 启动器.lnk`，读回验证目标为 `D:/Users/pj/agent-launcher/artifacts/preview/AgentLauncher.App.exe`、工作目录为完整 preview、参数为空。通过快捷方式实际打开生产 App，并确认界面发现 Pi/Codex/Claude Code/Omp/Kimi/Qoder；没有启动 Agent 或修改用户草稿。当前真实资源检查提示可用内存不足，启动按钮禁用是预期保护，不是门禁卡死。证据 `artifacts/desktop-ready/result.json`。
- 本轮 `task_verify run` 为 **unverified / project-or-git-baseline-unavailable，checks=[]**，没有 operator passed/failed 执行项。一次性新探针固定源码归档 `artifacts/status-generation-probe.zip` 供评审，临时项目已删除；未更改既有测试或 runner。其他完整 spec 未验收范围不因此关闭。

## 16. 最新 P0 多窗格、资源策略与节能收尾（2026-11-10）

### 实现边界

- 配置 schemaVersion 1→2，槽位上限 4→16；`PaneLayout` 支持 1～16 格、创建序映射和命令行 24000 字符门禁，Terminal 按 Agent 分配标签色，高对比度不注入配色。
- `LaunchItem` 增加稳定 `SourceSlotId/AgentId`；资源准入拒绝非默认 custom 缺省规划，保留旧活动账本兼容；waiver 由 monitor 读取当前配置完整有序启用槽绑定，配置变化、伪造槽、乱序槽或摘要变化即失效。
- Worker 空闲退出 15 秒，status 支持短帧/签名，回执 mtime+length 缓存；Started 回执现在把状态置为 `Started`（不代表任务完成），Job 成员归零且回执终态后释放 Job/回执缓存；根 Job CPU 上报实际值。
- UI 提供 1/2/4/8/16 预设、逐槽 Agent 混搭、四计数与资源设置；启动前 monitor 权威预检，软超只按预提交 `RequireWaiver` 经用户确认重试一次。自适应轮询活动 1 秒、有活动 2 秒、空闲/本机预览 30 秒，失败/Unchanged 保留既有条目。
- Git 准备和 Agent 均纳入授权/预算；Git 软超同样要求一次性确认。5/16 稀疏槽准备重放复用目录且生成新 attempt。

### 直接验证

- 旧代码独立探针已复现两类缺陷：custom 1 GiB + 新请求 PlanningBytes=null 被接受且仅按 768 MiB 计账；slot 5/16 重放报“准备记录损坏”。修正后对应边界探针均通过。
- `artifacts/p0-runtime-probe`：**PASS** Started 状态计数、回执缓存、Job 成员退出对账和终态释放。
- `artifacts/p0-admission-probe` / admission suite：**16 PASS / 0 failures**，包括 16/17 槽边界、伪造稳定槽、同 revision 配置编辑、Git 软超一次性授权。
- `artifacts/p0-e2e/result.json`：**7/7 PASS**（真实 UI；16 配置/启用 3、一次性确认、3 个受控进程、Terminal 三窗格标题、Started=3、完成后 Reserved=0、monitor 空闲退出、资源检查重启 monitor）。
- `powershell -NoProfile -File artifacts/p0-smoke/ui.ps1`：**UI passed: 138 / 0 failed**，16.14 秒完成（原 30 秒边界内）。
- Workspace 调试中一度出现 5/7/6/7：诊断输出证明测试根放在 probe 输出目录（外层 Git 仓库）内，`PhysicalDirectory.Workspace` 按既有合同把 direct sibling/destination 归并到外层 worktree 根；这是测试夹具位置错误。随后唯一剩余失败是 active 夹具自身不匹配：`active=Workspace(destination)` 却用 `WorkspacePreparer(elsewhere)`，两者为 disjoint sibling，不能期待拒绝。已按边界改为同一 `destination` preparer，并用不相关 active 做正控；临时诊断已移除。修正后 `artifacts/p0-workspace-probe`：**PASS workspace boundaries: 7/7**，47.93 秒，根目录 `D:\temp\User\AgentLauncher-tests-p0-workspace-8f03de7bf26a4796a01019c1f046710e`。
- 最终 `powershell -NoProfile -File scripts/test.ps1`：**exit 0，175.28 秒**；模型/原生 65/0，Integration failures 0，原型 5/0，Edge browser 5/0；WPF `--ui-check` 单独证据为 `artifacts/ui/ui-checks.json` / smoke 复跑 **138/0**。全量输出包含 CR12 三项 workspace 边界、ACC-S02 15 秒空闲、ACC-C01 根 CPU/配置核对、ACC-S03 回执缓存与 status 签名。证据根：`D:\temp\User\AgentLauncher-tests-be2e153ddb384937abbdd7bea91dd06b`、`D:\temp\User\AgentLauncher-tests-integration-e24f6df1b0934b1fb19d82f173fba87a`。
- 新增/修改测试仍待用户评审，不视为 operator 基线；评审包见 `artifacts/p0-test-review/README.md`（逐项理由、证据与评审要点）。
- 交付面收尾：桌面快捷方式 `D:\Desktop\多 Agent 启动器.lnk` 二进制解析目标为 `artifacts/preview/AgentLauncher.App.exe`（最终构建 2026-11-10T16:47Z，SHA-256 `4C0B3A87…`）；`artifacts/preview-smoke.ps1` 实际启动最终 preview，主窗口「多 Agent 启动器」出现且 responding，`CloseMainWindow` 后 exit 0，无残留进程。

### 测试变更（待评审）

- `RuntimeReviewChecks.ReceiptCacheAsync` 增加 Started 断言，并在进程终止后轮询至可观察 Job 成员归零；不是放宽终态断言。
- `AdmissionIntegration` 新增伪造/乱序槽、同 revision 配置编辑、Git 软超授权回归。
- `WorkspaceReviewChecks` 新增 5/16 稀疏槽重放回归。
- `UiChecks` 撤销容量 4→16，删除依赖旧 Preview 扁平子控件的断言，改用渲染几何/`PaneCell` 语义；`UiReviewChecks` CR09 容量夹具、Integration 低内存夹具、新增 `PaneLayoutChecks`/`UiMultipaneChecks` 均按本轮测试变更提交评审。
- 源码中的测试专用环境/发布路径只用于隔离测试，不进入正常 `artifacts/preview`。

## 17. 原生界面易用性与确认期间资源恢复（2026-11-11）

### 范围与实现

- 用户已同意第 16 节的测试评审包。本轮保留原有测试、runner、资源策略和启动合同；三名子代理分别处理主窗口布局、对话框和正确性审查，Main 负责整合、窄回归与实际桌面操作。不实现错峰/追加启动、不改用户 Terminal 设置、不启动真实 Agent 压测、不 commit/push。
- 基线 `artifacts/ui-usability/before-default.png`：默认 1120×800 DIP、系统 125% DPI 下，资源设置按钮屏幕 Y=1082～1117，已在主窗口底部 Y=1070 之外。将资源入口移到摘要首卡；默认窗口已实测进入 Y=395～440 的可见区。项目卡去掉重复引导、配置行将目录摘要与操作并排，每项预算与校验结果保留；四计数区分配置、勾选、预约和 Started，不制造运行状态。
- 资源明细渐进展开，模式、准入原因、CPU 封顶和运行风险仍常驻；参数/目录仍在详情可查看和编辑。对话框模式优先、只读原因邻近字段、错误固定在按钮上方；输入框先于旁边选择按钮进入 Tab 顺序，长标签换行。仅改善布局，不放宽数字范围或活动批次锁定。

### 已复现的功能问题

- 确认期间旧 Pending 预约释放：monitor 重新判定 `Allow`，`WaiverTable.Issue` 因无需签发而返回 `Ok=false, Decision=Allow`。原 `AuthorizePlansAsync` 先检查 `Ok`，把“资源充足”当错误，后面的 Allow 分支不可达。
- 仅调整 UI 分支顺序，先识别明确的 `Allow` 并返回无 waiver；不改 Issue 的公开返回合同、不改策略，后续正常 reserve 仍由 monitor 核验硬保护与工作目录。Deny/无效授权保持拒绝。
- 新独立 WPF/IPC 回归走真实 `AuthorizePlansAsync` 与 monitor，不创建 Agent：旧代码 **1 通过 / 1 失败**，失败消息“资源充足”；修正后 **2 通过 / 0 失败**。正控要求持续软超仍必须有单次授权且重放失败；恢复场景要求两个 Pending 预约成功且无 waiver。证据原始根分别为 `D:/temp/User/AgentLauncher-tests-waiver-recovery-bb687125836d4318b1484bf577c108a1` 与 `D:/temp/User/AgentLauncher-tests-waiver-recovery-2f7f075ba95045838c4ca6b4b72ce316`。

### 已执行验证与限制

- `artifacts/ui-usability/surface-results.json`：默认 4 槽 13 项、16 槽 13 项、深色＋150%＋窄窗 12 项，**38 通过 / 0 失败**。实际点击/键盘操作覆盖资源入口可见、底部按钮、Space 勾选与焦点、Tab 到详情、只读/自定义切换、非法值不落盘、错误固定可见、Escape 取消。对应截图在同目录。
- 原 `artifacts/p0-smoke/ui.ps1` **138 项通过**，保留原 30 秒上限。没有修改原有测试文件或断言。
- 原 `artifacts/p0-smoke/journey.py` 未修改：**7/7 通过、57.48 秒**；16 槽勾选 3 项，三种受控测试进程与 Terminal 窗格正确启动，完成后预约归零、monitor 空闲退出、手动资源检查可重新拉起。新证据根 `D:/temp/User/AgentLauncher-tests-p0-e2e-103a53cf42944884af6107645ae24cce`；不代表真实 Agent 性能/登录验收。
- 本轮独立回归源码及结果归档 `artifacts/ui-usability/review-inputs.zip`，供用户评审；不改原 tests/runner 接线。
- `task_verify run` 返回 **unverified / project-or-git-baseline-unavailable，checks=[]**；operator passed/failed 均无执行项。上述手动通过不能替代 operator 基线。
- 正常预览已由 `scripts/build.ps1` 重建（exit 0、6.37 秒），入口 `artifacts/preview/AgentLauncher.App.exe`；四个程序集均未检出 `AGENT_LAUNCHER_TEST_ROOT`，hash 证据 `artifacts/ui-usability/preview-hashes.json`。
- 验证脚本最初将 Edit 的 `window_text()` 值与 UIA `Name` 混用，导致字段定位失败；直接读取 UIA 属性确认字段名称完整后，改为按 UIA Name 查询，未放宽断言，未为此更改产品。
- 新独立回归与本轮桌面操作脚本属于待评审验证输入；手动绿色不等于 operator 基线。没有做真实用户可用性研究、屏幕阅读器体验测试或真实 Agent 满载测试，不能声称“符合所有用户习惯”或“全项目无问题”。

## 18. 16 轻量窗格实际验收与未归因样本（2026-11-11）

### 范围与固定条件

- 继续核对发现，第 13/15/17 节中的旧五项问题和授权恢复已在当前源码修复，未重复修改。本轮补规格 §15.1 的 **16 个实际轻量窗格**；此前 16 槽勾选 3 项的证据不能替代此项。
- 没有修改产品源码或重新发布预览。19 个既有测试、runner 和资源策略输入前后 SHA-256 完全一致，清单在 `artifacts/p0-sixteen/input-identities.json` 与 `input-revision.json`。本轮新增独立 `PaneProbe` 和桌面场景脚本，只作为待评审输入，不接入固定测试集。
- 只用独立临时根、现有 `INTEGRATION_TESTS` 程序集及 `AGENT_LAUNCHER_FAKE_SAMPLE=1`，不运行真实 Agent。夹具显式使用产品已有的自定义配置：每项规划 256 MiB、Job 上限 2 GiB、CPU 封顶 35%；仍经过 monitor 的一次性软超确认。执行前另要求真实可用内存至少 2 GiB；没有降低该门槛或更改产品保护。此配置不构成真实 16 Agent 的容量建议。

### 本轮完整通过证据

- `python artifacts/p0-sixteen/inputs/scenario.py` 最后一次运行 **exit 0，22/22 通过，32.59 秒**；原始证据 `artifacts/p0-sixteen/run-3c3cd79fe1/result.json`。实际桌面截图 `sixteen-visible-panes.png` 与 `sixteen-started-ui.png` 已打开核对。
- 确认 16 个不同受控子进程和 16 个 pane Worker、16 个稳定槽同批预约、中文/空格/分号/`&` 工作目录原样传入；实际 Terminal 为 4 列 × 4 行，每格都有受控程序输出，不是空占位。初始键盘输入只由第 1 项接收；左上/右上/左下/右下的第 1/13/4/16 项标题均正确。
- 产品命令构造函数给出的实际命令长度 **7540 字符 / 24000 字符上限**，没有截断。WPF 四计数均为 16；释放受控程序后 16 个退出码均为 0，Started 与预约归零，pane Worker 和子进程全部退出，UI 仍开着时 monitor 空闲退出；`forcedCleanup=[]`。
- 用户 Terminal `settings.json` 的内容 SHA-256 和修改时间前后相同。最终运行真实可用内存约 3.35 GiB → 2.43 GiB，属于当时整机观察值，不是产品独占占用或性能上限。

### 失败记录与不能关闭的风险

- 首次 `run-a37f1966c2` 因真实可用内存约 0.93 GiB 在启动任何 App/Worker/Terminal 前停止。之后只读核对确认余量恢复，保持原门槛再执行；没有终止用户进程。
- `run-eac25f887c` 的矩形转换错误属于探针：`RECT` 不可迭代，改为读取 left/top/right/bottom。`run-8a7f07b79a` 的右下角点击被其他应用通知截获；`run-1568c5f55d` 的键盘输入未进入 Terminal。修正为原生前台句柄检查、逐键拒绝焦点丢失、点击前命中测试窗口核对。截图/类型实验提供边界证据，未更改布局、标题、焦点、数量断言，也未放宽等待上限。
- **仍未归因：`run-81f23ce3e5` 有一次 15/16 启动。** 16 项均已预约，第 6 项保持 Pending 且没有 claim/回执，另 15 项运行后正常退出；没有自动重发批次，也未把缺失项计为 Started。两种解释仍是 Terminal 未创建该窗格，或 Worker 在 claim 前退出。失败窗口未被原探针及时采集；随后增加启动早期画面/文本观测的运行未再出现该缺失。Application 日志只有同期 WindowsTerminal `RADAR_PRE_LEAK_64` 报告，不能据此断言根因。最终一次通过不证明该偶发情况已修复；应保留此风险，不宣称 16 窗格长期稳定或全项目无缺陷。

### 评审与 operator 状态

- 新探针源码、各次原始 JSON、输入指纹与变更理由归档 `artifacts/p0-sixteen/review-inputs.zip`；临时源码/编译目录移除。请评审新增验证输入及观测修正，后续用户回合才能建立对应基线；本轮绿色仅为手动执行证据。
- `task_verify run` 返回 **unverified / project-or-git-baseline-unavailable，checks=[]**，operator passed/failed 均无执行项。GitNexus 仅有无关且落后 11 commits 的 `clideck` 索引，本项目无可用 context/impact；没有重建无关索引。LSP 无配置。
- 未进入 P1，未运行真实 Agent 压测、未进行 OOM 或长时稳定性实验、未 commit/push。产品源码未改，因此未重复已通过的全量套件或预览构建。

### 后续启动边界诊断（2026-11-11，同日继续）

- 预期 16 项均产生 Worker claim 和 Started 回执；历史实际为第 6 项 Pending 且无 claim。当前源码顺序为 Worker 入口 → 独占 attempt mutex → claim → IPC begin → 子进程，待查边界仍是 Terminal 未启动 Worker，或 Worker 在 claim 前退出；不归咎于后续资源准入或焦点点击。
- WMI `Win32_ProcessStartTrace` 订阅返回拒绝访问，未提权或绕过。独立诊断副本仅加入普通权限进程快照、已确认测试 Worker 的查询/同步句柄、claim/回执观测；不修改产品程序集。快照可能漏掉短命进程，所以缺少进程样本不能证明 Terminal 未创建它。新输入仍待评审，原 16 项、25 秒 Started 等待和 2 GiB 真实内存守卫不变。
- `artifacts/p0-start-boundary/run-aaec3bcafd/result.json` 与 `run-6ed7eea65b/result.json` 各 **22/22、exit 0**；前一命令含独立 probe publish 耗时 38.92 秒，后一场景 32.11 秒。分别采集 441/431 次快照，均逐一匹配 16 个 pane Worker、16 个 claim，所有 pane Worker 和 monitor 退出码为 0，观测错误为空，`forcedCleanup=[]`，Terminal 设置内容和时间戳不变。第一轮实际 4×4 截图已打开核对。
- 连续两次未复现后，停止同条件重试，改用原失败的归档账本离线重建命令：16 个 UUID 均恰好一次，包含第 6 项，不支持确定性组帧遗漏。只生成命令，未提交 Terminal 或重放批次。诊断 runtime 路径较历史长 7 字符，命令为 7652 字符，不冒充历史命令原始字节。初次离线调用引用已清理的临时 `control.json` 而非零退出，改用保留在原报告中的 `finalLedger` 后通过；原始失败记录未修改。
- 结果汇总 `artifacts/p0-start-boundary/diagnosis.json`；新增观测源码与证据归档 `review-inputs.zip`，临时源码和编译输出清理。19 个固定输入及前轮评审归档保持不变。`task_verify run` 仍为 **unverified / project-or-git-baseline-unavailable，checks=[]**，operator passed/failed 均无执行项。本轮没有产品补丁或红绿修复回归；两种启动假设仍未区分，不能将两次绿色写成原故障已修复。

### 同步启动记录器校准与内存阻断（2026-11-11，同日继续）

- 为弥补快照漏掉短命 Worker 的盲区，新增独立原生 `StartRecorder`：使用自身 `CreateProcessW` 返回的句柄记录创建结果、PID、创建时间和退出码，不订阅受限 WMI、不提权。隔离配置通过现有 `TerminalPath` 接入诊断 Relay，在原 pane 命令前加记录器；产品源码、用户配置和 Terminal 设置不改。该插桩改变进程层级及少量时序，不能冒充原始未插桩场景。
- 两个 .NET 夹具发布、原生 C 的 `-Wall -Wextra -Werror` 编译成功。首次 `calibration.json` 的早退进程已经被完整记录，但脚本把代码页 936 的 stderr 当 UTF-8 解码，中文原因断言失败；只修正诊断解码为实测代码页且保留原始十六进制字节，原断言不变、失败文件保留。随后 `calibration-35169b1330.json` **2/2 通过**：不存在的可执行文件产生 `CreateFailed/Win32=2`；持有隔离 attempt mutex 时，真实 Worker 产生 `WorkerCreated → WorkerExited/ExitCode=1` 且无 claim，错误明确为“该启动请求正在处理”。这是记录器的受控校准，不是历史故障根因或产品修复回归。
- `run-f803df2b92/result.json` 的 16 窗格场景被原有真实内存守卫拒绝：可用 **1684160512 字节（约 1.57 GiB）< 2 GiB**；后续只读采样为 1472167936 字节（约 1.37 GiB）。App、monitor、Terminal 均未启动，账本为空、原生记录为空、`forcedCleanup=[]`。没有降低门槛、关闭用户应用、清缓存或继续重复启动；恢复至少 2 GiB 可用内存后才可执行该场景。
- 汇总 `artifacts/p0-sync-start/diagnosis.json`，7 个新增诊断输入及校准/阻断证据归档 `review-inputs.zip`，可按归档 manifest 恢复；临时源码和编译输出清理。19 个固定输入、前两轮评审包和本次使用的 Worker/Core/Windows 程序集保持原哈希。新增输入待评审，插桩 16 窗格端到端仍未验证。`task_verify run` 为 **unverified / project-or-git-baseline-unavailable，checks=[]**，无 operator passed/failed 执行项；原 15/16 风险仍未关闭，未 commit/push。

### 同步记录的 16 窗格完整运行（2026-11-11，内存恢复后）

- 用户要求继续后，真实可用内存预检为 4937990144 字节（约 4.60 GiB）；恢复 7 个归档输入并逐一核对 SHA-256，原样重建，没有再次执行已通过的故障校准。运行自身仍执行原 2 GiB 守卫：启动前采样 2211643392 字节（约 2.06 GiB），通过；中途整机采样 3295473664 字节（约 3.07 GiB）。这些是整机瞬时余量，不是夹具的独占内存测量。
- `artifacts/p0-sync-start/run-8e502b86d4/result.json`：**25/25 通过、exit 0**；命令总耗时 **42.37 秒，包含两个 .NET 发布和原生编译**。原有 22 项布局、输入、标题、WPF 计数、回收和 monitor 空闲退出检查全部通过，另 3 项确认 Relay 收到原始产品参数、16 项都有原生创建/成功退出记录、记录中的 Worker PID 与实际父进程匹配。未延长 25 秒 Started 等待、降低数量或内存门槛。
- 一次性关联核对 16 个账本 attempt、claim、实际 pane Worker、受控子进程回执：均一一对应，原生 `WorkerCreated/WorkerExited` 的 PID 和创建时间一致，16 个 Worker 与 16 个子进程的退出码全部为 0。预约及 Started 计数归零，`forcedCleanup=[]`；用户 Terminal 设置 SHA-256 与修改时间未变。四个产品程序集哈希与历史 15/16 失败时完全相同；产品源码和 19 个固定输入未改。
- 本次产品构造命令为 **7588 字符**，加同步记录器后真正交给 Terminal 的命令为 **10068 字符**，均低于原 **24000 字符**门禁。不能用前者冒充插桩后的实际提交长度。
- 已打开实际 Terminal 和 WPF 截图核对：WPF 四计数均为 16；Terminal 的 4×4 排布可见，但保存画面时右下角部分被其他应用通知遮挡，不能称为无遮挡的全格视觉证据。16 格 UIA 文本/几何及右下角标题检查已通过；未修改通知设置，也未重复启动来重拍。
- 本轮关闭的是“同步记录器的 16 窗格端到端未验证”缺口，**没有复现或修复历史 15/16**。插桩改变命令和进程层级/时序，不能据此证明未插桩长期稳定。原失败记录、校准失败、内存阻断及原评审包保留；本轮新增结果见 `artifacts/p0-sync-start/run-8e502b86d4/verified-lifecycle.json`，诊断输入仍待评审。未改产品源码，未重跑全套测试或重建预览，未 commit/push。
- 本轮手动场景 passed=25、failed=0；最终 `task_verify run` 仍返回 **unverified / project-or-git-baseline-unavailable，checks=[]**，operator passed/failed 均无执行项，原样结果保存于本次运行目录的 `operator-verification.json`。7 个恢复源文件与原归档逐一匹配后已清理，生成的诊断运行时也已移除；原 `review-inputs.zip` 未改写。

## 19. CI worktree 长路径失败的单因修复（2026-11-11）

- 用户明确授权 commit/push 并在 CI 绿色后合并；P0 提交 `6123b79` 已推送 PR #2。其 [gate run 42940773058](https://github.com/zhuguang-ZFG/agent-launcher/actions/runs/42940773058) 模型检查 **65/65**，集成阶段两个 CR12 合法目录用例失败于 `Git 准备失败（128）：fatal: '$GIT_DIR' too big`；WPF/浏览器未到达，未合并该失败 head。
- 根因是跨层路径表示假设，不是继承了错误 Git 环境变量。本机 Git 2.54.0.windows.1 的隔离对照：160 字符绝对目标成功，225 字符绝对目标失败；同长目标加 `-c core.longpaths=true` 仍失败；改用指向同一目标的 178 字符相对参数成功。上游 [builtin/worktree.c](https://github.com/git-for-windows/git/blob/v2.54.0.windows.1/builtin/worktree.c#L585) 将 `<target>/.git` 导出为检出子进程的 `GIT_DIR`，因此绝对目标写法会额外触发其长度限制。
- 产品仅修改 `WorkspacePreparer.PrepareAsync` 的 `worktree add` 参数：在固定物理源仓库 cwd 下选择更短的等价相对目标；跨卷或相对表示更长时保持绝对目标。实际目标、Intent/Completed 记录、源仓库身份、分支/OID、碰撞与恢复核对均不改，没有全局 Git/注册表设置变更，也没有分拆 checkout/reset。
- 通过独立窄执行器链接**未改动的** `WorkspaceReviewChecks.cs`，只执行 CI 中失败的原两个用例。测试根长度同 CI 均为 **104 字符**：旧版 **0 通过 / 2 失败，exit 1**；修正后 **2 通过 / 0 失败，exit 0**。测试源 SHA-256 和窄执行器 DLL 哈希一致；本轮 19 个固定测试/runner/策略输入及 CI 工作流均未修改。复现不是重新跑旧 CI 求偶然绿色，而是在本机等长路径上区分原因并验证同一断言。
- 原始 `red.json`、`green.json`、Git 参数对照及窄执行器归档 `artifacts/ci-git-path/review-inputs.zip`，新增诊断输入待评审，不接入或替换固定测试集；源码和临时编译输出清理。`task_verify run` 仍为 **unverified / project-or-git-baseline-unavailable，checks=[]**；手动红绿不替代 [PR #2](https://github.com/zhuguang-ZFG/agent-launcher/pull/2) 当前提交的完整 Windows gate。
- 本修复只针对已复现的 Git 创建边界，与第 18 节未归因的 15/16 部分启动不同；不宣称后者已修复。现有 CI 阈值、测试断言和工作流保持不变，完整 CI 成功后才允许合并。

## 20. 「资源组句柄丢失」的用户确认核对释放（2026-11-11）

### 缺口与触发

- 生产 monitor 因 2026-11-10 的陈旧 `Creating` 条目（回执 `Exited`、Job 内核对象已不存在）永久阻塞准入，此前唯一产品内恢复路径是系统重启（boot-id 变化整批 Settled）；上一轮已用与 boot 恢复等价的人工转换解除。该缺口属于产品能力缺失：没有任何界面路径让用户在证明成立时释放预算。
- 本轮新增用户确认的「核对并释放占用」流程，恢复条件不放松：重启仍是无法证明时的兜底，释放只做 fail-closed 证明，不结束任何进程、不删除任何记录。

### 设计与实现

- 协议：`Response` 追加可选 `ReconcileOffered`（旧客户端忽略新字段；新客户端遇旧 monitor 得到 null，不显示入口）；分页帧一致性比较纳入该字段。新增 IPC 操作 `reconcile`，App 侧经 `Ipc.EnsureAsync`（必要时拉起 monitor）发送。
- monitor：Refresh 在「资源组句柄丢失」阻塞分支置 `reconcileOffered=true`（每次 Refresh 重置），`BuildStatus` 上报并纳入状态签名。`ReconcileRequests()` 对每条非终态非 Pending 记录依次证明：取得 attempt gate（创建者不在运行；持不到即拒绝）、打开资源组返回 Win32=2（对象确不存在；可打开则不属本流程，交常规恢复）、回执存在且为 `Finished`（运行器已按 Job 成员枚举证明全部受管进程退出）或 `FailedBeforeStart`（进程从未运行）。证明全部满足才置 `Settled`/`FailedBeforeStart` 并把证明写入 Detail 后原子保存；其余一律保持阻塞并写明「核对释放被拒绝：…」——记录进程仍存活是一类，**仅父进程退出但派生进程无法枚举核对**是另一类（产品 Job 不设 KILL_ON_JOB_CLOSE，句柄销毁后作业对象可在后代存活时消失，无句柄即无法枚举）。随后 Refresh 重算阻塞并返回含记录快照的响应。
- App：资源面板新增 `ReconcileButton`（`QuietButton`，默认隐藏），仅当 `status.ReconcileOffered==true` 且非 busy 时显示；点击先弹 YesNo 确认（说明证明内容、不结束进程、不删除记录），确认后调用 `reconcile` 并刷新状态，结果写入进度/错误区。确认走既有 `TestConfirm` 测试钩子。
- 文档：CONTRACTS C4 增加该恢复路径的 fail-closed 合同。

### 回归证据（红 → 绿）

- 集成回归「R-handle-lost reconcile releases finished-tree proof and refuses unprovable trees」用**真实内核语义**构造三类条目：`Finished` 回执（运行器已排干 Job 成员）应释放；真实 Job 中父进程被杀、同作业后代仍存活、最后句柄关闭后按名打开返回 Win32=2 的 `Exited` 条目**必须拒绝**（合成 Job 从不存在的情形不足以覆盖该语义）；记录进程真实存活的 `Started` 条目必须拒绝。断言含「幸存者进程在核对后仍存活」——证明若释放即为错误。
- 红（第一版弱实现）：该用例失败于「dead parent with a live surviving tree member was released: Settled …原阶段 Exited」——精确复现了评审指出的缺陷；断言失败留下的幸存者进程还导致后续 27 项级联失败（污染真实 monitor 根 Job 枚举），已在测试中改为 try/finally 即时清理。其余用例不受本变更影响。
- 绿：收紧后集成套件 **0 失败**（证据根 `D:\temp\User\AgentLauncher-tests-integration-green5-1794376392`）——`Finished` 条目释放、真实树场景拒绝且幸存者核对后仍存活、活进程条目拒绝。
- 早期版本（仅合成死进程 PID+FILETIME 核对即释放）曾全绿（模型 65/65、集成 0 失败、UI 142/0、原型 5/5、浏览器通过、E2E 13/13、生产健康），随后评审意见指出父进程死亡不等于进程树死亡，本轮据此收紧；UI 可见性断言（4 项）与 App 交互流程不变。

### 固定输入变更披露

- 本轮为新增回归修改了两个 pinned 测试输入：`tests/AgentLauncher.Tests/Integration.cs`（+1 用例）与 `src/AgentLauncher.App/UiReviewChecks.cs`（+4 断言），两者哈希随之变化；只增不改，既有断言与阈值原样。与 `artifacts/preview-delivery-6ce19ec/verification.json` 基线逐项核对：21 项固定输入中其余 19 项 SHA-256 不变（含 `.github/workflows/ci.yml`、`Directory.Build.props`）。新用例与断言在 operator 建立基线前属待评审输入；手动绿色不替代 operator 基线。

### 端到端与生产验证

- 收紧后的隔离 E2E（`artifacts/reconcile-e2e/inputs/e2e_reconcile.py`，INTEGRATION_TESTS 构建，真实按钮与原生确认框，win32 `#32770` 级交互）**21/21 通过**（证据 `artifacts/reconcile-e2e/run-12/result.json`），按真实内核语义分两个场景：
  - **场景 A（自动恢复）**：`Finished` 回执 + 资源组不存在 → 监控器常规 Refresh 直接 `Settled`（整树证明来自运行器，无需用户操作），且按钮全程不出现；回执哈希不变。同时以活动管道名验证 Python 复制的 Scope 与产品一致（管道 `AgentLauncher-<scope>` 实测存在）。
  - **场景 B（拒绝路径）**：真实作业树——`CreateJobObjectW` 按产品命名建组、挂起创建的父进程 `AssignProcessToJobObject` 入组、父进程派生长命孙进程（默认继承作业）、杀父、关闭最后句柄——实测按名打开返回 **Win32=2** 且孙进程仍存活（句柄丢失≠后代退出，真实内核证据）；`Exited` 回执条目楔住后：按钮出现；**答「否」条目保持 Creating 且不发 IPC**；按钮继续提供；答「是」后 reconcile **拒绝**——条目保持 `Creating`，Detail 写明「核对释放被拒绝：…派生进程无法枚举核对」；**孙进程核对后仍存活**；BootId 保留；回执与配置哈希不变；阻塞保持且按钮再次提供。重启仍是该情形的唯一兜底，与合同一致。
- 设计澄清（run-9 暴露）：第一版 E2E 以 `Exited` 回执验证「死父释放」，收紧后该情形属必须拒绝类；而 `Finished` 回执条目在常规 Refresh 即自动落终态，根本不会楔住——经用户确认的释放路径仅兜底非常规情形（如 Refresh 因 IO 异常跳过的记录），日常恢复无需按钮。run-10/11 为脚本自身问题（UTF-8 解码系统代码页输出失败、管道检查时序过早），与产品无关。
- 早期 13 步版 E2E（run-8，弱实现 + `Exited` 死父释放正向路径）记录作废，不代表当前语义。
- 生产实测（真实用户数据，只读交互）：健康账本不显示按钮；`SetWindowPos` 三尺寸（1120×800/1400×1000/900×700，前台句柄均为本窗口）布局正常——宽版双栏资源面板、窄版单栏滚动且底部警告正确换行；App 优雅退出 exit 0；用户 `configuration.json`/`control.json`/`.bak` 哈希前后不变。UIA 改尺寸两法（`move_window`、TransformPattern）在本窗口不可用，按两次失败规则改用 Win32 `SetWindowPos`，未再重试 UIA。
- 生产 DLL marker 扫描（`AGENT_LAUNCHER_TEST_ROOT`/`FAKE_SAMPLE`/`INTEGRATION_TESTS`/`TestConfirm`）四个程序集全部 CLEAN（收紧构建复扫：`marker-scan-tighten.json`）。预览再次替换：`artifacts/preview-before-tighten` 备份收紧前构建，`artifacts/preview` 为收紧构建（15 文件，含使用说明），桌面快捷方式目标路径不变；哈希清单 `artifacts/preview-delivery-reconcile/verification-tighten.json`。
- 评审归档：`artifacts/reconcile-e2e/review-inputs.zip`（收紧后重建，含脚本与场景证据）。`task_verify run` 仍为 **unverified / project-or-git-baseline-unavailable，checks=[]**；手动绿色不替代 operator 基线。PR #3，CI 全绿后合并。

### 限制

- 核对释放在「资源组可打开但无进程」等常规可恢复情形下不介入（常规 Refresh 已处理）；仅覆盖句柄丢失类阻塞。释放只接受回执级整树证明（`Finished`/`FailedBeforeStart`）；无法证明的记录（回执缺失、回执仅为 `Started`/`Exited` 等父进程级证据、进程存活、创建者仍在、其它 Win32 错误）一律保持阻塞，只能等系统重启或人工按 .bak 核对。
- 生产环境上一轮人工修复的条目维持原样（Detail 已含证明）；本轮不改动用户 `control.json`/`.bak`/回执。
- 历史 15/16 部分启动仍未归因，与本流程无关，不因本轮绿色而关闭。

## 21. 资源确认后数据流提前结束（2026-11-11）

- 用户确认的错误为 `Unable to read beyond the end of the stream.`，发生在窗口已打开、点击启动并确认资源授权之后；当日没有新增生产启动账本条目。真实 UI 复现相同错误。
- 根因：空闲 monitor 收到请求先强制 Refresh，waiver/reserve 处理再次强制 Refresh。`GetSystemTimes` 在同一 CPU 计数周期可返回相同累计值，原 Sampler 将零差值变成 NaN；带该读数的 Response 由 System.Text.Json 序列化时抛 ArgumentException，monitor 退出，客户端只见 EOF。原集成测试固定模拟 CPU=5，因此漏检。旧 DLL 的本机 1000 次紧邻采样有 975 次 NaN/序列化失败。
- 修复：零时间差时沿用上一 CPU 区间及其原始时间戳，内存仍重新读取；不刷新旧 CPU 的时间，不将未知值伪造为零。首次采样仍是未知。IPC 写响应将非有限 CPU 的资源快照置为不可用（Memory=null），保留原有拒绝结果与错误说明，避免损坏通信。准入硬保护和单次授权协议不变。
- 新增 P01 回归：1000 次原生紧邻采样、NaN/正负无穷的拒绝响应往返、独立 monitor 使用真实 CPU 的 50 轮 status/waiver 往返（明确移除模拟采样环境变量，不启动子进程）。只新增断言，未修改现有门槛或工作流。
- 验证机器为本工作站 Windows x64 / .NET SDK 8.0.419。模型/原生 **67/67**；集成 **93/93**（含新增真实采样回归）；WPF **142/142**；原型模型 **5/5**。发布 DLL 原生 1000 次采样复核 **0 NaN / 0 序列化失败**。日志 `artifacts/startup-diagnosis/final-gate.txt`，WPF 结果 `artifacts/startup-diagnosis/ui/ui-checks.json`，集成结果 `artifacts/integration-results.json`。
- 真实 UI → 资源授权 → Windows Terminal 四窗格 → 四个测试子进程完整路径验证成功；使用真实读数，四个 argv/cwd 正确，四个 Finished 回执 exit 0，四条账本具有 monitor 签发的 WaiverId。隔离测试未在用户项目运行 Agent。证据 `artifacts/startup-diagnosis/AgentLauncher-tests-native-ui-ef4e18ef712646a2b7a7e0ba0d6a5234/result.json` 和同目录 `success.png`。第一次自动化等待确认窗口超时，但四项已完成；第二次据输出、回执和授权账本核验，不将弹窗观察时序作为启动结果。
- 完整脚本在 WPF 阶段两次超时，落盘断言指向 ACC-L02：环境 TEMP 为 `D:\Temp\User`，物理路径为 `D:\temp\User`，既有断言使用大小写敏感的字符串相等。使用物理路径拼写的独立测试根重跑原 WPF 检查后 142 项全部通过、exit 0；本次未改该断言。浏览器原型检查执行但受阻于 `CDP timeout: Page.enable`，因此不宣称整条 `scripts/test.ps1` 全绿。
- 已备份旧 preview 至 `artifacts/startup-diagnosis/preview-before-fix`，生产修复构建为 `artifacts/preview-sampling-fix`，已替换 `artifacts/preview`；桌面原快捷方式仍指向该目录。生产 `AgentLauncher.Windows.dll` SHA-256：`89537208DEE178C2CE48647D3A833FB41836BB33B250D33752FFF3A16244BB33`。四个生产程序集均未发现测试根/模拟采样标记。未手工修改用户配置、control.json 或回执，未提交或推送代码。
- 后续生产验证：可用内存降至约 3.6 GiB，原四项需要 3 GiB 规划 + 2 GiB 系统余量，正常拒绝；用户明确选择「先启动前两项 Omp + Codex，保留另外两项配置」。通过界面取消第三、四项勾选，保留路径/参数；第一次两项申请因内存瞬时不足在预约前拒绝，核对无新条目后重试。15:13 成功启动 Omp（`D:\Users\monidianlu`，PID 6640）及 Codex 的 Node 入口（`D:\Users\claude`，PID 17772，原生 Codex 子进程 27704），两条 Started 回执、对应进程持续存活，Windows Terminal 新窗口已打开。证据 `artifacts/startup-diagnosis/production-two-agent-result.json`；只确认启动与存活，不将其表述为模型会话已就绪或任务已完成。配置和账本变更由产品正常流程写入。

### CI 跟进与计数倒退补充

- `40a1fb5` 已提交推送 main；GitHub run `42978866856` 在 R13 同步 ResumeThread 拒绝测试失败，原因是其前面的句柄丢失场景将合成进程加入共用根 Job，未归属成员可被主 monitor 观察到，导致后续预约正常拒绝。该场景现使用唯一根 Job，反射 monitor 也绑定同一隔离组；新增断言在幸存进程仍活着时验证主 monitor 未受污染。保留原有全部安全断言、阈值和 CI 步骤。
- 本地追加验证暴露真实 CPU 计数短暂倒退：原始 `GetSystemTimes` 20 万次采样捕获 9 次负总差值，之前的零差值保护不足。现对总差值 <=0 均保留原 CPU 基线和原时间戳，内存继续重采；确定性回归把基线置于真实计数之前，验证倒退不破坏有效读数、不改写基线，也不能给超过 3 秒的旧证据续期。IPC 非有限数保护仍然存在。
- `scripts/test.ps1 -SkipBrowser`（环境 TEMP/TMP 使用实际 `D:\temp\User` 拼写）exit 0：模型/原生 **68/68**、集成 **93/93**、WPF **142/142**、原型 **5/5**；真实采样独立 monitor 追加 **10×50 轮 status/waiver** 全部通过。证据 `artifacts/startup-diagnosis/counter-final-gate.txt`、`counter-targeted.txt`。本地浏览器仍按前述 CDP 超时记录，远程完整 gate 将验证浏览器。
- 后续核验：提交 `ffdfe78ba3d3f84ed7b6ae67e1131744df12fa4f` 的 GitHub Actions run `42982614811` 已 completed/success，完整远程 gate 通过；该结果仅覆盖该提交，不覆盖下节尚在工作区的 Omp 修复。

## 22. Omp 原生退出诊断与下一批内存设置（2026-11-11）

- 用户确认异常退出窗格为 Omp，退出码 `-1073740791`（`0xC0000409`）。Windows Application 事件记录 PID 20352 在 15:42:11 的故障模块为 Omp 18.18.8 的 `pi_natives.win32-x64-baseline.node`；对应回执 15:42:14 Finished。历史作业未保存峰值，不能认定所有同码退出都由同一个原因导致。
- 读取当前内核 Job：一个 Omp 作业树约 2048.1 MiB 峰值 / 2048 MiB 限额，含多个 Omp、MCP 与工具进程。证据 `artifacts/startup-diagnosis/omp-job-peaks.json`。限额是整棵树共享，不能把单个 omp.exe 的私有内存当作作业总量。
- 隔离探针用 Node 24.14.0 加载同一 18.18.8 原生模块，`countTokens('abc '.repeat(16*1024*1024))`：128 MiB Job 下输出 `memory allocation of 134217730 bytes failed` / `pi-natives alloc crash`，返回同一退出码；512 MiB 下同输入成功、exit 0。探针位于 `artifacts/startup-diagnosis/native-probe/`。这是原生分配失败机制的受控复现，不等同于完整 Omp/Bun 的实际任务回放。
- 实现：增加可空 `JobMemoryEvidence`，查询真实 Job 上限与提交峰值；父进程退出时保存到 Receipt，Finished 保留同一份退出时证据。查询不可用明确未知。异常退出在等待工具排干前即打印诊断；状态中接近上限时显示峰值提示。退出码本身不被当成内存因果证据。
- 设置改为可调整**之后批次**的内存参数；活动 Job 不改，活动期间共享根 CPU 继续锁定。解释额度包括工具子进程。旧 Receipt 无新字段仍可读取，旧 profile 和配置默认值不改。
- 新增/增强 P02 原生、Receipt 往返和跨批次集成检查；真实夹具先运行 2 GiB 作业，在配置改成 4 GiB 后走正常 waiver/reserve，再启动下一项，最终回执分别证明 2/4 GiB 且都有非零 OS 峰值。新增 WPF 检查验证内存可编辑、CPU 禁改且不能由程序赋值绕过。
- 验证：完整 `scripts/test.ps1`（含浏览器）exit 0，原生/模型 **69/69**、集成 **0 失败**、WPF **143/143**、原型 **5/5**、浏览器检查全部通过；证据 `artifacts/startup-diagnosis/vibe-s0-gate.txt`、`artifacts/ui/ui-checks.json`（Dpi 120）、`artifacts/integration-results.json`。本轮之前 `-SkipBrowser` 的同套结果见 `omp-authorized-gate.txt`。中间失败保留在 `omp-final-gate.txt` / `omp-sequential-gate.txt`：新夹具启用槽不匹配、软授权缺失已经修正；一次 Git 准备 pipe broken 在后续顺序执行中未再出现，尚无独立根因结论。
- 生产候选目录 `artifacts/preview-omp-stability` 按当前工作区重建（14 文件），四个程序集 marker 扫描 CLEAN；SHA-256（含参数预设、版本探测、任务队列、共享池创建/回收生命周期、真实准备批次池、资源对话框池选择与启动账本核对）：App `c0dd9046ab79b802be4fa57091bffd7160fb018b6e47fb9639c44019fa5088b5`、Windows `b098a31c0305c16ebabeba32edde83d6769e87c8c6eb5c82e3b091b38dee2b7f`、Worker `4d8c807cb97d4d35c54a57aaaa2b4a3eb5178b76a9e21da5e4d411890b814d9c`。当前生产进程与用户配置未替换/修改，仍可能使用 2 GiB 旧限制；交付切换、对应最终提交 CI 与 30 分钟 × 3 轮代表性负载仍属待完成事项。本轮不宣称频繁退出已彻底解决。
- 用户随后要求进入扩展规划阶段，新增 [扩展规格](superpowers/specs/2026-11-11-vibe-workbench-spec.md) 与 [实施计划](superpowers/plans/2026-11-11-vibe-workbench-plan.md)。参数预设、任务队列、共享池、审查中心和 Omp 多会话均为后续计划；稳定性封板为 S0 前置门槛。

## 23. 参数预设与 CLI 能力核对（2026-11-11）

- 版本证据：本机实际执行 `omp --version`（`omp/18.18.8`）、`omp --help`、`codex --version`（`codex-cli 0.167.1`）、`codex --help`、`codex resume --help`，原始输出保存在 `artifacts/startup-diagnosis/agent-capabilities/help-2026-11-11.txt`。预设只取帮助里明确存在的旗标：Omp 新会话（无参数）、`--continue`、`--resume <会话>`、`--model <模型>`、`--thinking high`；Codex 新会话（无参数）、`resume --last`、`resume <会话>`、`-m <模型>`。没有把 `/` 开头的会话内命令或未出现的旗标做成按钮。
- 数据合同：`ArgumentPreset`（Agent、Id、名称、语义、参数数组、值标签、验证版本）与 `AgentCapability`（程序路径、SHA-256 指纹、版本、探测时间、说明）。`AgentPresets.For` 只在安装版本与验证版本完全一致时返回预设；升级、未知版本或探测失败都返回空集并显示原因。预设按 Agent 隔离，`Fill` 返回新数组，不修改共享表；参数长度、数量和 NUL 有上限校验。
- 探测：`AgentProbe` 以挂起方式在独立 Job 中创建目标，重定向三路管道，超时（8 秒）或输出超过 64 KiB 即终止整棵 Job；主进程退出后同样终止 Job，连后代一起清理。指纹对 ≤64 MiB 程序做内容 SHA-256，更大程序退回大小+时间戳。探测只运行 `--version`，不接受用户提供的 shell 片段；失败不影响打开详情或手动编辑参数。
- UI：详情对话框在版本核对后显示“套用预设…”入口；选择框逐个显示参数预览，需要值的预设再单独询问一次，值始终作为单个 argv 元素传递。套用只替换当前 Agent 的参数数组，立即按行显示，仍可逐项修改或全部删除；取消不修改配置。活动任务、只读与既有保存门禁不变。
- 验证：完整 `scripts/test.ps1`（含浏览器）exit 0，原生/模型 **71/71**、集成 **0 失败**、WPF **145/145**、原型 **5/5**、浏览器检查全部通过；证据 `artifacts/startup-diagnosis/vibe-s1-final2-gate.txt`、`artifacts/ui/ui-checks.json`、`artifacts/integration-results.json`。新增 ACC-A01 用真实子进程回显 `--`、`-x`、`--name=value`、空字符串、中文、空格、换行、分号、末尾反斜杠与预设填充；ACC-A02 覆盖真实 git 版本探测、指纹稳定性、探测超时、超时后后代不再写入、输出上限。集成新增真实 Omp/Codex 能力探测（未安装时记录跳过），WPF 检查新增预设套用后的逐参数显示与保存。
- 限制：本轮只验证上述两个安装版本；模型与恢复目标值由用户填写，不自动发现模型或会话列表；`/plan` 等会话内输入仍需在终端手动完成；探测读数是版本证据，不代表该版本所有参数组合都被验证。

## 24. 任务卡片、队列与安全续接（2026-11-11）

- 数据合同（`src/AgentLauncher.Core/Tasks.cs`）：`TaskCard` 保存项目、Agent、参数、验收清单、目录策略、优先级、依赖与修订号；`QueueEntry` 在入队时冻结任务修订、已解析命令与程序指纹；`TaskRun` 记录尝试、状态、工作区物理键、程序指纹与可空的 `ResumeOf`/`SessionRef`。`tasks.json` 带 SchemaVersion、修订号、入队序号与数量上限；版本或结构不符即拒绝读写，不覆盖原文件。
- 持久化：`TaskStore` 把读-改-写放在同一跨进程命名互斥内，调用方必须持有当前修订号，过期修订返回“已被其他窗口修改”，不会静默覆盖；保存沿用既有原子替换与 `.bak`。队列分发另有独立互斥 `Local\AgentLauncher-queue-<Scope>`，另一个窗口正在分发时本窗口跳过，避免同一排队项被两个窗口同时领取。
- 调度（`QueuePolicy`）：优先级降序、同优先级按入队序号，一次只派发一项；`MaxRunning` 默认 1；依赖任务必须已被人工完成；同一任务不允许重复入队。修订号只在影响启动的编辑（项目、Agent、目录策略、优先级、依赖、参数、资源）时递增，并同时移除该任务的排队项；只改名称、说明或勾选验收不改变修订号，也不影响排队。
- 执行：任务 Profile 保存原始参数，QueueEntry.Command 保存入队时冻结的已解析命令；派发从 Profile 重建命令，不能把已解析 argv 回填 Profile（Node shim 会因此重复入口脚本）。派发比对冻结的程序指纹、可执行文件、适配器与参数，任一变化即挂起并提示重新入队，不启动。随后用任务自己的单槽草稿走既有 `WorkspacePreparer`（共享/独立目录语义与活动目录冲突检查），再次核对准备后的工作区/命令，再执行资源准入、`reserve` 与 `SubmitTerminal`。准备目录可以复用，但每次返回的尝试、批次与池身份重新生成。软预算需要一次性确认时不自动确认，转为挂起原因。
- 结果核对：监控记录 `Settled`（回执 Finished）→ 任务进入“等待验收”，必须人工点“标记完成”；`FailedBeforeStart`/`Revoked` → 失败；`Pending` 超过 5 分钟先撤销，确认撤销成功后才标记失败。无法证明结束的尝试保持“运行中”，不猜测终态，也不会自动重放。
- 会话登记与续接：首次尝试结束后可用「登记会话」记录由用户核对的会话标识；登记要求终态、同工作区物理键、同程序指纹、已验证 CLI 版本，拒绝空白/NUL/超过 200 字符及已占用会话。已被排队续接引用的登记不能改成另一会话。续接对话框修改会话时，重新登记与入队在同一任务文件事务内保存；取消不写文件。派发时重新探测精确版本、从原始参数追加 S1 预设再解析（Omp `--resume <会话>`，Codex `resume <会话>`），保留 Node shim 入口；未知版本、已有续接/继续参数、父尝试/登记/工作区/程序变化或未结束尝试占用相同会话时拒绝。`StartRun` 在持任务文件锁的写入前再次核对冻结队列意图、任务修订与会话占用，拒绝时不消费队列、不追加历史。
- 验证：完整 `scripts/test.ps1`（含浏览器）exit 0，原生/模型 **75/75**、集成 **0 失败**、WPF **155/155**、原型 **5/5**、浏览器检查通过；证据 `artifacts/startup-diagnosis/vibe-s2-gate-2.txt`（提交 `f12bcf4` 对应代码）与 `vibe-s2-gate-3.txt`（补充分发互斥断言后重跑）、`artifacts/ui/ui-checks.json`（Dpi 120）、`artifacts/integration-results.json`。新增原生 ACC-B01–B04 覆盖优先级/FIFO/并发与依赖门禁、仅启动相关编辑使排队失效、续接的四重门禁、修订冲突、分发互斥独占以及损坏任务文件不被覆盖；WPF 新增 ACC-B05/B06 覆盖卡片渲染、验收勾选持久化、续接按钮仅在已结算会话出现、队列默认暂停、并发 1–4 调整、程序指纹变化时只挂起不启动、损坏任务文件停用队列且保留原文件。期间修正了验收勾选在后台线程读取 WPF 控件导致的跨线程失败，以及并发调整时陈旧修订号覆盖的问题。
- 与原计划的差异：队列消费者放在 App（以便复用资源确认、Terminal 提交和目录准备），不是 Worker；两个 App 窗口用任务修订号 + 分发互斥避免重复领取，Worker 仍只负责单次启动的预算与进程生命周期。
- 限制：队列由 App 进程消费，关闭窗口后不再分发（已运行的进程不受影响），重启后按文件状态继续核对。任务级资源设置只决定该项的 Job/规划值，监控准入策略仍取当前保存配置，任务不会绕过配置边界。`prepared` 目录策略对任务未开放（任务没有逐槽目录字段）。App 崩溃在“已提交 Terminal、未写回执”窗口内会留下需人工核对的尝试，队列不会自动重试；本轮没有做进程崩溃注入（领取前后强杀、关闭窗口瞬间）自动化测试，这类时序仍靠人工验收。多窗口依靠任务文件修订与分发互斥避免重复领取，但两次领取之间仍可能出现短暂双预约，后到者会撤销自己的预约。

### 24.1 队列与续接反馈修复（2026-11-12）

- 核验结果：重复准备复用尝试身份的反馈已过期，当前 `WorkspacePreparer` 最终返回已有新 ID；未额外重写身份。真实缺陷是 raw/resolved 参数边界混用，以及首次尝试没有登记入口、派发时未复核会话占用。假设“安装或版本真的变化”与“解析时重复入口”通过冻结命令和真实 shim 派发区分：旧版未改动的命令被误判变化，修正参数边界后同一任务两次执行均成功。
- RED 证据：`artifacts/queue-review-shim-red-fc3406a5/ui-checks.json` 在首次 shim 派发失败（此前 78 项通过）；`artifacts/queue-focus-registration-red-32c60dfb/result.json` 在首次会话登记入口失败（此前 9 项通过）；原生 ACC-B09 两项失败分别为入队后会话被占用仍能派发、登记会话变化未拒绝，原始结果保存在临时根 `AgentLauncher-tests-122a67486bde4e34ac3cc2d99676e904/results.json`。
- GREEN 证据：`dotnet run --project tests/AgentLauncher.Tests/AgentLauncher.Tests.csproj -c Release` 实跑 **95/95**（`AgentLauncher-tests-bf63afb0b7914835aed526fede6f6dbc/results.json`）；聚焦 WPF/真实 Terminal 的 ACC-B08/B09/B10 **26/26**（`artifacts/queue-focus-final-c6445425/result.json`）；原有 `--ui-check` 全量 WPF **192/192**、Dpi 120（`artifacts/queue-review-final-ed37f0b0/ui-checks.json`）。实际覆盖同一 shim 任务重复派发、子进程精确 argv/cwd/退出回执、首次登记、登记/续接取消零写入、关联续接新尝试、派发前占用拒绝、排队期间 CLI 升级到未验证版本拒绝。未运行真实 Omp/Codex 云端会话恢复，不宣称恢复结果正确；CLI 夹具只验证启动器的参数与保护协议。
- 中间失败未隐藏：全量 UI 的一次手动根采用 `Temp` 大小写，既有目录字面断言与规范化后的 `temp` 不同；另一次在既有布局检查阶段失败，尚未执行新用例。未修改这两条断言，改用规范化隔离根与聚焦窗口入口区分产品失败；最后完整 WPF 运行通过。子代理交付的两处 `WorkspaceIdentity`/路径类型错误由主线程修正后编译通过。
- 审查边界：只新增 `TaskChecks.cs` 与 `TaskUiChecks.cs` 的行为回归，未削弱原断言、未修改 `scripts/test.ps1` 或验证策略。临时聚焦入口位于 artifacts，不加入产品或门禁，完成后删除源码。`task_verify` 返回 **unverified / project-or-git-baseline-unavailable**，没有配置检查被执行；上述人工运行不替代操作员基线。新增测试与临时执行输入需要用户审查，仍为 unverified。
- 候选发布：`powershell -NoProfile -File scripts/build.ps1 -Output ./artifacts/preview-queue-resume` 成功，完整 App/Worker 产物在独立目录；未覆盖正在使用的预览、未切换桌面入口，生产运行和长期稳定性未在本轮验证。
- 提交与远程门禁：`b65d313365bddaedd01a2104488268f276b2302f` 已推送，GitHub Actions run [`43055929687`](https://github.com/zhuguang-ZFG/agent-launcher/actions/runs/43055929687) 的完整 gate 成功（8 分 59 秒）；这是该提交的远程证据，不涵盖后续 S3.5/S4 开发中的修改。

## 25. 共享资源池内核原型（2026-11-11）

按计划的 S3 第一关先做隔离夹具验证，不接入产品，也不改变现有独立模式。

- 夹具：测试进程先以根 Job 启动一个 `--nested-setup` 子进程，子进程在根 Job 内创建池 Job、把自身加入池、再按前缀创建单项 Job；之后测试进程用 `PROC_THREAD_ATTRIBUTE_JOB_LIST` 以 `[根, 池, 单项]` 三级链挂起创建 `--allocate-mib` 工作进程，`--allocate-mib <MiB> <毫秒> [成功标记文件]` 用于区分“额度内成功”和“被内核拒绝”。
- 原生 API 只做加法：`ProtectedProcess` 增加任务组链重载（原两参构造保持不变），`Job.SetMemoryLimit` 允许对已持有句柄的池做动态额度调整，新增 `AssignProcessToJobObject` 导入。
- ACC-S3P01：三级链在创建时原子归属，工作进程同时属于根/池/单项；池设置 192 MiB 后 64 MiB 分配成功；根 Job 的 CPU 配置在建立池和改额度前后完全一致。
- ACC-S3P02：单项 64 MiB 上限下 96 MiB 分配被干净拒绝，即使池额度更大。
- ACC-S3P03：池 128 MiB、两个单项各 160 MiB；第一个 64 MiB 分配成功并保持存活，第二个 64 MiB 分配被池额度拒绝，第一个随后正常结束——证明池额度按整棵树累计，单项额度仍独立。
- ACC-S3P04：直接挂在池下、未加入任何单项的进程在池成员列表可见，且不会混入单项。
- ACC-S3P05：同名 `CreateJobObject` 返回既有对象（`CreatedNew=false`），第二个句柄改额度后第一个句柄能看到同一额度，不会产生第二个池。
- ACC-S3P06：关闭所有池句柄并结束创建者后，池名字无法再用 `OpenJobObject` 打开（Win32=2），但成员仍然存活并正常退出；因此“按名字恢复池并枚举成员”不可行，恢复只能依靠可信句柄或已证明的成员集合。
- ACC-S3P07：池创建者退出后，成员继续运行并可被已持有句柄的池查询，退出后成员列表清空。
- ACC-S3P08：在单项内启动的父进程再派生一个子进程后退出；子进程同时保持根/池/单项三级归属，单项成员列表包含它，池的峰值记账包含它，父进程退出后子进程正常跑完。这条覆盖了计划要求的“继承后代归属”和“父退出子存活”，而不只是创建者退出。
- 记账合同（S3.1）：`ResourceSettings` 增加 `Pool`（独立/共享，默认独立）与 `PoolMemoryLimit`；快照、`PolicyHash`、`PlanDigest` 都带上池模式/池额度/池标识。共享模式下软预算按池计费一次（同一池的已保留项与排队请求不重复计费），Git 仍独立按项计费，规划仍按项累计。`ValidateSettings`/`ValidateLaunchItem` 拒绝未知池模式、低于 1 GiB 或低于规划值的池上限、共享模式缺少池标识、独立模式携带池标识。默认仍是独立模式，UI 尚未暴露切换，因此现有启动路径行为不变。
- 账本版本（S3.3 前置）：控制账本升为 schema 2，新增 `Pools` 记录（池 ID、上限、策略哈希、批次）；版本 1 读作独立模式并就地升级，版本 3 及更高拒绝，池 ID 找不到记录、池记录重复或上限为 0 都按账本损坏停用启动与回收。旧 Worker 看到 schema 2 会按未知版本拒绝，不会强行接管池。
- 验证：`dotnet run --project tests/AgentLauncher.Tests -c Release` → `RESULT 89 passed, 0 failed`；新增 8 项 ACC-S3P01–P08 与 6 项 ACC-S3A01–A06。完整 `scripts/test.ps1` exit 0：原生/模型 89/89、集成 0 失败、WPF 155/155、原型 5/5、浏览器通过；证据 `artifacts/startup-diagnosis/vibe-s3-proto-gate.txt`（82/82 阶段）与 `vibe-s3-accounting-gate-2.txt`（89/89，本提交）、`artifacts/ui/ui-checks.json`（Dpi 120）。
- 结论与边界：嵌套归属与额度调整在本机可用，S3 可以进入第二关；恢复设计必须放弃“名字重开”，且未归属进程只能计入池额度、无法按单项核对。原型未覆盖监控进程崩溃时的两阶段记账、跨进程池恢复和真实 Agent 负载，这些留给 S3.1–S3.3。
- 生命周期（S3.2）：池由短命的 `pool-host` 辅助进程创建。辅助进程先要求 `CreateJobObject` 返回 `CreatedNew=true`（同名已存在即失败），再把自身加入池、设置并回读池额度、按需创建单项 Job 并回读额度，最后用 `pools/<id>.ready` 与 `pools/<id>.release` 标记交接句柄。监控进程在辅助进程存活期间按名字打开并缓存句柄，然后放行辅助进程退出；池对象因此不会在监视器接手前被销毁。
- 预留：共享模式同一批次只能使用一个池；`reserve` 时会话中记录池 ID、冻结的上限与策略哈希，重复预留必须完全一致，否则返回“资源池策略已变化，请撤销后重新准备。”；每个条目记录 `Pool` 与 `PoolMemoryLimit`，保存失败时新增的池记录一起回滚。
- 启动：池条目打开（或复用）池句柄，从内核重读额度，再由 `pool-host` 以 `[根, 池]` 链创建单项 Job，监视器打开后进程以 `[根, 池, 单项]` 链挂起创建；独立模式路径未改动。
- 回收：每轮刷新独立尝试重开账本中的每个池，打不开即停用启动并保留记录与预算（“资源池句柄丢失…”）；只有“没有任何非终止条目引用该池”且“内核成员列表为空”时才释放句柄并删除记录，否则保留并提示仍有存活或未归属进程。池阻塞优先于通用的“尚未归属进程”提示，避免同一事实出现两种说法。
- 记账：`ActiveBudgets` 为每个活动条目计入冻结的池额度，并为账本中尚无活动条目的池记录补一条合成预算；`CanRetire` 要求池集合为空。系统启动标识变化时按既有规则结算遗留条目并清空池记录。
- 集成证据（ACC-S3B01–B03）：B01 共享池只计费一次、内核额度 4 GiB/1 GiB、工厂小额度（池 192 MiB/单项 96 MiB）能拒绝 256 MiB 分配、`[根, 池, 单项]` 链可启动并在回执后结算；B02 直接挂在池下的进程在单项结算后仍持有池与预算，成员退出后池才释放，重启后账本健康；B03 监视器在直接成员存活时崩溃，重启后保留池记录并停用启动，成员退出后仍需人工移除记录才能恢复（名字重开的限制见 ACC-S3P06）。
- 测试夹具协议：池测试必须在 `Store.AttemptLock` 互斥量内完成 `begin`→创建进程→写回执，且互斥量跨 `await` 会触发 `ReleaseMutex` 线程归属错误，因此该区间使用同步等待；这与 RunPane 的实际持锁顺序一致。
- 验证（S3.2）：完整 `scripts/test.ps1` exit 0，原生/模型 **90/90**、集成 **0 失败**、WPF **155/155**（Dpi 120）、原型 **5/5**、浏览器检查全部通过；证据 `artifacts/startup-diagnosis/vibe-s3-pool-lifecycle-gate.txt`、`artifacts/ui/ui-checks.json`、`artifacts/integration-results.json`。期间修正两处真实竞态：池标记文件在写入过程中被读会导致“文件被占用”，监控器与测试夹具都改为可共享读并重试；池阻塞提示现在优先于通用未归属提示。
- 真实准备路径：`WorkspacePreparer` 在共享模式下为每个启动批次生成一个池标识并写入该批全部 Agent 项（Git 准备保持独立）；准备记录里的旧池标识不再复用，最终返回时按新批次重新生成，因此同一草稿重复准备不会把新批次接进旧池。App 的准入预检、队列分派与状态刷新都按条目冻结的池额度或策略池额度计费，不再把共享计划按单项上限加总。
- 启动核对：界面启动时用 `LedgerMigration.Accept` 读取控制账本，旧版本升级后接受；只要账本还保留池记录，即使全部条目已终止也会先走监控刷新，让监控器证明成员退出后再释放，避免把健康账本误报为损坏。
- 交互（S3.4）：资源模式对话框新增“内存并行方式”（独立/共享）与共享池上限输入。共享模式默认给出不低于单项 Job 上限的池额度，低于单项上限、超过 8 GiB 或低于 1 GiB 都会被拒绝并显示原因；离开自定义模式只重置数值，池选择与上限保留。池模式与上限只作用于之后的新批次，运行中的 Job 不迁移。主窗口新增内存摘要：共享模式显示池上限、单项 Job 上限、当前占用与峰值（系统未按池上报，明确写“未知”）以及账本保留的池记录数；独立模式显示每项独立保护。
- 状态合同：`Response` 增加 `Pools`（账本池记录，无记录时为 null），并纳入状态签名，客户端缓存能感知池变化；旧客户端忽略该字段，旧 Worker 不返回该字段时界面按“无池记录”显示。
- 队列移除：任务卡片“移出队列”只删除排队项，不改变任务、尝试记录或运行中的进程；ACC-B07 覆盖“排队项可移除且尝试数不变”。
- 验证（S3.4）：完整 `scripts/test.ps1` exit 0，原生/模型 90/90、集成 0 失败、WPF 166/166（Dpi 120）、原型 5/5、浏览器检查全部通过；证据：`artifacts/startup-diagnosis/vibe-s34-final-gate.txt`、`artifacts/ui/ui-checks.json`、`artifacts/integration-results.json`。ACC-S3B04 走真实准备路径：同一批次两项共用一个池、重复准备换新池、经 reserve→begin→`[根,池,单项]` 启动并结算；ACC-S3A08 覆盖账本 v1 接受、保留池强制核对、未知版本与缺计划条目失败关闭。

### 25.1 独立/共享同负载对照（S3.5，2026-11-12）

- 两项都请求 64 MiB，单项 Job 上限固定 160 MiB，CPU 根固定 3500；两模式使用同样进程拓扑，独立模式的结构父 Job 不限内存，共享模式另加池总上限。轻负载各保持 3 秒；触顶场景先证明第一项分配成功，再启动第二项（第二项不保持）。使用测试小额度，不将这些值作为生产配置建议。
- 实跑 ACC-S3P09/P10 均通过。原始四组记录（逐项退出码/拒绝/峰值/耗时，池峰值与限额，开始/结束物理及提交余量）在 `D:/temp/User/AgentLauncher-tests-c28309fc0db4400dae7b6716793d26ab/pool-comparison.json`。退出码 10 是夹具明确分配成功，0 是捕获到 OutOfMemory；不是普通 CLI 的成功码定义。

| 场景 | 模式 | 池限额 MiB | 两项分配结果 | 单项峰值 MiB | 池峰值 MiB | 两项耗时 ms |
| --- | --- | ---: | --- | --- | ---: | --- |
| 轻负载 | 独立 | — | 成功 / 成功 | 70.98 / 70.95 | — | 3100 / 3100 |
| 轻负载 | 共享 | 384 | 成功 / 成功 | 71.11 / 71.06 | 149.03 | 3109 / 3109 |
| 池触顶 | 独立 | — | 成功 / 成功 | 70.96 / 70.92 | — | 3092 / 96 |
| 池触顶 | 共享 | 128 | 成功 / 拒绝 | 70.98 / 70.38 | 148.08 | 3091 / 59 |

- 峰值按内核原值记录；触顶场景观察到峰值超过配置限额，不能据此把峰值当当前占用或“限额内使用量”。独立模式的池字段为 null，绝不把不同时间的单项峰值求和冒充池峰值。系统余量是同时受其它主机负载影响的观测值，不据差值推导内存收益。这里只证明额度约束的行为差异，不证明进程复用节省、吞吐改善或长期稳定。
- 回归只保留真实 Job 分配/拒绝、归属、根 CPU 不变、内核额度与峰值断言；删除子代理新增的重复序列化/字段默认值 P11 检查，测量 JSON 仍在两场景后保存，未删除实际行为断言。测试变更保持 unverified，需操作员审查；`task_verify` 无项目基线，未执行配置检查。
- 独立阶段提交 `020febe480533bb3490329c102d254876d9bccb3` 已推送；对应完整 GitHub gate run [`43071700940`](https://github.com/zhuguang-ZFG/agent-launcher/actions/runs/43071700940) 成功（10 分 50 秒）。该 SHA 仅包含 S3.5 测量与文档，不包含后续 S4 实现。

## 26. 本地审查、命名检查与精确 SHA CI（S4，2026-11-12）

- 任务卡新增「审查改动」，打开独立原生 WPF 窗口。窗口以 AttemptId/BatchId/WorkspaceKey 匹配 monitor/ledger 的真实计划目录；仅在计划缺失且当前项目物理键仍一致时回退项目目录，拒绝把旧尝试映射到新目录。显示完整基准与 HEAD SHA；受限宽度使用滚动布局。用户编辑任务后，不能沿用不匹配修订的基准。
- `ReviewService.CaptureAsync` 获取 Git 基准→工作区最终内容、索引和未跟踪文件。指纹绑定物理键、基准/HEAD、索引以及路径完整 SHA-256；不使用 mtime/size 冒充内容。最多 2000 个变更文件、1 GiB 流式哈希；冲突/符号链接/gitlink/类型变化明确拒绝。diff 禁用 external diff/textconv，以二进制摘要或超过 4000 行/512 KiB 的有界输出处理大变更；未跟踪默认不选，只有显式勾选才会进入提交。
- 命名检查由用户在 FormWindow 明确配置 EXE、逐项 argv、超时（1–3600 秒），保存本身不执行，也不自动读取 package scripts。执行通过 monitor reserve→Worker→原有 CPU 根与独立 Job（Git 准备预算）保护，检查与 Git 准备串行；超时/取消仅终止本次检查 Job，日志每路保留 64 KiB。结果记录运行/任务/命令身份、完整 SHA、工作区物理键、内容指纹、退出码/状态/耗时；命令成功但运行期间输入变化即 Stale。窗口监视文件变更立即将旧结果标为待刷新，检查结束后重采快照，不用旧快照误显示“当前有效”。
- 独立 `review.json` schema 1 原子保存；必需字段缺失、null、重复 ID、过量记录、未来版本和损坏文件拒绝覆盖。命名配置/结果各最多 100，受跨进程互斥保护。新合同尚未正式发布，缺工作区物理键的试制检查记录拒绝使用。
- 提交是显式文件列表与消息：取得真实 index.lock 后再次核对 HEAD/index，锁住选中文件，按已审查内容流式写 blob，构建临时索引树，以 old-HEAD CAS 发布分支；真实索引通过独立准备副本原子发布，保留未选 staged/untracked 工作。遇到并发写锁在 HEAD 变化前拒绝。遵循 pre-commit/prepare-commit-msg/commit-msg/post-commit 与 commit.gpgsign；hook 改写内容/消息时拒绝并要求重新审查。提交已发布但后续索引/post-hook失败明确报告提交 SHA，避免重复提交。推送不带 `--no-verify`、不 force；PR/推送均需要按钮与二次确认，不自动合并/清理。
- CI 同时读取完整目标 SHA 的 Actions workflows、check-runs 和 commit statuses；按 workflow/check提供者与名称取最新运行，旧 SHA 不匹配，部分/超界数据不判通过，只有实际 success 且没有等待/失败才绿色。无权限、网络、无 workflow 与未知分别显示。生产读取真实 GitHub：目标 `b65d313365bddaedd01a2104488268f276b2302f` 返回 Passed，含 run `43055929687` 与 check `130645252574`；未执行远程推送/建 PR 冒烟，以免制造用户外部副作用。
- 本机预览仅列该 Attempt Job 成员的 IPv4/IPv6 loopback TCP 监听，用户点默认浏览器打开；非本机/带凭据/非 HTTP(S) URL 拒绝。不存在 Job 明确报错，不把未知伪装成空端口。

### 26.1 回归与证据

- 根因回归：真实临时仓库证明初版持有 index.lock 时仍移动 HEAD、初版 `--no-verify` 绕过 pre-push、commit-tree 初版绕过 pre-commit（均 RED）。修复后窄 Git/Store 九项全部通过：`AgentLauncher-tests-review-45a76582e1f34ef09fedcc3779549a2f`。另发现成功命令改写输入后仍显示 Passed，新增回归先失败、完成时重采快照后通过。
- 受管窄集成 **5/5**：检查结果绑定/内容变化过期、结束时输入变化 Stale、超时收尽子孙、取消不报通过、真实 Node TCP 监听发现；隔离根 `AgentLauncher-tests-review-30bb7ee003ba457599353acee5b797fb`。原 PowerShell 监听夹具两次仅进程存活但未就绪，直接脚本可成功；改用现有已验证 Node 进程路径，未改变端口断言或延长超时。
- 真实 WPF 端到端 **9/9**：锁定目录→有界 diff→新增逐参数检查→受管执行→外部改动自动过期→仅选中文件提交→无远程 CI 未知；最终证据 `artifacts/review-window-final-d89d3852/result.json`，同目录 `profile-dialog.png`、`review-wide.png`、`review-narrow.png`。一次临时冒烟误把“不会视为当前有效”的警告文本当成功标签，截图证明实际条目已过期；限定匹配实际结果行后通过，未改产品或门禁断言。
- 新 WPF 身份回归先失败于结果无独立 WorkspaceKey；补齐模型/工厂/存储/调用方后原断言通过。WPF 窄检查 **205/205**、Dpi 120：`artifacts/queue-review-review-contract-fix-2e3f6c83/ui-checks.json`。首次完整门禁原生/集成通过，在该 WPF 断言失败后由原 runner 30秒超时终止；未调整 runner 上限。
- CI 聚合临时回归 **4/4**（不同 app 同名失败不被成功遮盖、同 app 新运行替代旧运行、外部 pending 阻止绿、部分列表拒绝）和 Core 结果/精确 SHA 策略 **2/2**。先前 skipped-only 误绿、跨 app 名称合并、部分列表误接受均由 RED→GREEN 证明修正。临时脚本完成后删除源文件；长期回归保留行为/边界检查，不保留仅文字/格式/字段拷贝的断言。
- `task_verify` 仍为 **unverified / project-or-git-baseline-unavailable**、checks=[]。新增/修改测试以及临时 runner 输入需要审查；人工绿色与远程 CI 不替代操作员基线。未改 `scripts/test.ps1`、CI policy 或原断言阈值。
- 最终完整门禁：`TEMP=D:/temp/User TMP=D:/temp/User powershell -NoProfile -File scripts/test.ps1` exit 0（332.50 秒）。原生/模型 **108/108**，集成 **0 失败**（104 条带 passed 的断言，另有诊断测量记录），WPF **205/205**、Dpi 120，原型 **5/5**，浏览器检查通过。原生根 `AgentLauncher-tests-f8b0dc47aeb74551b6ca8a9911cc23ff`；集成根 `AgentLauncher-tests-integration-bfb8013d7ac84a9f92509d7ad8374ac9`；最终文件 `artifacts/ui/ui-checks.json`、`artifacts/integration-results.json`。
- 完整候选发布：`artifacts/preview-vibe-review/AgentLauncher.App.exe`，App/Worker 及依赖同目录；UTF-8/UTF-16LE 测试标记扫描四个程序集均 CLEAN。SHA-256：App `b94c43e25b1d5a819246f4e5db13767ce85502e4da20950a9ab3fe789e662fc9`、Windows `6acf0880c3f61ac159d1b8c21e5bae7f38279d3855a4f59c5bca9a1a9f269c20`、Core `e1fd0956e90518a8c3c5d4fe05500a8edab749e4216c8ea3fcac26c7f4f41a0b`、Worker `c1dadc095fcacde281c3cbdf808443f103af7605c53e9986c57db58074836786`；清单 `artifacts/preview-vibe-review/verification.json`。未替换现有入口，未终止用户 Agent；S0.6 长时观察与生产切换仍未完成，因此 S5 按计划前置门槛保持未实现/未启用。

### 26.2 远程门禁的慢请求 EOF 补充

- `8557a943886c18c35bc451dc98533ee89992fd0f` 的 CI run `43076100797` 未通过：原生 108/108、新增 S4 集成全部通过，但两个既有 CR02 worktree 场景报告 `Unable to read beyond the end of the stream`，集成总失败 2；不能以本地绿覆盖该远程失败。
- 取证发现 monitor 的同一 3 秒 token 覆盖读完整请求、同步 Refresh/Handle 与写响应；配置/账本互斥等待消耗掉回包期限后，已完成的操作会在写回应时直接取消。隔离真实 monitor 在连接已接受后持配置文件互斥 3.5 秒，旧 Worker 稳定得到同一 EndOfStreamException；无需空闲退场或坏 JSON。这证明一条可产生远程同类 EOF 的具体边界，远程缺少服务端详细跟踪，不能证明其两次异常都只来自此处。
- 修复只拆分请求读取与回复发送各自的 3 秒时间窗；客户端 8 秒上限、资源策略、授权签名与不自动重放保持不变。相同窄回归新 Worker 通过；随后实际 worktree 准备/复用/替换拒绝/冲突检查 **7/7** 通过。证据 `artifacts/ipc-deadline-regression.json`（含旧 RED、新 GREEN 和工作区场景）；新增 RuntimeReviewChecks 断言保留在原集成入口，临时探针源码已删除。测试增量仍需操作员审查。
- 补丁后的候选目录仍为 `artifacts/preview-vibe-review`，重新生产构建与双编码标记扫描 CLEAN；本次产物替换上节候选哈希：App `80bbefe028023d269f14975adb0fa8c8577f1724a4efb54055ed78883c18c6f1`、Windows `161a96bbbffe262018bc8eb6487a6ef287603e0a1b6f2fffbe554a03e4504f29`、Core `640bd068f35ad21b6ea53eec8326e3986344979fc0e019bb94587807b76f9ae5`、Worker `11a0aa651f80a75ab2b9ef25ab2698ed1475905e23bfa220c5ec116a34edca95`。生产入口未切换。

## 27. 本机入口切换与 S0.6 准入检查（2026-11-12）

- 源码仍为 `5c04a3d7a311e1a8d73f4b6f40740eef24f7a937`；复核 `artifacts/preview-vibe-review` 四个程序集哈希与 §26.2 / `verification.json` 一致。既有完整 SHA CI 成功证据为 run `43079229748`，本轮未改产品源码、固定测试、runner 或 CI policy，未重复执行已通过的完整 gate。
- S0.5：核对没有启动器 App/Worker 存活、生产账本 16 项均为 Settled 且无保留池后，将 `D:\Desktop\多 Agent 启动器.lnk` 从旧 `artifacts/preview` 指向完整 `artifacts/preview-vibe-review`，工作目录同步，参数保持空。旧快捷方式完整备份在 `artifacts/delivery-s05-5c04a3d-20261112-073138/previous-entry.lnk`；未覆盖旧发布目录。候选仍标注稳定性观察未完成，不标稳定版。
- 实际通过快捷方式打开 WPF，核对进程路径为候选 App；界面显示真实内存不足并禁止启动，任务队列保持「队列已暂停」。仅展开队列查看后正常关闭本次窗口，未点击启动/执行队列，未启动或终止用户 Agent。关闭后 App/Worker 数为零，生产配置、账本、全部回执与 `.bak` 等 **61 个文件 SHA-256 全部不变**。
- 首次界面采集使用 `ErrorText` 定位器得到 ElementNotFound：实际无启动错误，空错误条折叠，不在 UIA 树中；改为记录实际树与截图，不重启程序、不修改产品。此为临时采集输入失配，不是应用故障。新增的内存/界面采集输入仍需操作员审查，不能替代配置验证基线。
- S0.6 **受阻，0/3 轮启动**：真实采样可用物理内存 `2,420,641,792` bytes（约 2.25 GiB）；现有实用模式每项规划 768 MiB、默认物理余量 2 GiB，双独立任务至少需 `3,758,096,384` bytes（3.50 GiB），此外仍需现场 CPU/提交余量等准入通过。没有减少规划、降低门槛、使用 fake sample 或结束现有进程来凑观测。短时打开界面不计入 30 分钟 × 3 轮；S5 前置仍未满足，保持未实现、未启用。
- 原始证据：`artifacts/delivery-s05-5c04a3d-20261112-073138/{preflight.json,surface-and-preflight.json,result.json}`，实际截图 `main-window.png`、`task-queue-stopped.png`。GitNexus 当前仅有无关的 `clideck`（落后 11 commits），本仓库 context/impact 明确不可用；未重建无关索引。LSP 无服务。
- `task_verify run` 返回 **unverified / project-or-git-baseline-unavailable，checks=[]**；operator-configured passed/failed 均无执行项。以上为实际入口/UI 与数据保全的手动证据，临时采集输入请操作员审查。本轮仅本地更新本节与交付产物，未新建提交或推送；不把文档变更宣称为新的已验证代码版本。

## 28. 快速启动转资源等待队列与空队列节能（2026-11-12）

- 新增「按资源依次启动」：先复制当前启用槽与各自 raw profile/目录/备注/资源，解析命令和物理身份；确认后一次原子导入任务并以并发 1 执行。取消不写任务，重复点击不重复保存，相同快照有等待/非终态尝试时拒绝重复导入。确认框说明已有队列也会继续，以及只有新任务副本使用独立额度；原快启配置、单项限制和活动 Job/池不修改。
- 不能只把多槽拆开：原 `WorkspacePreparer` 只在同批相同工作区多项时创建 worktree，拆项会丢失隔离。新增 trailing `forceIsolated` / `expectedWorkspaceKey`，只接受单项 isolated，并在写准备记录/Git 前核对源身份；原 default false 的准备身份保持兼容。每项实际获准后才独立准备，完成时绑定真实工作目录，后续审查/续接不会误指向源仓库。
- `TaskSchema` 升为 2；schema 1 只读迁移内存，下一次正常 Update 才持久化。新 metadata 的快照、修改失效、重复/容量/非法后项原子拒绝已有回归。新 `reserve-task` 绑定已保存的 `TaskEntryId`，monitor 自己读取任务资源并核对命令、程序指纹、物理身份、根 CPU 与并发；旧 Worker 不会把新队列请求静默按快启执行。原硬保护与软确认策略不变。
- 准备前先判资源；不足只保存等待原因，不创建 worktree/Agent。准备结束后重新获取 monitor 状态；暂停/关闭携带取消并等待当前派发，预约结果不明立即停队列，不自动补发；撤销只对确认 Pending 的项有效，已运行进程不终止。
- **已复现并修复的自身开销**：旧版打开空队列窗口时无 monitor，点击「执行队列」却生成 monitor，`red-empty-queue.json` 保存 RED；首个分歧在 `DispatchNextAsync` 的空队列判断之前调用 Refresh。修复为无等待项且无非终态尝试时停 timer，不发状态请求、不创建 monitor。新 WPF 同路径断言通过。保留活动/未知尝试核对，不从进程数猜测工具可回收。
- 窄验证（实际执行）：任务模型 **15/15**，真实 Git/worktree 准备 **12/12**，既有任务/续接与新快启 WPF **46/46**；记录 `artifacts/quick-queue-20261112-080514/focused-results.json`。旧版缺少新入口的 RED 为 `red-ui.json` / `old-missing-queue-action.png`。
- **实际资源恢复场景**：隔离测试根保留两条未领取预约模拟预算占用，真实窗口确认导入后两项都等待：0 尝试、无 worktree、无执行日志、原配置 SHA-256 不变。只撤销这两条测试预约后，两个受控 Node 程序自动先后经真实 Terminal/Worker/Job 执行，事件顺序 `start/end/start/end`，工作目录不同且均为 linked worktree；空参数、中文空格、`; &` 与末尾反斜杠保留，源仓库文件未变，两项均 exit 0 后进入 Review。`reserve-task` 的篡改参数请求被拒绝。证据 `resource-wait.json`、`recovery-result.json`、`tamper.log` 及 `confirmation.png` / `resource-waiting.png` / `serial-completed.png`。
- 采集故障单独保留：第一次驱动用了 stdout PIPE，子 monitor 继承输出导致采集进程等 EOF；确认 `blocker-ids.json` 与两条 Pending 已落盘后继续，未重发预约，改用文件接收输出。另一次 UIA 误把确认框当顶级窗口；实际为主窗口的 owned descendant，按实际控件树继续，没有重启或改产品。
- 边界：以上 fake sample 仅用于隔离的受控轻量程序，不能证明真实 Agent 内存节省或替代 S0.6 的双任务 30 分钟 × 3 轮。既有 Runner 会等全部 Job 后代排干，父进程退出不证明工具可安全回收；本轮没有杀用户工具、共享 MCP 或启用 S5。新增/修改回归与临时 runner 输入仍需操作员审查，原测试断言与 `scripts/test.ps1` / CI policy / `ResourcePolicy.cs` 未改变。
- 本地完整 `scripts/test.ps1` 首次运行：原生/模型 **114/114**，集成 **110 passed / 0 failed**；WPF 被原 runner 的 30 秒上限终止，原型/浏览器阶段当时未执行。`artifacts/ui/ui-checks.json` 为旧文件，未拿它计本轮成功。日志 `full-gate.log`（PowerShell UTF-16）。随后独立原 `--ui-check` 流程 **213/213、Dpi 120、exit 0**，新目录 `full-ui-diagnose/ui-checks.json`；未修改任何断言或超时。时间戳显示失败轮约第 22 秒进入新夹具、成功轮约第 16 秒，新夹具各约 1.6 / 1.8 秒；支持累计运行时间差异，不足以把具体主机负载当已证明根因。
- `task_verify run` 为 **unverified / project-or-git-baseline-unavailable，checks=[]**；没有 operator-configured passed/failed 项。待评审输入清单 `review-inputs.json`、差异 `test-changes.patch`、临时源归档 `verification-inputs.zip`；临时 probe 源码已从工作目录移除。固定 runner/CI 与资源策略哈希不变。
- 补执行未到达的 `node --test tests/prototype.test.cjs` **5/5** 与 `node scripts/test-browser.mjs` **5/5**，均 exit 0。顺序生产构建 `scripts/build.ps1 -Output ./artifacts/preview-quick-queue` 成功，完整 App/Worker 同目录；测试标记检查与程序集哈希落在该目录 `verification.json`。没有把本地分段绿色写成首次完整脚本通过。

## 29. monitor 队列领取原子化（2026-11-12）

- 用户指出 `reserve-task` 仅检查 `TaskBook.Attempts`、ledger 只按计划 ID 去重。已对 `82057eb` 运行两个独立 named-pipe 客户端，使用同一 TaskEntryId、不同计划 ID、共享目录、充足的隔离采样，并故意停在 StartRun 前。预期仅一次预约，实际两次都成功；另一个场景第一次预约后强制退出己方 monitor，再从同根重启，第二计划仍成功。两条回归均 RED，未启动 Agent；`artifacts/task-claim-20261112-095806/red.log` 与各隔离根保存请求响应证据。RED 回归独立提交 `cd01df3`。
- 根因是跨文件状态的提交次序假设：monitor 已持久化预约，App 尚未写任务尝试；串行处理请求不等于按逻辑队列项去重。第一处分歧在 `BindTaskAuthority` 到 ledger 插入之间缺少队列领取记录，不在 UI。修复没有添加客户端等待/重试，也没有降低共享目录、预算或其他资源保护。
- 新 `TaskReservationClaim` 与预约 Entries 在同一 ledger Save 原子发布；写失败同时移除内存中的新预约与领取。`ReplayTaskClaim` 先核对持久领取，相同计划/策略返回原尝试，其他计划 ID 拒绝。`StartRun` 已消费队列后同一预约的查询式重试仍幂等，不重新执行。
- 账本 schema 升为 3，旧 Worker 拒绝，schema 1/2 继续按旧内容读取。领取证据单独保留，不随 Revoked、终态展示裁剪或 boot rollover 删除；历史被裁剪时仍拒绝旧 EntryId。验证空 ID、重复 EntryId、复用 AttemptId 与保留数量边界；最多 1200 条，满时 fail-closed，不能删除旧领取来自动放行。无法从旧版未登记预约证明归属时阻止新领取，不伪造历史 claim。
- 修复后真实 monitor 窄检查 **5/5**：双客户端竞争、StartRun 前重启与同计划幂等、实际 File.Replace 失败后的双回滚、撤销+历史裁剪+boot 变化保留领取、旧版未登记预约拒绝。`green.log` 保存原断言通过结果；没有修改原 RED 断言来获得通过。保存故障使用仅限隔离 control.json 的文件共享锁，不修改生产权限或账本。
- 新增 `RuntimeReviewChecks` 五项行为回归及 `ResourcePolicyChecks` 领取有效性回归；既有迁移/池检查改为当前支持的 schema 合同，而不是把版本字面量 2 当永久不变。`ResourcePolicy.cs` 仅变更 LedgerMigration 的 schema/领取校验，ResourcePolicy 资源阈值与准入算法不变；scripts/test.ps1、CI policy 和原 UI timeout 不变。测试与临时 runner 输入仍需操作员审查。
- `task_verify run`：**unverified / project-or-git-baseline-unavailable，checks=[]**；手动红绿与完整 gate 结果不能替代 operator-configured 基线。未声称任务执行最终必定成功；本修复证明的是同一队列意图至多归属一个预约，不是跨崩溃的任务业务 exactly-once。
- 完整本地脚本原生/模型 **115/115**、集成 **115/115**（含上述五条）通过，随后 runner 报 WPF 30 秒 timeout。与上轮不同，本轮新报告明确失败在第 209 条 `ACC-S3A08 an unknown ledger version still fails closed`：夹具把已支持的 schema 3 当未来版本。否定“只是累计慢”的假设，仅将该输入改为 `int.MaxValue`，原拒绝断言与超时不变；随后同套 WPF **213/213、Dpi 120、exit 0、20.67 秒**。首轮报告 `full-gate-ui-failure.json`，修正报告 `ui-final/ui-checks.json`；不把原完整脚本失败隐去。
- 未执行到的原型与浏览器原检查补跑，均 **5/5、exit 0**。顺序生产发布 `artifacts/preview-task-claim/` 成功，App/Worker 与依赖同目录；四程序集双编码测试标记扫描 CLEAN，哈希与完整验证分类见该目录 `verification.json`。未覆盖旧生产包，未启动或终止真实 Agent；临时 probe 源码已归档 `runner-inputs.zip` 后删除。

## 30. 领取边界与队列恢复完整修复（2026-11-12）

- 预期：空 / 已保留的 AttemptId 在预约写入前拒绝；schema 3 缺失执行权威字段时停止而非当作空历史；预约先于 TaskRun 的中断不让旧队首持续重试。旧行为由 `artifacts/claim-review-20261112-112647/` 的真实 monitor 与 WPF 证据确认，本轮没有把历史 checkpoint 当成测试结果。
- 新增永久回归并先执行旧实现：真实 monitor 四项 **0/4**，分别命中空 AttemptId、裁剪后 AttemptId 被另一队列项复用、schema 3 缺字段仍健康、普通 reserve 复活旧身份；原始 JSON 检查 **1 通过 / 1 失败**，缺字段失败而既有身份校验仍通过；WPF **12 通过后失败于准备前暂停**。RED 提交 `0b8efb7`，原始输出 `artifacts/complete-repair-20261112-120124/red-{claims,json,wpf}.log`。
- 第一处分歧分别是 Worker 入账前未校验保留身份、JSON 对缺省成员执行初始化器、App 未同步永久领取后的恢复语义。最小修复：新预约在修改池/Entries/TaskClaims 前拒绝无效或复用身份；Ledger 用反序列化字段存在标记区分 schema 3 缺失与 legacy 1/2 缺省，成功迁移清除标记；App 在准备前及预约拒绝后后台读取并校验持久领取，发现旧领取或账本不可读即停队列，提供原尝试与明确人工恢复步骤。成功撤销但未写 TaskRun 也停队列，不删除领取、不自动重新入队。
- 同一测试输入与窄执行器：修复后真实 monitor **4/4**、原始 JSON **2/2**、WPF **20/20**。三个测试文件与两个执行器 DLL 的 RED/GREEN 哈希相同，记录在 `red-results.json` / `green-results.json`。WPF 同时覆盖原资源不足等待、不准备目录、空队列不拉起 monitor，以及明确移出/再入队的新身份能预约。
- 编译故障透明保留：首次测试发布因新用例引用了 `RunPoolAccounting` 作用域外的 `temp`（CS8801）失败，仅为两用例补独立临时根；不是业务 RED。首次 Worker 发布因新增循环变量 `claim` 与稍后变量同名（CS0136）失败，仅改为 `retainedClaim`；未动测试断言。
- 根因分类：跨层合同传播遗漏 + 覆盖只检查读取端。防复发已经落在真实请求入口、原始 JSON 与真实队列恢复回归，而非仅直接构造内存对象。未扩大资源门槛、保留上限或 UI 30 秒超时；`scripts/test.ps1` 和 CI policy 未改。
- 新增/修改的三项测试文件与临时执行器仍需操作员审查，清单 `regression-review.json`；`task_verify run` 返回 **unverified / project-or-git-baseline-unavailable，checks=[]**。手动红绿不代替 operator-configured 验证基线。
- 本次原样完整 `scripts/test.ps1` **exit 0**：原生/模型 **117/117**、集成 **119/119**、WPF **225/225，Dpi 120**、原型 **5/5**、浏览器 **5/5**，总耗时 **434.68 秒**。这次不是分段拼接绿色；原 30 秒 UI runner 和各断言保持。完整日志 `full-gate.log`，当前报告已归档到本轮证据目录。
- 真实 WPF 使用审查时相同的隔离持久边界复核：两项保留、队列自动暂停，按钮回到「执行队列」，提示原尝试 `Revoked` 及明确移出/再入队步骤；`actual-ui.json` / `actual-queue-recovery.png`，exit 0。未执行 Agent，未访问生产数据。
- S1–S4 已实现功能通过本次完整 gate；S4.6 的 `ReviewChecks.cs` 已有真实 Node loopback listener + 受管 Job + `PreviewPortsAsync` 回归，并非缺失实现。S3.5 的本次 `pool-comparison.json` 从 native 根归档，仍只证明额度行为，不宣传物理内存节省或长期稳定。
- S0.6 真实双任务准入为 **Deny**（可用 `3,624,382,464` bytes < 所需 `3,758,096,384` bytes，CPU 27.34%，提交余量 `6,818,865,152` bytes），0/3 轮。用户表单明确选择先交付已验证修复、S0.6/S5 保留受阻，不安装新运行时；长时实验草稿停止并归档 `deferred-soak-draft.zip`，未编译、未执行，不是发布交付物，活动源码已删除。
- 工具版本不是冻结常量：本轮初始直接查询 Omp 为 18.18.9、Bun 为 1.3.14，完整 gate 的后续能力探测已观察到 Omp 18.19.0。没有替换或安装它们；历史 18.18.8 原生模块路径不再存在，不将旧 SDK/原生探针能力套用于新版本。S5 宿主仍未实现、未启用，也不宣称整个 spec 已完成。


## 31. 六项审查与窗格内存拒绝修复（2026-11-11，America/Adak）

### 改动与边界

- 现场 HEAD 为 `198c852`，main 有四个未提交的部分修复。经用户“继续”授权，在 `fix/review-and-cmd-overflow-20261111` 先提交 `9afe2d4` 保全，再补缺口；没有 reset、丢弃现场修改或 push。现场 WIP 产品代码先于本轮测试，不把它描述为 TDD。
- `LoadLedger` 区分首次启动与主 control.json 丢失但 .bak 存在；后者维持损坏阻止态，不写回空账本。QQ-C11 使用真实 IPC 预约任务、撤销形成含领取的备份、终止己方隔离 monitor、删除测试主账本、重启，检查不能重新领取且备份字节不变。
- 预约前精确序列化投影的活动 Entries、Pools 与 TaskClaims，额外保留 512 KiB 固定余量和每活动项 64 KiB 生命周期余量，超过 6 MiB 整批拒绝，发生在池创建/领取写入前。Save 限制诊断 Detail 长度，保留 8 MiB 读取上限；活动记录不裁剪。文件发布失败回滚 Entries/Pools/Claim，并同步释放新增池 observer；Refresh 写失败则锁定阻止态，下一 tick 不会清掉错误。
- Refresh 用活动 ID 集合清理 Job 所有权，不再对可能刚被裁剪的记录执行 Single。终态裁剪前原子保存 `attempts/<id>/outcome.json`（schema、AttemptId、计划摘要及完整终态），再删除展示历史。只读 `attempt` IPC 优先读当前权威记录，其次校验身份和摘要的归档；不信任旧 .bak 或无身份 receipt。UI 另核对 AttemptId、BatchId、TaskId、WorkspaceKey；缺失保持未确认，不凭空判完成。旧版本已裁剪且没有身份归档的尝试仍需人工核对。
- `LoadAuthority` 在主配置丢失但存在备份时拒绝默认策略，status/reserve/waiver 使用同一阻止原因；reserve-task 同样持配置互斥。自审再发现已预约的 begin/resume 可在主配置丢失后激活，新增回归先红（旧 begin 实际给出授权），再在两种激活请求前复用同一配置阻止检查。真正没有配置/备份的新安装保留原首次启动兼容行为。
- UI 预检为整批共享项构造同一个临时池 ID 与冻结池上限，池只计费一次；ACC-L02 比较前后台实际 BudgetOf/Evaluate 结果。另为规划估算之和大于共享池硬限额的计划增加明确风险确认（保守模式拒绝），begin 不重复消耗授权。不会调整运行中的 Job。

### “cmd 子窗口超限退出”的证据与修法

- 不能把用户这次原始退出事件宣称已复现或完全归因；没有该次进程/回执记录。本轮确认的机制是：`JOB_OBJECT_LIMIT_JOB_MEMORY`（Native.cs）拒绝超额提交，不是“内核必然杀掉整个 Job”；CLI 可能自行报错/快速失败。旧实现只比较峰值与退出码，不能证明具体内存拒绝；普通 ReadLine 还可能消费 CLI 遗留换行而立即退出，早期异常直接从 Main 返回。
- 新 `JobMemoryWatch` 通过 Job completion port 观察内核消息 9/10，记录拒绝进程 PID，不修改内存限制、不结束任何进程。每 pane runner 持有自己单项 Job 的 observer，内存证据随 Started/Exited/Finished 回执保存；共享池由 monitor 观察并保存带 PoolId/PID 的通知。只有能匹配主进程或当前 Job 成员时才归属该窗格，否则明确显示共享池事件且归属未确认。通知缺失/峰值接近不被当作确定死因。
- 异常或已观察到内存拒绝的交互窗格改为按 F12 明确关闭，避免普通缓冲 Enter 自动消失；正常退出仍按 Enter。早期创建失败由唯一 claim 所有者保存实际错误，重复 runner 不得覆盖原尝试 receipt。资源/句柄通过 finally 释放，工作目录保留。INTEGRATION_TESTS 不等待人工按键；F12 的实际 Terminal 人工交互本轮未验证。
- 原生小额度夹具（96 MiB）用 VirtualAlloc 触发内核拒绝，证明通知 PID 对应失败进程、同胞 Job 进程仍存活、原上限未变；runner 隔离夹具同样验证明确内核证据进入 receipt。没有启动 16 个真实 Agent，没有耗尽系统内存；CPU 默认仍 3500（35%），无 KILL_ON_JOB_CLOSE、无全局 Terminal 设置修改。
- TerminalCommands.cs 直接提交 Worker pane + GUID，并在提交前拒绝超长命令；这不能解释已经运行中途的退出。历史 15/16 部分启动仍未复现/归因，不因这些测试而关闭。共享池本身仍存在共享额度竞争，不能承诺其它程序的后续分配永不被拒绝；实现不会因一个事件终止整池或同胞。

### 测试纪律与自审

- 新缺口先执行失败回归：缺配置退默认、容量预留、持久结果查询、Job 消息观察、任务身份、runner 回执、共享池预警、异常保留、observer 回滚及重复 runner 回执所有权均有 RED → GREEN 日志。WIP 已修路径补测试；另增加归档重建与 Refresh 写失败持续阻止的后补覆盖。
- 自审抓到两项新生命周期缺口并补红绿：池发布失败留下 observer、早期失败回执误覆盖已有 claim 的原 runner；均已修复。另两次测试夹具/产物问题明确保留：池工厂在反射 monitor 下曾误用 Tests.exe（改为明确 Store.WorkerPath）；仅更新 Tests 未更新 Worker 的定向目录出现旧重复回执行为，统一重发布后再测，未修改断言掩盖失败。
- 初次完整门禁因新 QQ-C11 夹具默认等待 healthy 而失败；该场景预期就是 blocked，改为原夹具的 requireHealthy:false，保留拒绝、备份字节与不写主文件断言。未放宽产品保护、原断言、UI 30 秒超时或 CI 阈值。
- 证据归档目录：`artifacts/review-cmd-20261111/`，包括各轮红灯、中途失败与定向绿色。运行期状态仅在隔离 AgentLauncher-tests-* 根；没有切换桌面快捷方式或覆盖正在使用的预览包。

- 第二次完整门禁原生 117/117、当时定向 14/14、集成 120/120 通过，WPF 30 秒超时且新报告明确失败于 ACC-L02 拖入 16 目录断言，不能归咎仅仅运行慢。独立原 30 秒 UI 取证证实 Root 为 D:\Temp\User，而物理规范化为 D:\temp\User：busy/readOnly/checkingDrop 全 false，16 项均正确保存规范路径。仅把夹具预期改成 PhysicalDirectory.Resolve(Store.Root).Path，数量、原子性与第 17 项整批拒绝断言不变；未修改产品拖放逻辑。失败报告与诊断为 gate-ui-failed.json、ui-drop-state-red.json。

### 本轮最终验证（America/Adak 2026-11-11）

- 原入口 `powershell -NoProfile -File scripts/test.ps1 -SkipBrowser` 最终 **exit 0，464.99 秒**，于 **20:51:34 America/Adak** 完成。原生/模型 **117/117**、新增隔离修复 **16/16**、真实 IPC/进程/既有双窗格集成 **120/120**、真实 WPF 检查 **229/229、Dpi 120**、原型 **5/5**。WPF 保持原 30 秒上限；不是把分段结果拼成完整脚本绿色。
- 最终匹配 Worker 额外运行 `artifacts/test-runtime/AgentLauncher.Tests.exe --admission-integration`，fresh AgentLauncher-tests-* 根，**16/16、exit 0**。未把 16 条准入断言说成启动 16 个真实 Agent。
- 生产 `dotnet build src/AgentLauncher.Worker/AgentLauncher.Worker.csproj -c Release --nologo -m:1` 与 App 同命令均 **exit 0、0 警告、0 错误**。`git diff --check` 无空白错误（只出现仓库既有换行转换提示）。未发布覆盖生产入口。
- 最终证据为 `gate-final-pass.log`、`focused-final16.json`、`integration-final120.json`、`ui-final.json`、`admission-current16.json`；早期失败及红灯继续保留。新增激活权威红灯单独为 `activation-red.log/.json`。
- 自审覆盖失主文件/权威配置、容量拒绝与完整回滚、裁剪与 Job/observer/cache 所有权、持久结果身份、重复 runner 的 receipt 所有权与写失败后的持续阻止。未发现这些修复路径有新的 fail-open；未独立运行第二个代码审查代理。
- **未验证/仍保留**：浏览器阶段明确 SkipBrowser；没有 16 个真实 Agent 压测、OOM 实验、用户本次原始退出复现、生产 Windows Terminal 中 F12 人工交互或 S0.6 长时观察。通知为 best-effort，不能把无通知当作没有内存压力；共享池通知无法确认成员归属时明确保留未确认。历史 15/16 偶发部分启动仍未归因；旧版已裁剪且没有身份归档的结果仍需人工核对。没有改全局 Terminal、CPU 默认或内存封顶，没有切换快捷方式或 push。

## 32. 当前 main 代码审查与隔离复现（2026-11-12，Asia/Shanghai）

本节保留修复前审查结果；CR-N01～N04 的后续实现与验证见第 33 节。

- 审查源码为 `8746cdee47386e8a82def09bffb33ce526d5a3c8`，本轮没有修改产品实现或永久测试。审查探针单独放在 `artifacts/code-review-20261112-153855/probe/`，引用本轮测试构建的程序集；实际进程仅为有 Job 保护、3 秒自退出的 Tests 夹具，不执行真实 Agent，不读写生产配置。
- **P1 / CR-N01，尚未修复**：`PersistRefresh` 实际写入失败后已锁定 `ledgerBroken`、status 返回 `Ok=false`，但 `resume` 只核对 Creating、epoch、digest 及配置可读性，仍返回允许恢复线程。探针用另一条已完成记录触发真实 File.Replace 共享冲突，同时保留创建者 attempt gate，得到 `ledgerBroken=true/statusOk=false/resumeOk=true`。恢复线程入口须遵守持久化故障阻止态。
- **P1 / CR-N02，尚未修复**：任务主文件丢失且 `.bak` 存在时，`TaskStore.Load` 返回空任务簿；随后两次正常 Update 会覆盖原备份。隔离复现原任务数 1、丢失后读取 0，最终 `backupPreserved=false`。应拒绝新写入并要求核对恢复，不能把已有备份的缺失主文件当新安装。
- **P1 / CR-N03，尚未修复**：任务来源的 FailedBeforeStart/Revoked 记录仍显示普通「仅重试此项」；该路径走普通 reserve，没有追加 TaskRun。隔离探针复用相同路径并运行受保护的 3 秒夹具，实际重试存活、任务簿非终态数 0、MaxRunning=1，下一队列任务仍预约成功，ledger 活动项变为 2。任务卡与并发核对均遗漏该重试；应保留任务尝试关联或要求明确重新入队。
- **P2 / CR-N04，尚未修复**：Node shim 的 ProgramFingerprint 只覆盖 node.exe，不包含入口 JS。使用合法 npm shim 在原路径替换 agent.js 后，Commands.Resolve 的命令和指纹都不变，真实 Worker Handle 入口仍接受冻结队列预约。应核对适配器所代表的 CLI 身份和版本，避免把运行时未变当作 CLI 未升级。
- 原样完整 `scripts/test.ps1`：原生/模型 **117/117**、修复检查 **16/16**、集成 **120/120**；随后 **WPF 30 秒超时，完整 gate 失败**。当时公共 `artifacts/ui/ui-checks.json` 为 14:51:33 的旧报告，不能计入本轮通过证据，另存为 `ui-preexisting-report.json`。本轮超时原因未完成归因。
- 保持原 30 秒限制、独立新隔离根与报告目录后，WPF 单独重跑 **229/229，Dpi 120，exit 0，26.86 秒**。未到达的原型、浏览器阶段补跑，各 **5/5、exit 0**；这些分段结果不改写首轮完整脚本失败的事实。
- 本轮四项行为证据为 `probe-results.json`，完整脚本输出 `gate.log`，单独 WPF 结果 `ui-rerun/ui-checks.json` / `runner.json`；审查报告与哈希清单同目录。四个探针均重现缺陷，尚未有对应产品修复或修复后绿色回归。
- 用户原列六项仍开放：原始 cmd 中途退出与历史 15/16 部分启动没有复现/归因；无退出证明继续保留占用；Job 通知和共享池争用的边界不变；无身份归档的旧任务继续人工核对；未发布新包或切换快捷方式，F12 生产交互未验收；S0.6、S5 及完整发布验收未完成。自适应轮询、短帧、发现缓存已有实现；过时文档和审查订阅属于维护事项。

## 33. CR-N01～N04 修复与发布候选（2026-11-12，Asia/Shanghai）

- 本轮基于 `8746cdee47386e8a82def09bffb33ce526d5a3c8` 继续，修改尚未提交。先将四个隔离复现改为永久回归：原生旧行为 **118 通过 / 4 失败**，Worker 旧行为 **16 通过 / 7 失败**；`artifacts/repair-20261112-163545/native-red.log`、`worker-red.log/.json` 保留。测试入口、资源策略、CI、原 30 秒 WPF 上限没有修改。
- **CR-N01 已修复**：begin / resume 共用持久化与恢复阻止检查；实际 File.Replace 失败后，Pending / Creating 授权与磁盘字节保持，下一 tick 仍拒绝激活。恢复健康冻结授权不重做软准入确认；新增回归实际触发共享冲突并由另一线程 Refresh，保留真实创建者 mutex 所有权。
- **CR-N02 已修复**：任务主文件缺失而备份仍在时，Load / Update 都拒绝，修改回调不执行；不新建主文件或覆盖备份，说明需人工核对恢复。确实首次使用仍可新建，显式恢复有效主文件后正常读取。原生与 WPF 均验证备份字节与队列停用。
- **CR-N03 已修复**：Core 的纯来源策略沿 RetryOf 祖先核对持久领取、任务尝试及 SourceSlot 对应任务；祖先缺失、循环或任务记录不可读均不能证明可普通重试。App 隐藏任务来源的普通重试并提示重新入队，执行时再次读任务元数据；Worker 直接请求同样拒绝。保留原普通快启重试兼容性与 R05 集成断言，不引入第二套任务尝试登记流程。
- **CR-N04 已修复**：所有程序身份调用改用完整 CommandSpec；Node 摘要包含运行时、入口内容、最近包 manifest 内容及路径，原 exe 指纹兼容，原始参数与会话值保持独立。快启缓存按入口区分，版本核对前后验证身份。旧运行时专用摘要拒绝，不自动改写旧任务/会话。正文合同记明 8 层、64 MiB / 1 MiB 上限与非原子外部升级边界。
- 自审进一步发现预约后升级仍可激活；追加 begin / resume 两项回归先得 **23 通过 / 2 失败**（`activation-red.log/.json`），再在持久任务领取的激活路径从 TaskRun 或原队列项核对冻结程序。缺证据、错身份或升级均在创建 Job / 返回恢复授权之前拒绝，原预约保留。最终定向 Worker **28/28**（`worker-final.log/.json`），原生 **122/122**（`native-green.log`）。一个新增软准入夹具曾反射不存在的 pressure 字段而失败，已改为真实 PressureTracker.Observe 进入压力态；该夹具错误不是业务红灯。
- WPF 前几次原 30 秒超时，通过新增 test-only 阶段记录定位：真实空闲物理内存当时仅 `669256 KiB`，队列按设计因压力暂停，未到达程序身份拒绝分支。任务 UI 组改用既有隔离 FAKE_SAMPLE 钩子并 finally 恢复，保留真实资源检查与生产阈值；修复后定向 **234/234、exit 0、24.85 秒**。没有把此前超时报告计为通过。
- 第一次本轮完整 gate **exit 1、468.47 秒**：原生 **122/122**、Worker **28/28**、集成 **120/120** 均通过，WPF 因深色下拉当前项容器未生成抛 ArgumentNullException。新的 `ui-exception.json` 揭示原先会仅显示错误并最终超时的夹具异常；公共 ui-checks 是 14:51:33 旧报告，不计本轮结果。失败证据为 `gate-final.log`、`gate-runner.json`、`gate-ui-exception.json`、`gate-ui-progress.json`。
- 仅修正 test-only 下拉检查：等待当前附着行的容器生成，异步发现替换行时重新取得控件；增加容器生成断言，原深色背景颜色断言不变。定向 **235/235、exit 0、25.20 秒**（`ui-container/` 及 runner）。UI 异常输出具体堆栈并 exit 1，阶段进度文件让超时可诊断；这些代码全部受 INTEGRATION_TESTS 条件控制。
- 第二次完整 gate **exit 1、590.35 秒**：原生 **122/122**、Worker **28/28**、集成 **120/120** 通过；WPF 在原 30 秒到期被结束，阶段记录已通过 234 项，但尚未完成最后检查与报告，不能计为通过。保留 `gate-rerun.log`、`gate-rerun-runner.json`、`rerun-ui-progress.json`；没有套用先前定向绿色。
- 后续减少无关工作：test-only 进度文件最多每 500 ms 写一次并保留最终记录；运行记录只在实际重试资格变化时刷新，不再因任务 Detail/Revision 的无关更新重建全部会话控件。原有断言、采样保护与 30 秒上限保持。定向 **235/235、exit 0、25.69 秒**（`ui-render/`、`ui-render-runner.json`）。完整最终重跑与匹配生产包结果见后续补充；单段通过不替代完整 gate。
- **最终原样完整 `scripts/test.ps1` 通过**：2026-11-12 **17:28:37～17:36:48 Asia/Shanghai**，**exit 0、490.93 秒**。原生/模型 **122/122**、修复检查 **28/28**、真实 IPC/进程/Terminal 集成 **120/120**、WPF **235/235（Dpi 120）**、原型 **5/5**、浏览器 **5/5**。保留原 30 秒 WPF runner，未跳过阶段；`gate-complete.log`、`gate-complete-runner.json`、`complete-repair-results.json`、`complete-integration-results.json`、`complete-ui.json` 为本轮新证据。集成数组中元数据不计为通过用例。
- 匹配生产包通过原 `scripts/build.ps1 -Output ./artifacts/preview-review-20261112` 顺序发布 App / Worker，**exit 0**；生产四程序集 UTF-8 / UTF-16LE 测试钩子标记扫描 CLEAN。独立 `invalid-command` Worker 冒烟按预期 **exit 2**，不启动 monitor / Agent。包内 `verification.json` 记录基准 commit、未提交源码文件哈希、程序集哈希、SDK、依赖和完整验证分类；不把基准 commit 冒充修改后提交。
- 分发 ZIP `artifacts/preview-review-20261112.zip` **15 个文件**，解读归档逐项核对长度及 SHA-256 全部一致；ZIP SHA-256 为 `34D70911909B3112B7F76693FA45EFC49679157BF7F16372A851B3ED5AA0681D`。构建、生产验证、交付清单为 `release-build.log`、`release-verify.log`、`delivery.json`。这是需要 .NET 8 Desktop Runtime x64 的技术预览包，未安装新运行时，也不是安装器或完整产品发布验收。
- **未切换桌面入口或覆盖旧包**：只读核对生产根 `%LOCALAPPDATA%\\AgentLauncher` 时，旧 `preview-claim-recovery` Worker 仍在运行，账本有一条 Exited 非终态、无池。没有为版本切换强行终止进程、删除账本或释放占用。新目录和压缩包均已具备，待旧尝试核对及旧 Worker 正常退出后再切换；当前快捷方式不能视为已使用新修复。源码未 commit / push，未启用自动审查订阅或声称独立审查通过。
- 保留的运行边界：原始 cmd 退出与历史 15/16 部分启动未复现/归因；未知退出、丢失 Job/池与无归档旧任务继续人工核对。Job 通知仍为 best-effort、共享额度竞争仍可能拒绝分配。F12 生产人工交互、S0.6 长时真实负载、S5 多会话宿主、两小时真实中文 CLI / 无障碍 / 干净环境分发仍未验收。既有记录第 30 节的 S0.6/S5 延后决定继续保留，不通过放宽资源门槛完成实验。

## 34. 九项可用性改进与本轮验证（2026-11-12，Asia/Shanghai）

用户同意完整处理九项使用体验问题。本轮源码未提交，基准仍为 `8746cdee47386e8a82def09bffb33ce526d5a3c8`；保留第 33 节修复。证据目录为 `artifacts/usability-20261112-181041/`，最终测试构建为其 `runtime-acceptance/`；定向结果不替代完整发布验收。

- **UX01 归档**：活动最多 200 张，总保留卡片最多 1200 张；归档保留身份、依赖和尝试，释放活动名额。排队、运行或非终态启动记录拒绝归档；活动/归档/全部可筛选搜索，恢复不入队。
- **UX02 任务交接**：新建/编辑和复制窗口明确说明内容尚未发送。预览项目、说明和验收项，再复制到对应终端；不导出启动参数，不声称 Agent 已收到。
- **UX03 关闭/继续**：启用队列即使不在创建进程，也会提示等待项数量与关闭后果；取消关闭保持原队列。重开显示暂停横幅，继续读取持久批次范围；明确执行整个队列才清除范围。修正执行按钮新增异步保存后的状态转换，回归等待真实完成。
- **UX04 恢复**：提供只读证据、备份预览、显式恢复、核对占用和引导重新入队。恢复同一份已核对 hash 的备份字节，保留原文件与 `.bak`，原子发布并推进修订；可验证终态后才生成新意图，保留顺序与领取，仍暂停。孤立 Finished、损坏记录、无身份归档不授权重放。续接项提示回到会话核对流程。Terminal 入口现在于目录准备/预约/消费等待项之前核对，缺失时保留原意图并给出设置指引。
- **UX05 执行范围**：快启默认仅本批次并发 1，全局并发保留，范围与导入同事务保存；明确选择整个队列才改全局并发为 1。续接对话框默认只执行本任务，也可明确扩展整个队列，避免被旧批次范围遗漏。
- **UX06/07 暂缓和顺序**：未领取项可暂缓，保留 EntryId/Sequence/冻结设置，后续独立任务可以检查；恢复保留原顺序。Worker 独立拒绝暂缓项的 reserve/begin。列表使用真实优先级/FIFO，范围外、暂缓明确标注；序号只计算当前范围。撤销后提示统一指向恢复页，未知结果说明不会被重绘覆盖。
- **UX08 历史**：任务/全局历史搜索保留的 TaskRun、控制记录、领取、已核验归档，突破近期 12 条展示限制。最多扫描 10000 个目录并说明截断。中文状态、筛选列表导出、单条诊断复制/导出均具备；参数、会话 ID、环境不导出，诊断提示工作目录信息。损坏控制记录或回执不遮蔽健康身份归档，也不授权释放占用。
- **UX09 会话帮助**：登记/续接提供各 CLI 指引，说明任务 ID 不等于会话 ID、不要粘贴整条命令。原版本/程序身份/工作目录核对继续执行。`tasks.json` 升至 schema 3，旧版本拒绝；schema 1/2 仅在内存迁移。

最终验证与限制：

| 检查 | 实测 | 证据 |
| --- | --- | --- |
| 原生/模型/存储 | **131 通过 / 0 失败，exit 0** | `native-acceptance-rerun.log/.json` |
| Worker 修复 | **29 通过 / 0 失败，exit 0**，含暂缓预约/激活拒绝 | `repairs-acceptance.log/.json`、`native-repair-acceptance.json` |
| 可用性 + 快速入队 WPF | **42 通过 / 0 失败，exit 0，10.54 秒，Dpi 120**，保持 30 秒上限 | `ui-acceptance/usability-checks.json`、`ui-acceptance-runner.json` |
| IPC/进程/共享池集成 | **117 通过 / 3 失败，exit 1，406.86 秒** | `integration-final.log/.json`、`integration-runner.json` |
| 完整 WPF | **100 项通过后失败，exit 1，17.56 秒**；未到达后续组 | `ui-full-acceptance/`、对应 runner |
| 原型模型 | **5 通过 / 0 失败，exit 0** | `prototype-final.log` |
| 浏览器 | **失败**，两次停于 `CDP timeout: Page.enable`；没有浏览器通过报告 | `browser-rerun.log` |
| 生产构建 | 配套 App / Worker 顺序发布 **exit 0**，未编入测试钩子 | `release-build.log`、`release-verification.json` |

- 集成三项失败为真实 Pi 读取 `C:\Users\zhugu\.pi` 遇到 `EPERM lstat`，以及找不到当前账户可用的 Windows Terminal 导致两个真实开窗场景失败。完整 WPF 在 ACC-B08 真实 pane 派发断言失败，保存的队列 Detail 明确为 Terminal 不可用，原等待项保留且未创建尝试。未改断言、资源阈值或超时来绕过这些前提；真实续接派发等后续 WPF 用例本轮未完成。续接执行范围已实现并编译，仍需在具备 Terminal 的环境补验。
- 首次完整脚本重跑只到集成：130 原生、28 修复、112 集成通过，8 集成失败；保留 `gate-third.log`。首个 Terminal 测试先预约再发现无入口，遗留 Pending 导致后续软预算及配置清理级联失败。仅把两个 Terminal 集成夹具的入口前提检查移到预约之前；再次隔离运行共享池和 busy-pipe 等均通过，缩小为上述 3 项外部前提失败，没有削弱 Worker 的稳定槽与确认授权检查。
- 早期界面红灯包括未等待执行按钮的异步保存，以及撤销提示仍使用旧人工步骤；分别修正执行时序和恢复提示后通过。`ui-first` / `ui-second` / `ui-verified` 保留失败证据。一次原生测试误用测试构建且未给隔离根，因缺失 `test-root.txt` 退出；随后明确隔离根重跑为 131/131，不把前次计为通过。一次发布公共测试目录被旧测试监控器锁住 DLL，改用全新测试目录，无强制结束生产进程。
- SDK 8.0.419、Node 24.14.0。当前受限环境不能读取原用户 NuGet.Config；仅构建进程将 APPDATA 指向证据下 `build-appdata`，使用无外部源 NuGet 配置；未修改系统配置。运行和最终集成不携带该构建覆盖。脚本执行策略仅对此次 PowerShell 进程使用 Bypass。
- 截图为真实 WPF：`ui-acceptance/queue.png`、`queue-order.png`、`recovery.png`、`handoff.png`、`history.png`；已查看版面和可见操作，不替代 DPI/主题全矩阵与屏幕阅读器人工验收。README、C6/C8 合同与验收表同步新语义。
- 交付目录 `artifacts/preview-usability-20261112/` 与同名 ZIP，为需要 **.NET 8 Desktop Runtime x64** 的技术预览，App/Worker/DLL 须整体使用。校验清单记录当前未提交源码、程序集与测试证据哈希，ZIP 逐项长度与 SHA-256 核对。**未切换桌面入口、未覆盖第 33 节旧包、未安装外部 CLI/Terminal、未 commit/push。**
- 本轮没有通过完整 `scripts/test.ps1`；不能使用第 33 节旧绿色结果冒充本轮通过。用户原列的 cmd/15/16 原事件归因、极端崩溃的退出证明、Job 通知与共享池竞争、无身份旧记录、F12 人工交互、S0.6/S5、两小时真实中文 CLI、无障碍与干净环境分发仍按原边界开放。未知结果保持占用，未自动删除证据或重放。

## 35. `69bc500` 完整 CI 与本机验收边界（2026-11-12）

- GitHub Actions run `43222175697` 对 commit `69bc50053fde0332fcc010ed011fbecb9950ca30` 于 2026-11-12 13:07:56 UTC 完成，结论 `success`。真实 Pi CLI 安装步骤和完整 `scripts/test.ps1` 门禁均通过；完整门禁覆盖 model、integration、WPF、prototype、browser，集成结果为 0 失败。该结果关闭此提交的 CI 门禁，不覆盖下方需要本机或人工操作的验收项。
- 本机同一源码的完整门禁仍因 Pi 配置目录 `EPERM`、Windows Terminal 不可用及 Edge CDP `Page.enable` 超时而失败；此前 117/120 是本机运行结果，不能覆盖 CI 成功，也不能反过来冒充本机全绿。CI 完整门禁已通过，说明这些限制没有阻断当前提交的 CI 验收。
- `scripts/build.ps1` 已将同一源码的 App/Worker 顺序构建到 `artifacts/preview-final-69bc500/`，正式程序集测试钩子扫描为 CLEAN。它是并列技术预览产物，不代表已经安装或切换入口。
- 复查本机当前空闲物理内存为 1,824,632 KiB（1,868,423,168 bytes），低于 S0.6 双任务准入线 3,758,096,384 bytes；没有降低门槛或开始长时负载。S0.6 尚未执行，S5 尚未实现或验收。
- 桌面快捷方式 `D:\Desktop\多 Agent 启动器.lnk` 仍指向 `D:\Users\pj\agent-launcher\artifacts\preview-claim-recovery\AgentLauncher.App.exe`；本轮未切换。当前工作区写入范围不包含 `D:\Desktop`，因此此入口变更无法在本会话内执行。
- 原始 cmd 中途退出及历史 15/16 部分启动仍未复现和归因；极端崩溃后不能证明退出时继续 fail-closed；Job 内存通知仍为 best-effort，共享池竞争仍可能拒绝后续分配；无身份归档的旧裁剪任务仍须人工核对。生产 Terminal F12、两小时真实中文 CLI、无障碍及干净环境分发仍未人工验收。CI 变绿不关闭这些独立风险。

## 36. 16 窗格回归与 CI 诊断补充（2026-11-12）

- `58c5e5e` 的 run `43225867595` 集成 0 失败，但 WPF 在固定总计 30 秒上限超时；原有 `ui-progress.json` 未被 runner 使用。runner 现保留 30 秒无进度门槛，并增加 90 秒总硬上限及最后通过断言输出；断言未删除，既有 30 秒总上限已明确调整，不能将这项变化描述为保持原超时。
- `2526924` 的 run `43230146870` 新增 16 窗格场景失败：第 7 项未完成，随后监控器不在运行，后续共享池用例连带失败。该夹具使用立即退出的测试程序，无法区分拆分过程中的窗格退出与长驻 CLI 场景；此结果不证明历史 15/16 同因，也不标为历史故障已关闭。
- 16 窗格夹具改为独立就绪文件和统一释放屏障，25 秒内要求全部就绪，释放后 15 秒内要求全部 `Finished/0`；每项就绪输出使用 `FileMode.CreateNew` 拒绝重复执行。失败快照记录缺失位置、claim、回执及已持有句柄的监控器退出码，失败 CI 上传隔离账本、回执及 UI/浏览器报告。监控器顶层异常尽力写入 `monitor-error.json`，保留类型、错误码和堆栈；不自动释放占用或重放。
- Worker 与测试程序集顺序编译通过，0 警告、0 错误；前次测试程序集构建与 Worker 构建同时写共享 obj 曾产生锁冲突，顺序重跑通过。131 项原生测试已通过，新的 Terminal 屏障场景仍待对应提交的完整 CI 验证。本节记录真实失败与修改范围，不能替代最终 CI 或生产长稳验收。

## 37. 16 窗格门禁通过与现有入口更新（2026-11-12）

- commit `11f385f5305f0330628f4b3496fb22d9840810ab` 的完整 CI run `43236261872` 于 **2026-11-12 14:35:25 UTC** 完成，job 及完整门禁均为 `success`，包括真实 Pi、IPC/进程/Terminal 集成、新增 16 窗格屏障、WPF、原型和浏览器阶段。诊断上传步骤也成功。原 117/120 的本机受限结果不再是唯一验收证据；本机 Pi/Terminal/Edge 的权限或环境限制仍未被远端成功替代。
- 同一源码通过生产顺序构建到 `artifacts/preview-final-11f385f/`，四个正式程序集 UTF-8 / UTF-16LE 测试钩子扫描 CLEAN。屏障夹具本机冒烟确认就绪 PID、释放前存活及释放后 exit 0；首次冒烟过早读取刚创建的 JSON，PID 检查失败，等待完整 JSON 后通过，未将首次失败计为通过。
- **现有桌面入口已更新**：2026-11-12 **14:40:08 UTC** 在确认匹配 CI 成功、待替换目录无运行进程后，将 `artifacts/preview-claim-recovery/` 备份到 `artifacts/preview-claim-recovery-before-11f385f/`，再将新包完整切换到原目标路径。14 个发布文件逐项核对长度及 SHA-256；桌面 `D:\Desktop\多 Agent 启动器.lnk` 的目标与工作目录读回正确，所指目录的 `verification.json` 记录上述 source commit 和成功 CI。未修改桌面文件，原快捷方式下次打开即使用新包；回退目录保留。证据：`artifacts/ci-11f385f.json`、`artifacts/preview-entry-switch-11f385f.json`。
- 切换预检期间，子进程中的 GitHub 请求与 WMI 查询被拒绝，旧包均未被移动；随后使用本会话已取得的完整 CI JSON 和普通进程映像查询完成核对。此处记录的是预览目录切换，不代表安装器、干净环境分发或运行真实 Agent 已验收。
- 16 窗格门禁现有一次成功，但历史 cmd 中途退出、原始 15/16 的同因证明仍未建立。S0.6 的真实双任务内存准入仍未满足，S5 与原定生产 F12、两小时真实中文 CLI、无障碍及干净环境分发继续开放。未知退出、丢失 Job/池和无身份旧记录保持原安全边界。此轮完成提交、CI 和本机预览入口交付，不将这些未完成项改成已修复。

### 最终提交重跑暴露的偶发失败

- 文档提交 `381c972` 的 run `43240231942` 再次失败于 16 窗格：25 秒时第 3、6 项未就绪，随后配置/预算相关用例级联失败。不能用 `11f385f` 的一次成功替代此提交失败；入口中的产品代码与该文档提交相同，保留此前记录但不宣称窗格问题已经解决。
- 夹具现支持独立的 `--parallel-batch-checks` 和 `--terminal-batch-checks`；前者绕过 Terminal，保留 16 个 Worker 同时走真实 IPC/Job 启动。在本机隔离根、既有 FAKE_SAMPLE 下，直接并发对照 16/16 就绪并统一释放后成功。它不能证明 Terminal 路径正常。
- 失败快照同时写入日志，包含每项 claim/回执及监控器退出码；统一释放与等待放入 finally，未领取的合成项仅走已有 revoke 协议，恢复之前的隔离配置，避免失败污染后续测试。全量 gate 前增加两种独立对照，仍保留全量中的真实 Terminal 场景。远端归档下载受本机网络权限限制，新增日志用于下一次精确归因；未凭缺失推断完成。

## 38. 并发启动 IPC 排队超时修复（2026-11-12）

- `2ea0080` 的 run `43245351197` 在不经过 Terminal 的直接 16 Worker 对照失败：16 项全部已有 claim，2、3、7、8、10、12、14 的回执为 `FailedBeforeStart/Pid=0`，错误为请求取消，监控器仍存活。该次失败排除了 Terminal 未创建 pane runner；结合单线程 pipe 服务及 begin 内同步池 helper 创建，确认大量 begin/resume 请求争用同一服务、耗尽 8 秒 IPC 预算的启动缺陷。此证据不证明历史无 claim 的 15/16 或运行中的 cmd 退出同因。
- Worker 领取单项 claim 后，先等待同一监控器的启动事务互斥，再发送 begin、受保护地创建 suspended 子进程、resume 和持久 Started；之后立即释放事务互斥，已启动 Agent 仍并行运行。排队最多 60 秒，超时明确记录未创建 Agent；异常退出的互斥通过 abandoned ownership 继续。没有延长 IPC 预算、重发未知请求、降低资源阈值或释放无法证明退出的占用。
- 本机匹配测试构建 `artifacts/concurrent-start-fixed/`：独立 16 Worker 回归 **16/16 就绪，统一释放后全部 Finished/0**；新增两项排队边界检查 **2/2 通过**。其中正常释放场景在互斥内等待 9 秒，超过 IPC 预算，仍保持 Pending、无子进程输出/回执且 status 可查询；另一场景让互斥所有者 exit 97，等待项随后正常执行并 Settled。测试夹具只操作独立 AgentLauncher-tests-* 根。
- `scripts/test.ps1` 将两项边界检查作为独立启动阶段，随后继续直接并发、真实 Terminal、全量集成、WPF、原型及浏览器检查，失败诊断仍上传。最终对应 SHA 的 CI、生产包和入口切换证据单独保存在 artifacts；不以本机定向通过替代完整门禁。
- S0.6 的 30 分钟三轮、真实中文 CLI 两小时、生产 Terminal F12 人工交互、无障碍/干净环境分发与 S5 的能力前置仍须实际完成；本节不将它们标成通过。缺失身份的旧记录与无法证明结束的进程树继续保留待核对状态。

## 39. 本机 Pi / Terminal 集成前提补齐（2026-11-12）

- `7e014a02a6a2df91d1ab0f3a1389ec5f4e4199d2` 的完整 CI run `43248830224` 于 **2026-11-12 15:45:54 UTC** 成功完成，覆盖新增启动排队边界、直接 16 Worker、真实 16 窗格、全量集成、WPF、原型及浏览器；这才是第 38 节修复的完整 CI 证据。后续测试脚本维护提交仍须核对其自身 SHA 的 CI。
- 本机 Pi 已安装 **1.5.2**，CI 固定为 **1.5.0**。Pi 官方 `PI_CODING_AGENT_DIR` 指向隔离可写目录后，真实 `--version` exit 0；没有修改原用户配置。`scripts/test.ps1` 现为 Pi 设置独立测试配置根，并在 finally 恢复调用者的变量，消除版本检查初始化原用户目录引发的 EPERM。
- 官方 Terminal **1.25.3131.0 x64** 便携 ZIP 已下载到 artifacts，SHA-256 与官方 release asset digest 完全匹配，WindowsTerminal.exe 签名为 Valid。便携模式只写工作区，测试进程的 PATH 包含此入口；没有安装系统包、修改全局 PATH 或 Terminal 设置。真实独立 16 窗格检查通过；随后全量集成再次包含 16 窗格，结果 **121/121、0 失败**，此前 117/120 的三个本机前提失败已补齐（新增一项为 16 窗格回归）。证据：`artifacts/local-integration-complete-7e014a0.json`。
- 同一产品修复的原生/模型 **131/131、0 失败**。测试 runner 增加 `-RuntimeDirectory`，允许在旧测试监控器仍持有默认目录时使用新发布目录；不会为重建而结束旧进程。正在验证的本机统一脚本使用此选项、隔离 Pi 及便携 Terminal；浏览器仍另以完整 CI 验证，本机脚本的 SkipBrowser 会明确记录，不能标为本机完整 gate。
- 2026-11-12 **15:36:51 UTC** 原生内存预检可用物理内存 **3,175,452,672 bytes**，S0.6 默认双任务要求 **3,758,096,384 bytes**，准入不满足，已执行轮数仍为 0。证据：`artifacts/acceptance-preflight-7e014a0.json`。独立真实长期负载、S5 与人工验收继续按实际状态保留；不靠降低门槛、终止用户进程或远端普通 CI 成功替代。
- 最终生产 App/Worker 必须按同一最终提交顺序发布，扫描无测试钩子，并在匹配 SHA 的完整 CI 成功后替换 `artifacts/preview-claim-recovery/`；原快捷方式的目标路径不变。逐文件 SHA-256、ZIP 和旧目录备份保存在 artifacts 的最终提交交付清单中，操作结果以清单为准。

## 40. 重跑发现的断连崩溃与无领取窗格（2026-11-13，Asia/Shanghai）

- `ca6def3` 的 run `43251943785` 通过直接并发、真实 16 窗格等场景后，失败于 QQ-C08 隔离 monitor 提前退出，错误 **Pipe hasn't been connected yet**。服务端原来只检查 `WhenAny` 中 accept 已完成，未观察该 Task 的异常，也未处理连接成功后立即断开的 InvalidOperationException。现在先观察连接结果，未连接即重建 listener，并仅对已断连 pipe 的该异常进行隔离；不重发请求、不修改账本。新增 64 次短连接断开检查，确认原 monitor PID/epoch 保持，status 持续正常。
- 本机统一脚本 `artifacts/local-final-startup-queue/` **exit 1**：Pi Node 入口在原安装路径上 lstat `.pi` 遭 EPERM；另有第 10 项无 claim、无回执，monitor 仍活着。该轮不能用第 39 节 121/121 的单独运行替代。Pi 临时配置能避免写用户数据，但不能解决读取原安装目录被拒绝；工作区另按 CI 同版本安装官方 **1.5.0**，验证版本 exit 0，仅对测试进程设置 PATH，无全局安装或配置修改。
- 无领取窗格已实际复现，具体 Terminal 内部丢失/早退原因仍未有完整证据，不能将其与历史故障全部同因关闭。产品将整批 Terminal 命令拆为逐项提交，每次等待对应 Worker 的一次性 claim 后才提交下一次 focus/split；每项 8 秒仍未确认则停止后续，明确提示核对，支持取消，不重试未知提交。完整命令长度仍在任何提交前校验，参数继续使用 ArgumentList。已启动 Agent 不等待其他项完成。
- Worker 增加进入 RunPane 的 PID/时间证据及未处理错误的类型/HResult/堆栈（不记录参数）；16 窗格快照在 finally 保存，因此提交阶段提前失败也能保留各项 entered、claim、receipt、error。缺失证据仍不等于进程退出或任务完成。
- 更新双窗格夹具为与长期 CLI 相符的就绪/释放屏障，继续真实核对 cwd 与中文、分号、空格字面参数。新增外部 Terminal 替身验证“未领取不发送下一项、取消不继续发送”，并保留真实 Terminal 检查。真实 16 窗格连续 **8 轮、128 项全部通过**；启动事务正常/崩溃释放与断连检查均通过。生产 solution 编译 **0 警告、0 错误**。新增快照和夹具之后仍以最终 SHA 的完整 gate 为发布依据。
- `artifacts/production-pane-check/result.json` 对 `ca6def3` 生产 Worker 的真实 Terminal 自动化检查 **3/3 通过**：异常退出、明确内存受限两种情形忽略缓冲 Enter，F12 可关闭；正常退出 Enter 可关闭。该探针使用真实生产程序集和 console input，并非 `INTEGRATION_TESTS` 跳过等待；仍不冒充独立人类验收。后续产品提交须核对相同逻辑及最终包。
- 长期代表性负载、两小时真实中文 CLI、S5 及无障碍/干净环境人工验收未获得新通过证据，继续保留实际状态。当前处理的是可执行的故障修复、完整门禁与最终交付，不将短回归或 CI 代替这些要求。
