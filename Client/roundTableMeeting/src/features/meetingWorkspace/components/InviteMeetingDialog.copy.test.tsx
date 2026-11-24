import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { InviteMeetingDialog } from './InviteMeetingDialog'
import { buildAgentInvitePrompt } from '../invitePrompts'

const invitation = { meetingId: 'meeting', meetingCode: 'copy-code' }
let copied: string[]
const original = Object.getOwnPropertyDescriptor(navigator, 'clipboard')
beforeEach(() => {
  copied = []
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined })
  Object.defineProperty(document, 'execCommand', { configurable: true, value: vi.fn(() => { copied.push((document.activeElement as HTMLTextAreaElement).value); return true }) })
})
afterEach(() => {
  cleanup(); vi.restoreAllMocks()
  if (original) Object.defineProperty(navigator, 'clipboard', original)
  else Reflect.deleteProperty(navigator, 'clipboard')
  Reflect.deleteProperty(document, 'execCommand')
})
it('copies human invitation address and code on HTTP', async () => {
  render(<InviteMeetingDialog open meetingName={'  设计\n  评审  '} invitation={invitation} onCancel={() => {}} siteUrl="http://test.local" />)
  fireEvent.click(screen.getByRole('radio', { name: '人类成员' }))
  fireEvent.click(screen.getByRole('button', { name: '复制完整邀请文案' }))
  await waitFor(() => expect(copied).toEqual(['邀请你加入「设计 评审」。\n\n网站地址：http://test.local\n\n会议码：copy-code']))
})
it('copies the complete multiline Agent prompt on HTTP', async () => {
  render(<InviteMeetingDialog open invitation={invitation} onCancel={() => {}} mcpUrl="http://test.local/mcp" />)
  fireEvent.click(screen.getByRole('radio', { name: 'AI Agent' }))
  fireEvent.change(screen.getByLabelText('Agent 显示名称'), { target: { value: '助手😀' } })
  fireEvent.click(screen.getByRole('button', { name: '复制 MCP 接入提示词' }))
  await waitFor(() => expect(copied).toHaveLength(1))
  const key = JSON.parse(copied[0].split('本次 attend 的私有参数：')[1].split('\n')[0]).invitationKey
  expect(key).toMatch(/^[a-f0-9]{32}$/)
  expect(copied[0]).toBe(buildAgentInvitePrompt('助手😀', 'copy-code', 'http://test.local/mcp', key))
})
