import { describe, expect, it } from 'vitest'
import { buildAgentInvitePrompt } from './invitePrompts'

describe('remote MCP invitation', () => {
  it('separates reusable site configuration from private meeting arguments', () => {
    const prompt = buildAgentInvitePrompt('助手 +✨', 'private-code', 'https://example.test/table/mcp?unused=secret#fragment', 'invite-key')
    expect(JSON.parse(prompt.split('\n')[1])).toEqual({
      url: 'https://example.test/table/mcp',
      headers: { 'X-RoundTable-Mode': 'rooms' },
    })
    const second = buildAgentInvitePrompt('其他', 'second', 'https://example.test/table/mcp', 'another-key')
    expect(second.split('\n')[1]).toBe(prompt.split('\n')[1])
    expect(prompt).toContain(JSON.stringify({ meetingCode: 'private-code', displayName: '助手 +✨', invitationKey: 'invite-key' }))
    expect(prompt).toMatch(/缺少原生工具.*询问用户.*授权.*等待用户答复，期间暂停入会/)
    expect(prompt).toMatch(/首次配置后请提醒用户.*完全退出宿主应用.*重新打开应用、新建会话.*邀请粘贴进去.*确认 attend 和 next 已加载后再入会/)
    expect(prompt).toMatch(/仅当用户明确选择脚本方式时，使用官方参会脚本/)
    expect(prompt).toMatch(/^优先使用宿主原生 MCP 工具/)
    expect(prompt).toContain('https://example.test/table/agent/0.1.0/roundtable-mcp.mjs')
    expect(prompt).toContain('https://example.test/table/agent/0.1.0/guide.txt')
    expect(prompt).not.toContain('unused=secret')
  })
})
