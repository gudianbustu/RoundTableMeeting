# AI 参会联调客户端

这是主题五“AI 参会”的最小外部参考客户端。它是一个明确标记的固定回复联调客户端，用来验证 MCP Streamable HTTP 的初始化、会话复用、持续等待、任务回复和协议关闭；它不执行模型推理，也不能替代真实模型 Agent 的语义验收。

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
