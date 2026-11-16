# UI 重新设计 v0.8 —— 会话优先侧栏（方向 A）

状态：已批准方向与稿（用户两轮 ask 确认）；本文档是实施契约。
基线：v0.7（docs/UI-REDESIGN-v0.7.md，单栏卡片 + 折叠区）。参考：claude-squad / Conductor / hivemux 的「侧栏会话列表 + 主区详情」范式，落地为 WPF 原生控件。

## 1. 方向与约束

- 会话/状态优先：左侧栏一眼看到「项目 → Agent 状态 → 运行会话与队列 → 本机资源」。
- 简单、美观、实用、省内存：不引入 WebView2/CEF；不新增定时器、轮询或持续动画；侧栏复用现有 `Render()` 周期；视觉只加静态画笔与一条选中高亮。
- 行为契约全部保留：启动门禁、硬/压力两级理由、队列与并发上限、目录策略、只读失效保护、恢复备份、参数结构化编辑、无障碍名称。
- 底部启动条不变：提示 + 重新检查 + 按资源依次启动 + 主按钮 + 错误条。

## 2. 结构总览

```
┌────────────────────────────────────────────────────────────────────┐
│ Rail 232px（固定）              │ Main（*，ScrollViewer）             │
│ ┌────────────────────────────┐ │ ┌────────────────────────────────┐│
│ │ ◆ 多 Agent 启动器     ⚙ ⋯  │ │ │ 01 · Pi        [程序与目录就绪] ││
│ ├────────────────────────────┤ │ │ ┌─配置────────────────────────┐││
│ │ 项目                       │ │ │ │ Agent [下拉]  目录 [选择…]    │││
│ │ demo                       │ │ │ │ 参数 默认 · 详情…             │││
│ │ D:\Projects\demo    [更换] │ │ │ │ 规划 0.75 GiB · Job 上限 2GiB │││
│ ├────────────────────────────┤ │ │ └─────────────────────────────┘││
│ │ AGENT · 已选 1/4           │ │ │ ┌─分屏预览────────1 个窗格─────┐││
│ │ ● 01 Pi            就绪 ✓  │ │ │ │ $ pi   （沿用现有预览渲染）   │││
│ │ ○ 02 Codex        未启用   │ │ │ └─────────────────────────────┘││
│ │ ○ 03 Claude       未找到   │ │ │ ▾ 启动选项 · 目录与分屏（原样） ││
│ │ ┄ ＋ 添加 Agent            │ │ │ ▾ 组合与批量设置（原样）       ││
│ ├────────────────────────────┤ │ │ ▾ 任务队列（原样，含卡片）     ││
│ │ 会话与队列                 │ │ │ ▾ 运行记录（原样）             ││
│ │ ▶ 01 Pi · 12:34           │ │ └────────────────────────────────┘│
│ │ ⏸ 排队 2 项 · 等待资源     │ └────────────────────────────────┘│
│ ├────────────────────────────┤├────────────────────────────────────┤
│ │ 实用 · 可用 9.8 GiB   ⚙   ││ 提示 …  [重新检查][按资源依次][启动]│
│ └────────────────────────────┘└────────────────────────────────────┘
```

- Rail 宽度 232（逻辑像素），窗口逻辑宽 < 900 时收为 56px 圆点条（圆点 + 序号，名称进 ToolTip；点击行为不变）。窄态隐藏 ProjectCard 与 RailResource pill，但命令不丢失：底部出现「⋯」窄屏菜单（更换项目、资源设置、工作目录、恢复与诊断、运行历史与导出、恢复备份——恢复备份仅在只读恢复态可用），「设置」按钮停靠品牌行右侧保持可达；项目也可拖拽文件夹切换。
- Main 顶部是选中项详情卡；其下依次是预览卡与三个既有折叠区（原样重挂载，不改编码行为）。
- 运行记录（SessionExpander）与任务队列（TaskExpander）保留在 Main；Rail 的「会话与队列」是只读摘要 + 点击跳转（展开对应折叠区），不是第二份编辑器。

## 3. 元素迁移表（旧 → 新）

