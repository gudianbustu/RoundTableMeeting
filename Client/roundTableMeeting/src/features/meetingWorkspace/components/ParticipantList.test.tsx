import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it } from 'vitest'
import { ParticipantList } from './ParticipantList'
import type { AgentActivity } from '../types/realtimeChat'

const agent = { participantId: 'worker', displayName: 'Worker', type: 'agent' as const, isCurrentUser: false, isCreator: false }
const activity: AgentActivity = { meetingId: 'meeting', participantId: 'worker', state: 'blocked', changedAt: new Date(), revision: 2 }
afterEach(cleanup)

describe('Agent participant recovery status', () => {
  it.each([
    ['activating', '连接建立中', false],
    ['initial_context_unconfirmed', '等待接收确认', true],
    ['awaiting_context_confirmation', '等待接收确认', true],
    ['retry_pending', '重试等待', true],
    ['retry_exhausted', '任务失败，等待重试', true],
  ] as const)('shows %s from activity rather than unversioned presence', (reason, label, online) => {
    render(<ParticipantList participants={[agent]} presence={{ worker: online ? 'offline' : 'online' }} agentActivities={{ worker: { ...activity, reason } }} />)
    expect(screen.getByText(label)).toBeInTheDocument()
    expect(screen.queryByText('在线') !== null).toBe(online)
  })

  it('keeps a single Agent after takeover and ignores a stale online presence event', () => {
    render(<ParticipantList participants={[agent]} presence={{ worker: 'online' }} agentActivities={{ worker: { ...activity, state: 'disconnected', reason: 'connection_lease_expired', connectionDisposition: 'taken_over' } }} />)
    expect(screen.getAllByText('Worker')).toHaveLength(1)
    expect(screen.getByText('连接中断')).toBeInTheDocument()
    expect(screen.getByText('已由新连接接管')).toBeInTheDocument()
    expect(screen.queryByText('在线')).not.toBeInTheDocument()
  })
})
