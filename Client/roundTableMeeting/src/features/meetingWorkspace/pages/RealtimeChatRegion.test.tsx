import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import type { ChatConnectionCallbacks } from '../api/meetingChatConnection'
import { RealtimeChatRegion } from './RealtimeChatRegion'

const mocks = vi.hoisted(() => ({ callbacks: [] as ChatConnectionCallbacks[], list: vi.fn() }))
vi.mock('../api/listMessages', () => ({ listMessages: mocks.list }))
vi.mock('../api/meetingChatConnection', () => ({ MeetingChatConnection: class {
  constructor(callbacks: ChatConnectionCallbacks) { mocks.callbacks.push(callbacks) }
  async start() { return { onlineParticipantIds: [], highWatermarkSequence: 0, agentActivities: [] } }
  async stop() {}
} }))
vi.mock('../components/MessageComposer', () => ({ MessageComposer: ({ value, onChange }: { value: string; onChange: (value: string) => void }) => <textarea aria-label="draft" value={value} onChange={event => onChange(event.target.value)} /> }))
vi.mock('../components/ChatConnectionStatus', () => ({ ChatConnectionStatus: ({ onRetry }: { onRetry: () => void }) => <button onClick={onRetry}>Reconnect</button> }))
vi.mock('../components/MessageTimeline', () => ({ MessageTimeline: ({ messages }: { messages: { content: string }[] }) => <div data-testid="messages">{messages.map(m => m.content).join(',')}</div> }))
afterEach(() => { cleanup(); mocks.callbacks.length = 0; vi.clearAllMocks() })

it('retires the previous connection on manual reconnect while preserving the unsent draft', async () => {
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  const unavailable = vi.fn()
  render(<RealtimeChatRegion meetingId="11111111-1111-4111-8111-111111111111" participants={[]} onMeetingUnavailable={unavailable} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  const old = mocks.callbacks[0]
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'Keep this unsent question' } })
  fireEvent.click(screen.getByText('Reconnect'))
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(2))
  await act(async () => { old.onDeleted() })
  expect(unavailable).not.toHaveBeenCalled()
  expect(screen.getByLabelText('draft')).toHaveValue('Keep this unsent question')
})

it('rejects late activity, messages, resubscriptions and deletion callbacks after switching or deleting a meeting', async () => {
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  const firstMeeting = '11111111-1111-4111-8111-111111111111'
  const secondMeeting = '22222222-2222-4222-8222-222222222222'
  const unavailable = vi.fn()
  const activities = vi.fn()
  const props = { participants: [], onMeetingUnavailable: unavailable, onAgentActivitiesChanged: activities }
  const view = render(<RealtimeChatRegion {...props} meetingId={firstMeeting} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  const old = mocks.callbacks[0]
  view.rerender(<RealtimeChatRegion {...props} meetingId={secondMeeting} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(2))
  const current = mocks.callbacks[1]
  const activity = (meetingId: string) => ({ meetingId, participantId: 'agent', state: 'waiting' as const, changedAt: new Date(), revision: 99 })
  const message = (meetingId: string) => ({ meetingId, messageId: 'late', sequence: 1, author: { participantId: 'human', kind: 'human' as const, displayName: 'Alice' }, content: 'late message', mentions: [], createdAt: new Date() })
  await act(async () => {
    old.onAgentActivity(activity(firstMeeting))
    old.onMessage(message(firstMeeting))
    old.onDeleted()
    old.onResubscribed?.({ onlineParticipantIds: ['old'], highWatermarkSequence: 1, agentActivities: [activity(firstMeeting)] })
    current.onAgentActivity(activity(firstMeeting))
    current.onMessage(message(firstMeeting))
  })
  expect(unavailable).not.toHaveBeenCalled()
  expect(mocks.list).toHaveBeenCalledTimes(2)
  expect(activities).toHaveBeenLastCalledWith({})
  expect(screen.getByTestId('messages')).toBeEmptyDOMElement()
  await act(async () => { current.onAgentActivity(activity(secondMeeting)) })
  expect(activities).toHaveBeenLastCalledWith({ agent: expect.objectContaining({ meetingId: secondMeeting }) })
  await act(async () => {
    current.onDeleted()
    current.onAgentActivity(activity(secondMeeting))
    current.onMessage(message(secondMeeting))
    current.onDeleted()
  })
  expect(unavailable).toHaveBeenCalledOnce()
  expect(activities).toHaveBeenLastCalledWith({})
  expect(screen.getByTestId('messages')).toBeEmptyDOMElement()
})
