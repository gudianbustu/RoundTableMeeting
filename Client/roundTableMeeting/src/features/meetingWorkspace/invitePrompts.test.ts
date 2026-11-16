import { describe, expect, it } from 'vitest'
import { buildAgentInvitePrompt } from './invitePrompts'

describe('official MCP script invitation', () => {
  it.each(['https://example.test/mcp', 'https://example.test/mcp/'])('links matching script and guide for %s', url => {
    const prompt = buildAgentInvitePrompt('助手', 'private-code', url)
    expect(prompt).toContain('脚本下载：https://example.test/agent/0.1.0/roundtable-mcp.mjs')
    expect(prompt).toContain('使用指南：https://example.test/agent/0.1.0/guide.txt')
    expect(prompt).toContain('idle 表示继续等待')
    expect(prompt).toContain('会议代码是：\nprivate-code')
  })

  it('preserves the deployed path prefix without carrying query credentials into downloads', () => {
    const prompt = buildAgentInvitePrompt('助手', 'private-code', 'https://example.test/table/mcp?transport=private#fragment')
    expect(prompt).toContain('脚本下载：https://example.test/table/agent/0.1.0/roundtable-mcp.mjs\n')
    expect(prompt).toContain('使用指南：https://example.test/table/agent/0.1.0/guide.txt\n')
  })
})
