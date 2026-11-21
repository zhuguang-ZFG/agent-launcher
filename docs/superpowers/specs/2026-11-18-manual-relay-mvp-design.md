# 人工触发的隔离任务中转最小原型

日期：2026-11-18。基线是已核对的 `origin/main` `4915841af10d1776d282ad6dede587efd4bca093`（PR #9，同步可靠性设计）。该提交是设计文档，同步可靠性本身仍待实现。PR #8 重新挂接已在 `7a3af193d37e79ef20142c3a489adbe0bde54e03` 落地。本文不改 R33，不改 Mutagen 或三机同步，不改 8 个窗格名额、16 个保存槽位和本地内存/CPU 准入。

状态：已批准范围的设计。本文不是测试通过记录，不写入 ACCEPTANCE 的「通过」，也不把原型说成生产任务入口。

## 1. 本轮是什么

操作者在聊天里明确触发一次之后，原型在调用方给出的临时目录里完成一轮：领取一条任务、在调用方给出的隔离项目目录里做白名单内的只读校验或结果数据落盘、再提交结果。传输是进程内的本地目录账本（fake transport）。没有套接字，没有轮询循环，没有常驻进程。

fake transport 只证明本机账本协议。它不是 VPS 已连通，不是用户电脑已连通，也不是 Grok Bot 已接入。

## 2. 本轮不是什么

- 不连接任何 VPS、用户电脑或公网地址，不读取既有主机、密钥或凭据文件。
- 不部署服务，不开监听，不创建 routine，不注册 webhook，不保存 Bearer 密钥。
- 不把本 Bot 登记为与本机、京云、阿里云同权的第四个节点。
- 不扩展现有 Worker、App 或远程执行入口，不自动唤醒，不自动重连。
- 不提供任意 shell、程序执行或仓库修改工具。自然语言和外部 payload 只是数据，不是授权。
- 不宣称 exactly-once。

## 3. 记录与状态

每条任务有 `task_id`、`attempt_id`、`idempotency_key`、载荷的规范 JSON 与 sha256、类型、状态和单调 `version`。类型只有 `file_verify`、`result_data`、`agent`。状态只有：

| 状态 | 含义 |
| --- | --- |
| `queued` | 已入队，尚无当前尝试 |
| `leased` | 一次人工触发已领取，租约未过期 |
| `succeeded` | 同一 attempt 的结果清单已通过校验并留下提交标记 |
| `rejected` | 路径、校验或协议拒绝。不自动另开 attempt |
| `needs_agent` | 智能/自然语言任务只归类，供人工处理 |
| `uncertain` | 租约到期或提交中断后结果不能判定。不自动重跑 |

同一幂等键加上相同类型和相同规范载荷，返回原任务，不建第二条。同一幂等键的冲突载荷被拒绝，原任务状态和载荷不变。

租约含 `attempt_id`、`holder_id`、`state_version`、`expires_at`。到期只说明账本上的租约不再有效，不说明原进程已经停止。到期后不得自动重跑，不得再写结果，不得发放新 attempt。提交旧 attempt 被拒绝。只有人工确认 `uncertain` 之后，下一次再明确领取时才允许新的 `attempt_id`。旧 attempt 不能提交新 attempt 的结果。

`agent` 在领取时改为 `needs_agent`，不创建执行用的暂存，不读取任务文本去运行。`file_verify` 只通过路径监狱读取隔离目录里的文件并比对 sha256，把结果 JSON 写进该 attempt 的暂存。`result_data` 只把结构化数据写进暂存。两者都不修改隔离项目目录。

## 4. 路径、载荷与提交

拒绝空路径、绝对路径、盘符、UNC，以及任何 `.` 或 `..` 段。拒绝符号链接，包括指向隔离目录之外的链接；不打开链接目标。暂存文件名只能是安全的单段相对名。

拒绝名为 shell、command、args、argv、cmd、exec 的字段，以及 password、secret、token、private_key、api_key、authorization、credential、pem、bearer 等凭据字段和 PEM 正文。JSON 对象里的重复字段也拒绝。拒绝时不把这些值写入账本。凭据不放在项目共享目录。本原型也不接受凭据参数。

提交前同时核对任务身份、attempt 身份、状态版本和租约。清单里的每个 sha256 必须等于暂存文件的实际字节。不一致则拒绝本次提交，状态不得变成 `succeeded`。相同清单的重复提交不写第二份结果。不同清单的再次提交被拒绝。

持久化使用临时文件、刷新和替换。事件文件只追加。崩溃恢复只在再次显式调用时发生：提交标记完整且清单与字节一致时，把账本补记为 `succeeded`，不重做任务；标记不完整时改为 `uncertain`，不自动恢复执行。未来 schema 拒绝覆盖。这些规则不是 exactly-once。入队过程在任务文件与索引之间仍有崩溃窗口，本原型不把该窗口说成已经关闭。

## 5. Grok Bot 后续路线

本轮不实现这一节。手动隔离原型只是以后可能适配的前置账本，不是平台接入。

依据 2026-11-18 读取的 [Skills and routines](https://docs.x.ai/grok-bot/skills-routines-and-automations)：指定 Bot 所属 routine 可以添加 webhook。桌面上该 routine 的 Webhook 节给出 POST URL、作为 `Authorization: Bearer` 发送的 key，以及可复制的完整 header。POST 可以带 JSON 正文，Bot 收到的是正文加上 routine 指令。文档写明 `200` 只表示 Grok Bot 接受调用并启动了一次 run，不表示 Bot 已经做完；结果要到该 Bot 的对话里查看。其他响应表示 run 没有启动。该页没有公开返回 job id，也没有公开给第三方的结果查询接口。手机端 routine 的 Run history 会显示 Running、Succeeded 或 Failed，那是产品内的记录，不是本仓库可以调用的外部查询 API。

三个名称只在本文定义，代码里不增加字段、枚举或网络适配器：

- `wake_channel`：将来唤醒指定 Bot 时，文档给出的正式入口，即该 Bot 所属 routine 的 webhook。它不是任务成功，也不是本原型的配置。本轮不保存 URL、key 或 header，不发起 POST。
- `wake_accepted`：对方接受并启动了一次 run。对上述文档，这只对应 HTTP 200。不得把 200 或任何传输层确认写成任务状态 `succeeded`。
- `result_delivery`：把结果送到 Bot 对话或操作者能读到的外部位置。这需要单独做的出站联通和授权验证，目前没有接通。本地账本 `succeeded` 不是 `result_delivery`。在没有公开 job id 和外部查询接口的情况下，本设计不编造查询句柄。

外部 payload、队列正文和 webhook 正文永远是数据。routine 里固定的 Instruction，以及用户在对话中的明确请求，才是授权。普通后台 worker 不能凭队列数据启动本 Bot 的智能回合。不承诺当前对话能被任意 worker 调用。禁止把 payload 变成任意 shell。

## 6. 验证边界

原型测试用临时目录和注入的时钟，不访问网络。符号链接用例必须真的创建链接。跑不通的环境不得用口头演练代替，必须记为未验证。Windows 上现有的 `scripts/test.ps1` 不编译、不运行本原型；本轮不把原型塞进该门禁，也不据此宣称生产门禁覆盖了它。
