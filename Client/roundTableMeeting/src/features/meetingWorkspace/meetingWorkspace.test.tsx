import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ConfigProvider } from 'antd'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mapMeetingDetails } from './api/mappers'
import { JoinMeetingDialog } from './components/JoinMeetingDialog'
import { MeetingHeader } from './components/MeetingHeader'

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

  it('does not expose invitation controls to ordinary members', () => {
    render(<ConfigProvider><MeetingHeader meeting={{ meetingId: 'm', name: 'Meeting', canDelete: false, canManageInvitation: false, participants: [] }} onInvitation={vi.fn()} onHideInvitation={vi.fn()} onCopy={vi.fn()} onRefresh={vi.fn()} onDelete={vi.fn()} /></ConfigProvider>)
    expect(screen.queryByRole('button', { name: '显示邀请码' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '删除会议' })).not.toBeInTheDocument()
  })
})
