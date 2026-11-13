# Omp 多会话宿主能力审计

审计日期：2026-11-13。对象为官方 `v18.19.0`，提交 `04cd1a7549a02d62ad1d24826198d52507595683`，不是浮动主分支。源码只读检出于 `artifacts/omp-audit-18.19.0/`。本表是静态审计；未运行双会话 SDK、故障演练或内存收益对照，不能把“存在 API”标为隔离验收通过。

结论：当前不能启用或分发 S5 同进程双会话宿主。通用 CLI 模式和共享 Job 资源池继续可用。S5.1 的静态审计已完成，运行验证受下列前提阻碍；S5.2–S5.6 尚未完成。

| 核对项 | 固定版本源码证据（相对于 Omp 仓库） | 审计结果与剩余验证 |
| --- | --- | --- |
| SDK / 运行时 | `packages/coding-agent/package.json` 的 `exports` 指向 `src/*.ts`，`engines.bun` 为 `>=1.4.4`；SDK 包含 Bun 及文本模块导入。 | SDK 可用但不是可直接由 Node 或独立 omp.exe 加载的 .NET 依赖。本机 Bun 实测为 1.3.14；保留“不安装新运行时”的约束，不在不满足前提的运行时上声称 SDK 通过。独立官方 omp.exe 自带运行时，仍可用于已有 CLI/RPC 长稳。 |
| 会话 / 工作目录 | `src/sdk.ts` 的 `CreateAgentSessionOptions` 提供 `cwd`、`agentDir`、`sessionManager`、`eventBus`；创建时可注入独立 `SessionManager`。 | 有显式会话入口。尚未实测不同 worktree 的事件、文件、会话租约及损坏恢复。 |
| AgentRegistry | `src/registry/agent-registry.ts` 有进程级 `global()`；`src/sdk.ts` 可注入 `agentRegistry`，默认顶层身份为 Main。 | 不能直接创建两个默认顶层会话。自定义 registry 的取消路径有专门处理，但 `internal-urls/history-protocol.ts`、`registry-helpers.ts` 等仍有全局查找；需按实际启用工具逐项验证。 |
| 扩展发现 | `src/discovery/omp-extension-roots.ts` 用 `AsyncLocalStorage` 保存 invocation roots；SDK 支持禁发现和 explicit-only。 | roots 已有作用域隔离，不能据此把全部扩展标为安全。扩展工厂可保存模块全局状态，首期仍必须禁止未核验扩展。 |
| 设置 / 规则 / 技能 | `src/sdk.ts` 的 `bindProcessState:false` 可关闭设置 effects / provider toggles 的绑定；同文件的顶层会话仍调用 `setActiveSkills` / `setActiveRules`。对应实现为进程级快照。 | `bindProcessState:false` 不是完整隔离开关。部分 URL 读取优先会话 context，但技能补全等仍读全局快照；同名不同内容技能、规则与补全必须实测不串会话。 |
| 环境 / cwd | `src/session/bash-runner.ts` 从会话 `SessionManager` 取 cwd，使用会话取消器及 sessionKey；shell env 来自设置。`session/auth-broker-config.ts` 仍读取进程环境；`session/session-worktree.ts` 存在受条件保护的 `process.chdir`。 | 部分工具显式接收 cwd/env，不等于任意扩展与凭据发现有独立进程环境。须验证认证、`/move`、子任务及所有声明支持的工具；未知项不标支持。 |
| 权限 / 取消 | SDK 的 `settingsApproval` 描述使用进程级 cfg approval host；bash runner 保留会话级 AbortController，custom registry 不复用全局 lifecycle。 | 必须验证两会话不同权限、取消一个另一个继续、迟到结果归属。源码结构不足以证明 100% 隔离。 |
| RPC 模式 | `src/modes/rpc/rpc-types.ts` 的 `new_session` / `switch_session` 操作当前会话；`rpc-mode.ts` 的入口持有一个 AgentSession。 | 会话切换不是两个并行 SDK 会话，不能用两个 session ID 或单会话循环代替最小宿主实验。 |
| 故障 / 收益 | 计划要求宿主崩溃、断管、背压、取消、损坏与租约演练，以及双 CLI 对双会话至少 5 × 10 分钟对照。 | 未执行；内存中位数改善至少 20% 与隔离 100% 均无通过证据。CLI 长稳不替代这些实验。 |

下一步进入条件：S0 稳定性封板；提供满足固定 SDK 引擎约束的隔离实验环境；设计显式的会话身份、工具支持名单和事件/权限作用域。随后先做双会话隔离红绿实验，任一串会话错误即保持功能关闭，再执行故障和收益对照。此次审计不修改 Omp 上游源码，不添加 Launcher 的生产运行时依赖，也不把 S5 未交付写成当前 CLI 的功能回退。
