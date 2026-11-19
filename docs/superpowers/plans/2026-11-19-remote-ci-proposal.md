# 远端 CI 方案（提案，待操作员决定）

日期：2026-11-19 起草。状态：提案，未实施，不进入本仓库 workflow。
对象：把「远端 CI」从缺口收口计划的开放项变成一个可执行、可审计的最小方案。

## 1. 目标与边界

- 目标：让远程执行合同（C14：R23–R26、R28、R33，以及 R45 的真实 scope 路径）在真实 Linux VPS 上获得**可重复**的自动回归，而不是只依赖一次性 opt-in 活测。
- 边界：不在公开 CI 里存放或读取任何地址、用户名、密钥；不把活测结果写成 G4 或浸泡；不改变现有 `gate` workflow（约 18 分钟，保持 main 绿色）；远端 CI 失败不得阻塞 `gate`，只作为独立信号。

## 2. 推荐方案：GitHub self-hosted runner（阿里云 VPS，Linux x64）

1. 在**一台** VPS（建议阿里云）上安装 GitHub Actions self-hosted runner，注册到本仓库，打标签 `remote-linux`。
2. 新增 workflow `remote-gate.yml`：`workflow_dispatch` + `workflow_run`（gate 成功后触发），`runs-on: [self-hosted, remote-linux]`。
3. Runner 主机预置（一次性，操作员手工完成并记录）：
   - 独立系统用户 `ci-agent`，HOME 下无任何凭据文件；SSH 私钥只放 runner 服务账户可读的位置，环境变量注入，不落脚本。
   - 预装被测 agent CLI（pi/kimi/omp 固定版本）、Git、.NET 8 SDK、Mutagen、systemd（cgroup v2 默认开启）。
   - `/sys/fs/cgroup` 为 v2 统一层级（`stat -f -c %T /sys/fs/cgroup` = cgroup2fs），这是 R45 真实 scope 路径的前提。
4. workflow 步骤（全部走仓库现有入口，不新增测试通道）：
   - `git checkout` + `dotnet publish`（INTEGRATION_TESTS）到 runner 本地目录；
   - 运行 `--remote-execution-integration`、`--remote-startup-e2e`（环境指向 localhost SSH 回环或 runner 自身 sshd，fake-ssh 退出 PATH，走真实 OpenSSH）；
   - opt-in 步骤 `--remote-live <host> …` 以环境变量提供 VPS 参数（仅 workflow_dispatch 手动触发时执行）；
   - 每次运行归档 evidence 到 runner 的 `artifacts/remote-ci/<run-id>/`，工作流只上传结论 JSON，不上传密钥与主机名。
5. 自托管 runner 的安全前提（必须写入运行记录）：仓库是公开的，**self-hosted runner 绝不能暴露在 fork PR 触发的 workflow 里**——`remote-gate.yml` 只允许 `workflow_dispatch` 与本仓库 main 上的 `workflow_run`，不响应 `pull_request`。

## 3. 备选与不推荐

- SSH 到 VPS 的 GitHub hosted runner（`ssh` step）：把私钥放公开 CI 环境，违背 §P1「凭据不进仓库/CI」；不采用。
- 在 VPS 上跑完整 `gate`（含 WPF/浏览器）：Windows 专用步骤无法在 Linux 运行，收益为零；不采用。
- 京东云作为第二台 runner：等阿里云单点稳定、连续两轮成功后再考虑；同权活测仍以操作员 opt-in 为准。

## 4. 验收与记录

- 远端 CI 的通过标准：与本地门禁同口径——集成/远程集成/E2E 退出 0，evidence 齐全；结果必须标注「远端 CI（self-hosted，阿里云）」，不能当作本机长稳或 G4 证据。
- 实施后在本计划追加「已实施」小节并引用 run 记录；未实施前本提案不关闭缺口收口计划的对应条目。
