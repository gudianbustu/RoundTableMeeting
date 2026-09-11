# AI 参会联调客户端

本 Demo 有两个入口：`index.mjs` 保留主题五的 legacy 固定回复闭环；`reliable.mjs` 使用主题六 explicit_ack_v1，支持私有恢复状态、显式确认、独立续租、失败阻塞、恢复和主动接管。下文原有配置与流程适用于 legacy 入口，可靠模式配置见最后一节。固定回复结果不能替代真实模型语义验收。

客户端只依赖 Node.js 24 原生 fetch，不依赖 Codex、MCP 客户端插件或外部 npm 包。服务端需要先启动，并在可访问地址提供 /mcp。

## 配置

PowerShell 示例：

    $env:MCP_URL = "http://localhost:5043/mcp"
    $env:MEETING_CODE = "<会议码>"
    $env:AGENT_NAME = "联调助手"
    $env:CONTEXT_CHARACTER_LIMIT = "12000"
    $env:AGENT_REPLY = "这是固定回复联调客户端，请将它替换为真实模型生成内容。"
    node .\index.mjs

可用环境变量：

| 变量 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| MCP_URL | 否 | http://localhost:5043/mcp | MCP Streamable HTTP 地址；远程或容器环境使用服务端实际可访问地址 |
| MEETING_CODE | 是 | 无 | 目标会议码；只从环境变量读取，不写入仓库 |
| AGENT_NAME | 否 | ReferenceAgent | Agent 在会议中的显示名称 |
| CONTEXT_CHARACTER_LIMIT | 否 | 12000 | 传给 join_meeting 的上下文字符预算 |
| AGENT_REPLY | 否 | 固定联调文案 | 固定回复内容；不会输出到客户端日志 |
| WAIT_REQUEST_TIMEOUT_MS | 否 | 0 | 本地请求超时，0 表示不主动取消 wait；它不能改变服务端等待上限 |

服务端默认 AgentCollaboration:WaitTimeoutSeconds 为 300 秒。客户端超时只能取消本地 HTTP 请求；需要较短的 idle 周期时，在测试实例设置 AgentCollaboration__WaitTimeoutSeconds，不要向 wait_for_mention 添加未定义参数。

请求超时覆盖响应头和响应正文/SSE 的完整读取过程。设置的时限应大于服务端等待上限；超时属于本次客户端运行失败，主循环会报告错误并明确关闭协议会话，不把超时当成服务端已断开或任务已恢复。运行 `node --test index.test.mjs` 可验证响应正文停滞时仍能超时退出并关闭远端会话。

## 运行流程

客户端只初始化一次 MCP 会话，并复用服务端返回的 Mcp-Session-Id：

    initialize -> notifications/initialized -> tools/list -> join_meeting
    -> 首次 wait_for_mention -> idle/mention 循环
    -> mention 时 reply_to_mention -> 再次 wait_for_mention
    -> 停止时取消本地挂起请求 -> MCP DELETE

idle 是正常的空闲结果，客户端会继续使用同一个协议会话和业务 connectionSessionId 等待。客户端不会重复 join_meeting，也不会并行执行多个 wait/reply 业务循环。

收到 mention 时，客户端原样携带服务端返回的 taskId、deliveryAttemptId 和 replyIdempotencyKey 调用 reply_to_mention。收到 task_not_available 时会先调用 get_agent_status 记录诊断，然后停止循环，避免把队首阻塞当成 idle 反复忙等。

使用 Ctrl+C 停止时，客户端会先停止新的业务调用、取消本地挂起请求，再发送带 Mcp-Session-Id 的协议级 DELETE。只有 DELETE 返回成功才报告远端会话关闭成功；DELETE 失败会以失败状态结束进程，不能仅凭本地进程退出推断网页已经离线。

日志只记录阶段、结果、数量和脱敏后的 ID 摘要，不记录会议码、完整上下文、完整回复、MCP 会话标识或请求正文。

## 真实模型验收

本 Demo 的固定回复只能证明传输和任务闭环。真实模型 Agent 应使用同样的工具顺序，持续 wait_for_mention，对至少两条内容不同的 @Agent 消息分别作答，并在每次回复后继续等待；验收还应覆盖多次 idle、浏览器刷新和明确关闭后的离线状态。

## 可靠模式

在同一目录执行 `node reliable.mjs`，仍使用 MCP_URL、MEETING_CODE、AGENT_NAME、CONTEXT_CHARACTER_LIMIT。默认使用固定回复；配置模型后使用兼容 OpenAI 的 Chat Completions 接口。Node.js 24，无额外依赖。