| 旧（v0.7 位置） | 新（v0.8） | 说明 |
| --- | --- | --- |
| HeaderPanel：Brand + 更多/设置/恢复备份 | Rail 顶部行：Brand + ⋯（更多菜单）+ ⚙ 设置；恢复备份按钮同菜单逻辑保留 | 顶栏整行删除，少一层 |
| ProjectPanel 大卡（18px 标题 + 路径 TextBox + 选择文件夹 + 最近项目） | Rail「项目」迷你卡：项目名（加粗一行）+ 等宽路径（截断）+「更换」QuietButton；路径编辑改为点击「更换」后弹出现有选择/输入路径（复用 ChooseClick 与拖放） | `ProjectPath` TextBox 保留为可聚焦编辑框（迷你卡内单行等宽），自动化 Id 不变 |
| SlotList 大卡行（勾选+序号+下拉+目录+详情+⋯+状态行） | 主区详情卡内 **仅渲染选中项一行**（SlotList 语义不变，仍含 CheckBox/ComboBox/MenuButton/详情/⋯）；Rail「AGENT · 已选 N/M」= 全部 slot 的只读索引行（新 `RailSlots`，状态点+序号+名称+右侧状态），行点击 = 选中 | `AddSlot` 拆为 `AddRailSlot`（新）与详情行重建；检查夹具以「先点 Rail 行选中，再 Find<>(SlotList)」迁移 |
| 行内状态文本（commandLabels） | 选中详情行的状态行 **与** Rail 行右侧状态文本共用同一 `commandLabels[slot.Id]` 语义 | `row labels follow theme` 读 `commandLabels.Values.First()` 不变 |
| （无新增） | 详情卡即 SlotList 行本身（Agent 下拉/目录/详情/⋯ 原样保留），不另加只读参数摘要；参数编辑仍只走详情对话框 | 不复制第二套参数编辑器 |
| SummaryPanel 内 Preview（折叠区内） | 主区常显「分屏预览」卡：`Preview` 与 `PaneCount` 移出新 `PreviewCard`；`DirectoryMode`/`Layout`/`DirectoryHelp`/`SummaryPanel` 留在「启动选项」折叠区内 | 预览渲染函数不变；ACC-U04 与 `!SummaryPanel.IsVisible` 断言继续成立 |
| LaunchOptionsExpander / ConfigurationToolsExpander / SessionExpander / TaskExpander | Main 详情卡下方，原顺序原行为 | 仅重挂载 |
| （无状态点） | 状态点画笔：StatusOk/StatusOff/StatusBad/StatusBusy | 新 token，见 §4 |
| （无资源常驻） | Rail 底部资源 pill：模式 + 可用内存一行 + ⚙ 资源设置；数据来自既有 LoadResourceViewAsync，不新增采样 | 仅读既有结果 |
| Sessions/ProgressText（折叠区内） | Rail「会话与队列」摘要行（只读文本行，最多各 3 条 + 「全部…」跳转） | 数据源复用现有渲染产物 |

## 4. 主题 token 增量

新增（亮/暗两组，进 `ApplyTheme` 数组与高对比映射）：

| Key | 浅色 | 深色 | 高对比 | 用途 |
| --- | --- | --- | --- | --- |
| StatusOk | #1E8E3E | #7BD88A | WindowText | 就绪/运行中点 |
| StatusOff | #9AA0A6 | #6B7078 | WindowText | 未启用点 |
| StatusBad | #B42318 | #FFB4AB | WindowText | 未找到命令/错误点 |
| RailSurface | #ECECEE | #26282C | Window | 侧栏底（与 Page 区分一层） |

其余沿用现有 Surface/Raised/Ink/Muted/Line/Accent/Selected/Warning*/Preview*。无新动画、无渐变。

## 5. 交互模型

- `selectedSlotId: Guid?`：默认第一个 Enabled 项，无 Enabled 则第一项；Render 时保持选中（项被删则回落到第一项）。
- Rail 行点击 → 设 `selectedSlotId` 并重绑详情卡；勾选框点击只改 Enabled（Checked/Unchecked 路径不变，UIA TogglePattern 一致）。
- 详情卡控件按选中项重建绑定，自动化名称按选中项位置重发「第 N 项 Agent / 第 N 项工作目录 / 第 N 项详情」——检查夹具按名称定位的断言不受影响。
- 参数摘要是只读一行（`参数 N 项 · 预览` 或 `默认参数`），编辑只走 EditSlot 对话框（含预设/结构化参数列表）；不复制第二套参数编辑器。
- 拖拽排序（AttachSlotDrag）保留在 Rail 行；行高变紧凑，拖拽手柄改为序号区。
- 会话与队列摘要：行点击 → 展开 Main 中对应折叠区并滚动到可视。

## 6. 响应式

- 逻辑宽 < 900：Rail → 56px 圆点条；< 650：沿用现有行内紧凑分支（详情卡控件换行）与底栏换行逻辑（ArrangePanels 既有分支扩展一处）。
- UiScale 100/125/150 不变；所有新尺寸走逻辑像素。

## 7. 内存约束

- 不新增 DispatcherTimer/CompositionTarget 回调；Rail 各行在既有 `Render()` 内重建（与现状同频）。
- 状态点用共享静态画笔（FindResource 一次），不为每行 new Brush。
- 摘要有界：会话 ≤3 行、队列 ≤3 行，超出折叠为「全部…」。

## 8. 检查迁移策略

- 结构稳定断言（文本/自动化名称/行为）原样保留；布局脆弱断言（坐标/尺寸/树路径）按本文档结构重写，全部记入 docs/IMPLEMENTATION.md §60 待评审。
- 截图基线（native-{theme}-{width}.png、native-text-{scale}.png）重新出图，人工核对后替换，记 §60。
- 检查清单逐项映射表见 §9（由结构盘点补齐）。

## 9. 检查清单映射（274 项，8 个套件）

