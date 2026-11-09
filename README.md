# 多 Agent 启动器 · 技术预览 v0.6

Windows 原生配置窗口 + Windows Terminal 分屏。默认只启用一个 Agent；同项目多项使用独立 Git worktree，或明确选用现有目录/共享目录。

运行 `artifacts/preview/AgentLauncher.App.exe`，请保留同目录所有 DLL、Worker 和 runtimeconfig 文件。当前机器可直接使用；其他电脑需要 .NET 8 Desktop Runtime x64、Windows Terminal、所选 CLI；自动 worktree 需要 Git。此包不是自包含安装包，正式 .NET 10 发布及完整性能/交互验收仍待完成。

1. 拖入项目文件夹，或点“选择文件夹”。
2. 勾选本次项，选择 Pi/Codex 等；“参数与目录”可覆盖实际程序和目录，参数每行一个。
3. 选择目录策略并查看本次布局和资源检查；资源不足时不会强行启动。
4. 点启动后查看逐项回执，在 Windows Terminal 内工作。启动进程不代表 Agent 已就绪或任务完成。
5. “工作目录”可查看分支/基线、打开目录；完成后人工审阅、提交、合并和测试。

没有任意 shell 命令拼接入口，没有自动合并、清理 worktree 或结束用户其他程序。关闭配置窗口不主动结束已运行 Agent；关闭 Terminal 窗格仍可能结束其中任务。

- [实现状态、实测与未完成门禁](docs/IMPLEMENTATION.md)
- [模块架构和 UI 信息来源](docs/ARCHITECTURE.md)
- [UI 详细设计](docs/UI-DESIGN.md)
- [完整验收要求](docs/ACCEPTANCE.md)
- [HTML 交互示例](docs/prototype.html)（示例数据，与原生程序分开）

构建：`powershell -NoProfile -File scripts/build.ps1`。

开发验证：`powershell -NoProfile -File scripts/test.ps1`。测试构建使用隔离临时目录、短生命周期进程，包含故障注入和模拟准入采样；会短暂打开一个测试 Terminal 双窗格。该测试模式不编入 preview 生产配置。不要用测试目录的 exe 日常运行。

源码使用 .NET BCL/WPF，没有引用第三方 NuGet UI 库。Git、Terminal 和各 CLI 是用户已有的外部程序，不随包复制。工具默认在 `%LOCALAPPDATA%\AgentLauncher` 保存配置、备份、授权记录及准备目录；没有自动删除策略。配置中参数按本地执行需要保存，请勿在参数中填写长期密钥。
