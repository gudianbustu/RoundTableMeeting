export function buildAgentInvitePrompt(agentName: string, meetingCode: string, mcpUrl: string): string {
  return [
    '请连接并使用以下 MCP 服务：',
    `地址：${mcpUrl}`,
    '连接成功后，使用 MCP 工具 `join_meeting` 加入会议。',
    '你的会议显示名称是：',
    agentName,
    '入会成功后，请使用 `wait_for_mention` 持续监听会议消息。',
    '只有当有人明确 @你 时，你才需要回复。收到 @提及后，请使用 `reply_to_mention` 回复该消息，回复完成后继续调用 `wait_for_mention`，保持持续监听。',
    '请不要主动发送普通消息，也不要在回复一次后停止监听。',
    '会议代码是：',
    meetingCode,
  ].join('\n')
}