| 环境变量 | 默认值 | 含义 |
| --- | --- | --- |
| AGENT_JOIN_MODE | create | 新状态首次加入用 create；只有明确指定 takeover 才替换同名在线/离线 Agent |
| AGENT_STATE_FILE | 当前用户本地状态目录/身份摘要/state.json | 必须位于专用私有目录；保存加入操作、恢复密钥、业务连接、上下文及尚未提交的回复，不放入仓库或共享目录 |
| AGENT_WAIT_SECONDS | 30 | 每次服务端 wait 秒数，上限取服务端公布值 |
| AGENT_REQUEST_TIMEOUT_MS | 15000 | 普通协议调用超时；wait 在此基础上加服务端等待时长 |
| AGENT_MODEL_BASE_URL | 无 | 兼容接口 Base URL，例如 https://provider.example/v1；末尾自动添加 /chat/completions |
| AGENT_MODEL | 无 | 实际模型名称；与 Base URL 一起配置 |
| AGENT_MODEL_KEY_ENV | OPENAI_API_KEY | 保存模型密钥的环境变量名；日志不输出密钥或模型响应 |
| AGENT_MODEL_TIMEOUT_MS | 0 | 可选模型请求超时，0 为不设置本地模型时限；独立续租持续运行 |
| AGENT_GENERATION_DELAY_MS | 0 | 联调故障点：生成前延迟，用于验证处理跨租约周期 |
| AGENT_DROP_RESPONSES | 无 | 逗号分隔的工具名，各丢弃一次已收到的响应并重放；wait 只丢 mention；confirm_context:task 可只丢任务确认 |
| AGENT_FAULT | 无 | permanent_failure：第一个确认的任务主动报告永久失败，之后等待显式重试 |

首次调用前先落盘随机 recoveryKey 和 operationId。再次使用同一状态文件会恢复原业务连接 ID、代数和预算；不会因设置 takeover 而把既有状态转换为新身份。要明确接管，应使用新的专用状态目录并设置 AGENT_JOIN_MODE=takeover。会议码与名称授权接管；恢复密钥只授权恢复原业务连接。可靠加入的 Activating 事务一旦提交，该参与者即不能回退 legacy，即使后续激活失败；未提交的请求不会升级。

Windows 自动设置专用目录仅当前用户可访问，并以命名管道互斥；进程崩溃后操作系统释放互斥。Unix 使用 0700 目录、0600 文件和独占创建的锁文件；已有锁一律返回 state_locked，不按 PID 自动删除，避免并发回收误删新进程的锁。Unix 崩溃后须先停止所有使用该状态文件的客户端，再手动删除同路径的 `.lock` 文件，然后以原状态文件恢复；不要删除 state.json。状态文件原子替换并刷盘。该文件含上下文与凭据，应按秘密保存；不要把状态目录指向已有业务文件目录。

业务循环串行执行 status → wait → 保存 mention → confirm → 生成并保存正文 → reply，同时按服务端建议周期独立 renew。耗尽时业务暂停，续租持续；终端输入 `retry` 才为当前已显示的阻塞队首创建新重试操作，也可输入 `retry <taskId>` 限定目标。输入 `quit` 或 Ctrl+C 停止、取消等待并发送 MCP DELETE；关闭失败会明确报告。

响应不确定时先在原传输重放相同操作。join/resume 的 operation_pending 在同传输按 250/500/1000/2000 毫秒最多重查四次；持续未完成则停止并保留原操作。暂时不可用及 agent_operation_conflict 条件写冲突先同传输重试两次，保持原参数与幂等键并遵守 retryAfterSeconds；operation_conflict 指纹不匹配直接停止；停止与切换恢复周期会取消退避。连续十轮握手后仍无业务进展会停止恢复，不能仅凭握手成功清零失败次数。

传输失效或旧 resume 操作已被补偿为失效时，先关闭旧会话，再建立新 MCP 会话并 resume；新传输使用新的 resume operationId，同一传输重放保留原值。恢复时先查已保存任务：已回复只清理本地待提交状态；旧尝试失效则丢弃旧正文，等待新尝试。服务端省略已确认初始上下文时保留本地原历史。连接被接管返回 connection_superseded 时停止，不反向接管。已生成正文在回复前落盘可避免同一有效尝试重复生成；进程在生成与保存之间崩溃仍可能再次生成，不承诺外部副作用恰好一次。

运行 `node --test index.test.mjs reliable.test.mjs reliable-recovery.test.mjs reliable-state.test.mjs reliable-conflict.test.mjs` 验证传输超时/多路响应、状态互斥、Unix 遗留锁并发拒绝与人工清理后单一归属、丢响应重放、恢复后的回复识别和尝试隔离、独立续租、显式重试、有界激活重查和可取消退避。真实模型、十分钟驻留、进程崩溃和浏览器验收以主题六 testPlan 的实际结果为准。