套件：UiChecks 55、QuickQueue 22、TaskUsability 21、UiReview 48、TaskUiChecks、UiMultipaneChecks、SimpleUiChecks、ReviewUiChecks（后四套共 128 项，抽样审计：仅 SimpleUiChecks :37 触 SlotList）。
元素定位事实：`Find<>(SlotList)` 共 13 处，全在 UiChecks（12×`.First()`、1×`.Last()`）；`SlotList.Children[n]` 无；名称式访问「第 N 项」仅 SimpleUiChecks :37（第 1 项）与 UiMultipaneChecks :67（ProgressText 文案）。

### 保留（不改断言）

- 底栏全部（LaunchButton/LaunchHint/ErrorBanner/ErrorText/SaveStatus/RetrySaveButton/ReconcileButton/CancelButton/QueueQuickButton、admission 硬/压力、清理门、CR03、CR08、R-reconcile）——底栏不变。
- 主题 6 键（Ink/Selected/Raised/WarningInk/Muted/Page）+ `dark palette complete`、`theme resources switch`、`window background follows theme`、`row labels follow theme`（commandLabels 语义保留）。
- 预览（`preview keeps original slot numbers`，Preview 常显化不改渲染）。
- 折叠区（ACC-U04 保持折叠/可达；`!SummaryPanel.IsVisible` 与 `SummaryPanel.IsVisible` 两组都成立）。
- Sessions/QueueList/QueueResumeBanner/TaskExpander 全部（CR05 13 行、CR09 撤销族、UX03/UX07）——元素原位保留。
- 只读/模板/拖放/诊断/参数对话框/queue claim 全部。
- `SelectionSummary` 精确文案「已选 1 个，共 4 个」（UiChecks :88）不变；Rail 的「AGENT」区标题是新的独立元素，不复用该文案格式。
- `native resize and layout settle`：WorkGrid/ConfigurationPanel 保留为主区根 → 原样通过。

### 重写（记 §60 待评审）

| 检查 | 原因 | 新断言 |
| --- | --- | --- |
| `native checkbox updates Draft`（UiChecks :92） | `.Last()` 依赖 4 行同时渲染；SlotList 只含选中行 | 新增夹具助手 `SelectRailSlot(4)`（点 AutomationName「第 4 项 Agent 行」），再取 `Find<CheckBox>(SlotList).First()`；其后插 `await SelectRailSlot(1)` 复位选中，因为后续 Agent 选择器/菜单/移除族都以选中第 1 项为前提 |
| `preview keeps original slot numbers`（:94） | 上一条选中第 4 项后 Preview 首窗格为 4 | 断言不变，前提改由 SelectRailSlot 建立 |
| `row edit restores keyboard focus` / 只读回退 / automation toggle ×2 / Agent 选择器 ×2 / menu 状态 / remove / undo 链（:77-:120） | 默认选中第一项，语义不变 | 断言不变（仅确认默认选中 = 第一项） |
| `dropdown selection container…` / `…readable in dark`（:142/:144） | SlotList 仅一行 | 断言不变 |
| `primary configuration fills width without a resource sidebar`（:171） | 左侧新增 Rail；断言字面仍成立（无右侧摘要列），名称误导 | 更名 `primary configuration fills the main column without a right summary sidebar`；断言不变 + 布局 JSON 增 RailWidth/SelectedSlot |
| 截图基线（native-{theme}-{width}.png / native-text-{scale}.png / layout-*.json） | 视觉结构改变 | 重新出图，人工核对 |
| 新增检查 | Rail 新结构 | 见下 |

### 新增检查（记 §60 待评审）

1. Rail 索引完整：`RailSlots` 行数 == Slots 数，行名「第 N 项 Agent 行」连续，状态点按 Enabled/命令状态分色。
2. 选中联动：点「第 3 项 Agent 行」→ SlotList 行序号/控件自动化名变为「第 3 项 …」。
3. 会话与队列摘要：Sessions/Queue 渲染后 Rail 摘要行数与上限（≤3 + 全部…）一致。
4. 资源 pill：LoadResourceViewAsync 后 pill 文本含模式与可用内存。
5. Rail 收起：逻辑宽 < 900 时 RowDetails.RailWidth==56 且行 ToolTip 含名称。

## 9.1 出范围风险

TaskUiChecks / UiMultipaneChecks / ReviewUiChecks 未逐项审计；实施后必须整门绿。若个别检查依赖多行 SlotList 同屏，按上表同法（SelectRailSlot）迁移并记 §60。

## 10. 验证

1. `node --test tests/prototype.test.cjs`（HTML 原型模型不受影响）。
2. `AgentLauncher.App.exe --ui-check <dir>` 全绿 + 新截图人工核对（dark/light × 宽/窄 + 125%/150%）。
3. `scripts/test.ps1` 全门绿。
4. 内存：冷启动到稳定空闲的工作集与 v0.7 基线（约 130.8 MiB，§60）对比，增量 ≤ 5 MiB，记 §60。
