import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfigProvider } from 'antd'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mapMeetingDetails } from './api/mappers'
import { InviteMeetingDialog } from './components/InviteMeetingDialog'
import { JoinMeetingDialog } from './components/JoinMeetingDialog'
import { MeetingHeader } from './components/MeetingHeader'
import { ParticipantList } from './components/ParticipantList'
import { buildAgentInvitePrompt } from './invitePrompts'
import { hasUnknownAgentActivity, hasUnknownOnlineParticipant } from './pages/MeetingWorkspacePage'

afterEach(cleanup)

describe('meeting workspace contracts', () => {
  it('maps the generated string participant enum and full participant list', () => {
    const result = mapMeetingDetails({
      meetingId: '11111111-1111-1111-1111-111111111111',
      name: 'Review',
      canDelete: false,
      canManageInvitation: false,
      participants: [{ participantId: '22222222-2222-2222-2222-222222222222', displayName: 'Assistant', type: 'agent', isCreator: false, isCurrentUser: false }],
    })
    expect(result.participants).toHaveLength(1)
    expect(result.participants[0].type).toBe('agent')
  })

  it('requires standard GUID D format before preview and normalizes casing', async () => {
    const preview = vi.fn()
    const actor = userEvent.setup()
    render(<ConfigProvider><JoinMeetingDialog open onCancel={vi.fn()} onPreview={preview} onSubmit={vi.fn()} /></ConfigProvider>)
    const input = screen.getByPlaceholderText('输入标准 GUID 会议码')
    await actor.type(input, 'NOT-A-GUID')
    expect(screen.getByRole('button', { name: '检查会议码' })).toBeDisabled()
    await actor.clear(input)
    await actor.type(input, '8F14E45F-EA5E-4F5A-A7E9-2C9D5A02B761')
    await actor.click(screen.getByRole('button', { name: '检查会议码' }))
    expect(preview).toHaveBeenCalledWith('8f14e45f-ea5e-4f5a-a7e9-2c9d5a02b761')
  })

  it('exposes invitation controls to ordinary human members', () => {
    render(<ConfigProvider><MeetingHeader meeting={{ meetingId: 'm', name: 'Meeting', canDelete: false, canManageInvitation: true, participants: [] }} invitation={{ meetingId: 'm', meetingCode: '0e04091b-4287-4b4c-8c99-70217a3aeea0' }} onCopy={vi.fn()} onRefresh={vi.fn()} /></ConfigProvider>)
    expect(screen.queryByRole('button', { name: '邀请入会' })).not.toBeInTheDocument()
    expect(screen.getByText('0e04091b-4287-4b4c-8c99-70217a3aeea0')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '复制邀请码' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: '删除会议' })).not.toBeInTheDocument()
  })

  it('renders icon-only header actions with accessible labels for creators', async () => {
    render(<ConfigProvider><MeetingHeader meeting={{ meetingId: 'm', name: 'Meeting', canDelete: true, canManageInvitation: true, participants: [] }} invitation={{ meetingId: 'm', meetingCode: '0e04091b-4287-4b4c-8c99-70217a3aeea0' }} onCopy={vi.fn()} onRefresh={vi.fn()} /></ConfigProvider>)
    for (const label of ['刷新会议', '复制邀请码']) {
      expect(screen.getByRole('button', { name: label })).toBeInTheDocument()
    }
    expect(screen.queryByRole('button', { name: '隐藏邀请码' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '显示邀请码' })).not.toBeInTheDocument()
    expect(screen.getByText('0e04091b-4287-4b4c-8c99-70217a3aeea0').closest('.meeting-code-row')).toContainElement(screen.getByRole('button', { name: '复制邀请码' }))
    expect(screen.queryByText('删除会议')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '更多会议操作' })).not.toBeInTheDocument()
  })

  it('shows site address and meeting code when inviting a human', async () => {
    const actor = userEvent.setup()
    render(<ConfigProvider><InviteMeetingDialog open invitation={{ meetingId: 'm', meetingCode: '0e04091b-4287-4b4c-8c99-70217a3aeea0' }} onCancel={vi.fn()} siteUrl="http://test.local" mcpUrl="http://test.local/mcp" /></ConfigProvider>)
    await actor.click(screen.getByRole('button', { name: '邀请人类成员' }))
    expect(screen.getByText('http://test.local')).toBeInTheDocument()
    expect(screen.getByText('0e04091b-4287-4b4c-8c99-70217a3aeea0')).toBeInTheDocument()
  })

  it('builds the agent MCP prompt from display name and meeting code', async () => {
    const actor = userEvent.setup()
    render(<ConfigProvider><InviteMeetingDialog open invitation={{ meetingId: 'm', meetingCode: '0e04091b-4287-4b4c-8c99-70217a3aeea0' }} onCancel={vi.fn()} siteUrl="http://test.local" mcpUrl="http://test.local/mcp" /></ConfigProvider>)
    await actor.click(screen.getByRole('button', { name: '邀请 AI Agent' }))
    expect(screen.queryByText(/join_meeting/)).not.toBeInTheDocument()
    await actor.type(screen.getByLabelText('Agent 显示名称'), '助手一号')
    expect(screen.getByText(/X-RoundTable-Mode/)).toHaveTextContent('助手一号')
    expect(screen.getByRole('button', { name: '复制提示词' })).toBeEnabled()
  })

  it('builds the agent prompt with MCP address, display name and meeting code', () => {
    const prompt = buildAgentInvitePrompt('助手一号', '0e04091b', 'http://test.local/mcp', 'invite-key')
    expect(prompt).toContain(JSON.stringify({ meetingCode: '0e04091b', displayName: '助手一号', invitationKey: 'invite-key' }))
    expect(prompt).toContain('调用 next')
  })

  it('detects an online participant missing from the current detail snapshot', () => {
    const participants = [{ participantId: '11111111-1111-1111-1111-111111111111', displayName: 'Alice', type: 'human' as const, isCreator: true, isCurrentUser: true }]
    expect(hasUnknownOnlineParticipant(participants, {
      '11111111-1111-1111-1111-111111111111': 'online',
      '22222222-2222-2222-2222-222222222222': 'online',
    })).toBe(true)
    expect(hasUnknownOnlineParticipant(participants, {
      '11111111-1111-1111-1111-111111111111': 'online',
      '22222222-2222-2222-2222-222222222222': 'offline',
    })).toBe(false)
  })

  it('detects Agent activity that arrives before participant details', () => {
    const participants = [{ participantId: '11111111-1111-1111-1111-111111111111', displayName: 'Alice', type: 'human' as const, isCreator: true, isCurrentUser: true }]
    expect(hasUnknownAgentActivity(participants, {
      '22222222-2222-4222-8222-222222222222': { meetingId: '33333333-3333-4333-8333-333333333333', participantId: '22222222-2222-4222-8222-222222222222', state: 'waiting', changedAt: new Date() },
    })).toBe(true)
  })

  it('does not let retired Agent activity override current presence', () => {
    const participant = { participantId: '22222222-2222-4222-8222-222222222222', displayName: 'Assistant', type: 'agent' as const, isCreator: false, isCurrentUser: false }
    render(<ConfigProvider><ParticipantList participants={[participant]} presence={{ [participant.participantId]: 'offline' }} agentActivities={{ [participant.participantId]: { meetingId: '11111111-1111-4111-8111-111111111111', participantId: participant.participantId, state: 'waiting', changedAt: new Date() } }} /></ConfigProvider>)
    expect(screen.getByText('在线 · 0')).toBeInTheDocument()
    expect(screen.queryByText('等待中')).not.toBeInTheDocument()
    expect(screen.getByText('离线 · 1')).toBeInTheDocument()
  })
})
