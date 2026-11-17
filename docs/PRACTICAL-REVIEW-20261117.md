# 日常使用改进依据（2026-11-17）

本轮依据 `IMPLEMENTATION.md` §67、§75–77、当前代码和测试日志审查。现有会话通知、worktree、任务队列、变更摘要和审阅入口已经具备；优先补齐内存压力下继续工作的能力及其验证。

## GitHub 参考

| 来源 | 本次实际读取 | 采用的原则与边界 |
| --- | --- | --- |
| [AgentHub](https://github.com/jamesrochabrun/AgentHub) | main 的 README：会话管理、worktree、变更审查、通知、延后加载 GitHub 状态 | 实用性取决于会话到结果的完整工作路径。启动器已有对应入口，当前日志的主要缺口在资源压力；没有引入其 macOS 内嵌终端或对话采集模型。 |
| [Mem Reduct](https://github.com/henrypp/memreduct/blob/master/src/main.c) | `src/main.c` 的 `_app_timercallback`，物理用量门槛和 `AUTOREDUCT_COOLDOWN`；文件 blob `7cd9ed9ef794b906be42cc681603b98ce60cae9b`，注释指向 issue #191 的反复清理问题 | 内存缩减必须限定触发条件并控制频率。这里仅触达启动器原 Job 的成员，采用逐项与全局冷却，不使用该项目的提权全机清理。未复制上游代码。 |
| [Microsoft EmptyWorkingSet](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset) | 官方 API 文档与访问权限要求 | 使用 `PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA`；固定句柄再核对归属。缩减工作集不等于释放 commit 或证明已换出到磁盘。 |

抓取快照保存在忽略目录 `artifacts/github-agenthub-readme.md`、`github-memreduct-main.json`、`github-memreduct-main.c` 和 `emptyworkingset.html`。GitHub 连接器返回凭据错误后改用公开 HTTP；后续提交 API 达到限流，因此不宣称已核对 Mem Reduct 的最新提交 SHA，以上 blob 对应实际读取内容。

## 改动与验收重点

1. 修复原草稿按 Refresh 次数累计空闲的问题：请求密集时原“30 拍”可远短于 30 秒。Core 纯策略接收单调时间与整 Job CPU 计数，未知读数重新观察。
2. 物理内存低于策略保留线且压力已锁定才回收；CPU/commit 单独告急不清理物理页。每项尝试冷却 120 秒、全局 5 秒，等待新采样再处理下一项；失败也不会形成高速循环。
3. 打开进程句柄后再检查 Job 归属，防止 PID 复用误触其他程序；读取与回收失败不改变启动账本。
4. 报告实测私有工作集差值，不声称减少 commit、不承诺增加固定并发。夹具回收后读取原始页面、改写并正常退出，验证继续工作的能力。
5. `--admission-integration` 原本没有被 `scripts/test.ps1` 调用，现纳入统一门禁并保留独立诊断结果。

可执行合同见 `CONTRACTS.md` C10；验收映射见 `ACCEPTANCE.md` W1–W3；实际结果与剩余边界见 `IMPLEMENTATION.md` §78。
