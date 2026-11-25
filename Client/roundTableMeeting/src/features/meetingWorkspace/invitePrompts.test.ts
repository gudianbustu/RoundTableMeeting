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
    expect(prompt).toContain(JSON.stringify({ meetingCode: 'private-code', displayName: '助手 +✨', invitationKey: 'invite-key', siteUrl: 'https://example.test/table/' }))
    expect(prompt).toMatch(/缺少原生工具.*询问用户.*授权.*等用户授权后配置/)
    expect(prompt).toMatch(/旧 attend.*核对宿主实际入口.*核实同站才可省略 siteUrl/)
    expect(prompt).toContain('完全退出宿主并新建会话')
    expect(prompt).toMatch(/仅在用户明确选择脚本时/)
    expect(prompt).toContain('https://example.test/table/agent/0.1.0/native-guide.txt')
    expect(prompt).not.toContain('clientSubmissionId')
    expect(buildAgentInvitePrompt('助手 +✨', 'private-code', 'https://example.test/table/mcp/', 'invite-key')).toBe(prompt)
    expect(prompt).not.toContain('unused=secret')
  })
})
