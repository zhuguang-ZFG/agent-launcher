# 多 Agent 启动器 · 技术预览 v0.6

Windows 原生配置窗口 + Windows Terminal 分屏。默认只启用一个 Agent；同项目多项使用独立 Git worktree，或明确选用现有目录/共享目录。

运行 `artifacts/preview/AgentLauncher.App.exe`，请保留同目录所有 DLL、Worker 和 runtimeconfig 文件。当前机器可直接使用；其他电脑需要 .NET 8 Desktop Runtime x64、Windows Terminal、所选 CLI；自动 worktree 需要 Git。此包不是自包含安装包，正式 .NET 10 发布及完整性能/交互验收仍待完成。
本轮另生成 `artifacts/spec-candidate/AgentLauncher.App.exe`（win-x64 自包含技术预览，内含 .NET 8.0.25）。仍须保留整个目录，外部 Git / Terminal / CLI 不内置；尚未签名或通过完整发布验收。当前验证范围与限制见实现记录第 8 节。

1. 拖入项目文件夹，或点“选择文件夹”。
2. 勾选本次项，选择 Pi/Codex/Omp/Kimi/Qoder 等。启动器自动查找 PATH 与常见安装目录；详情中的程序路径可下拉选择或浏览。参数逐项添加、移除、调整顺序，空参数和参数内换行按原值保留。
3. 选择目录策略并查看本次布局和资源检查；资源不足时不会强行启动。
4. 点启动后查看逐项回执，在 Windows Terminal 内工作。启动进程不代表 Agent 已就绪或任务完成。
5. “工作目录”可查看分支/基线、打开目录；完成后人工审阅、提交、合并和测试。
6. 保存组合时可选“绑定当前项目”或“通用组合”；通用组合继承新项目，但原本单独指定的目录需要重新选择，不会自动降为共享模式。
7. 设置提供主题、100/125/150% 文字与控件大小、最近项目/组合管理及诊断预览。诊断默认隐藏完整路径，始终不导出参数、环境变量或对话。
8. 保存失败时使用「重试保存」；成功落盘前不会启动。启动中「停止后续步骤」会撤销尚未领取的请求，已运行任务保留；运行记录始终显示活动项，历史仅显示最近部分。

没有任意 shell 命令拼接入口，没有自动合并、清理 worktree 或结束用户其他程序。关闭配置窗口不主动结束已运行 Agent；关闭 Terminal 窗格仍可能结束其中任务。

- [实现状态、实测与未完成门禁](docs/IMPLEMENTATION.md)
- [模块架构和 UI 信息来源](docs/ARCHITECTURE.md)
- [UI 详细设计](docs/UI-DESIGN.md)
- [完整验收要求](docs/ACCEPTANCE.md)
- [HTML 交互示例](docs/prototype.html)（示例数据，与原生程序分开）

构建：`powershell -NoProfile -File scripts/build.ps1`。

开发验证：`powershell -NoProfile -File scripts/test.ps1`。测试构建使用隔离临时目录、短生命周期进程，包含故障注入和模拟准入采样；会短暂打开一个测试 Terminal 双窗格。该测试模式不编入 preview 生产配置。不要用测试目录的 exe 日常运行。

合并后评审的 CR01～CR12 修复与新旧回归证据见[实现记录第 10 节](docs/IMPLEMENTATION.md#10-合并后评审修复2026-11-10)。`artifacts/preview` 为本轮更新的普通预览；第 8 节的自包含候选是先前构建，不包含本轮修复。新增回归仍待评审，operator `task_verify` 未有可用基线，不能把手动门禁通过称为完整 spec 验收。


源码使用 .NET BCL/WPF，没有引用第三方 NuGet UI 库。Git、Terminal 和各 CLI 是用户已有的外部程序，不随包复制。工具默认在 `%LOCALAPPDATA%\AgentLauncher` 保存配置、备份、授权记录及准备目录；没有自动删除策略。配置中参数按本地执行需要保存，请勿在参数中填写长期密钥。
