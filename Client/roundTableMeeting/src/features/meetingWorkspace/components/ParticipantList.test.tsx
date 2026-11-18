import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ParticipantList } from './ParticipantList'
const agent = { participantId: 'worker', displayName: 'Worker', type: 'agent' as const, isCurrentUser: false, isCreator: false }
afterEach(cleanup)
it('uses presence for online grouping without exposing retired task activity', () => {
  const onPrivateChat = vi.fn()
  render(<ParticipantList participants={[agent]} presence={{ worker: 'online' }} onPrivateChat={onPrivateChat} agentActivities={{ worker: { meetingId: 'meeting', participantId: 'worker', state: 'blocked', reason: 'retry_exhausted', changedAt: new Date() } }} />)
  expect(screen.getByText('在线 · 1')).toBeInTheDocument()
  expect(screen.queryByText('任务失败，等待重试')).not.toBeInTheDocument()
  fireEvent.click(screen.getByRole('button', { name: '私聊 Worker' }))
  expect(onPrivateChat).toHaveBeenCalledWith(agent)
})
it('collapses offline members until requested and preserves mention actions', () => {
  const onMention = vi.fn()
  render(<ParticipantList participants={[agent]} onMention={onMention} />)
  expect(screen.queryByText('Worker')).not.toBeInTheDocument()
  fireEvent.click(screen.getByText('离线 · 1'))
  fireEvent.click(screen.getByRole('button', { name: '提及 Worker' }))
  expect(onMention).toHaveBeenCalledWith(agent)
})

it('omits departed members even with a stale online presence', () => {
  render(<ParticipantList participants={[{ ...agent, membershipState: 'left' }]} presence={{ worker: 'online' }} />)
  expect(screen.queryByText('Worker')).not.toBeInTheDocument()
  expect(screen.getByText('暂无参与者')).toBeInTheDocument()
})
