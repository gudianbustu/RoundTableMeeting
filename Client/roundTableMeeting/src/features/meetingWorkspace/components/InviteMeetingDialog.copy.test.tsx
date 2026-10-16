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
  render(<InviteMeetingDialog open invitation={invitation} onCancel={() => {}} siteUrl="http://test.local" />)
  fireEvent.click(screen.getByRole('button', { name: '邀请人类成员' }))
  fireEvent.click(screen.getByRole('button', { name: '复制网站地址' }))
  fireEvent.click(screen.getByRole('button', { name: '复制会议码' }))
  await waitFor(() => expect(copied).toEqual(['http://test.local', 'copy-code']))
})
it('copies the complete multiline Agent prompt on HTTP', async () => {
  render(<InviteMeetingDialog open invitation={invitation} onCancel={() => {}} mcpUrl="http://test.local/mcp" />)
  fireEvent.click(screen.getByRole('button', { name: '邀请 AI Agent' }))
  fireEvent.change(screen.getByLabelText('Agent 显示名称'), { target: { value: '助手😀' } })
  fireEvent.click(screen.getByRole('button', { name: '复制提示词' }))
  await waitFor(() => expect(copied).toEqual([buildAgentInvitePrompt('助手😀', 'copy-code', 'http://test.local/mcp')]))
})
