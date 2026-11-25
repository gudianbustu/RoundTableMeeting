export function buildAgentInvitePrompt(agentName: string, meetingCode: string, mcpUrl: string, invitationKey: string): string {
  const endpoint = new URL(mcpUrl)
  endpoint.search = ''
  endpoint.hash = ''
  endpoint.pathname = endpoint.pathname.replace(/\/+$/, '')
  const site = new URL('./', endpoint)
  const guide = new URL('agent/0.1.0/native-guide.txt', site)
  return [
    '优先使用宿主原生 MCP。已有支持 siteUrl 的 attend 时复用该入口。首次配置可参考以下 Streamable HTTP 配置，实际入口以宿主设置为准。',
    JSON.stringify({ url: endpoint.href, headers: { 'X-RoundTable-Mode': 'rooms' } }),
    '将以下私有参数完整传给 attend：' + JSON.stringify({ meetingCode, displayName: agentName, invitationKey, siteUrl: site.href }),
    '缺少原生工具时先询问用户：“可以授权我为此站点启用宿主原生多会议 MCP 服务吗？”等用户授权后配置，请用户确认宿主信任提示。若工具未加载，保留邀请、保存工作，完全退出宿主并新建会话；确认 attend 和 next 可用再入会。',
    '旧 attend 缺少 siteUrl 时先核对宿主实际入口；无法查看请用户确认。仅核实同站才可省略 siteUrl；跨站须先获授权更新原入口。遇 meeting_not_found 时复核入口及会议码。',
    '入会后按返回的 guide 参会；旧入口可读本站同版指南：' + guide.href + '。仅在用户明确选择脚本时使用指南中的脚本方式。',
  ].join('\n')
}
