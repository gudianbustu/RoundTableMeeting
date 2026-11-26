import { createRef, type ReactNode } from 'react'
import type { MessageComposerHandle } from '../components/MessageComposer'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import type { ChatConnectionCallbacks } from '../api/meetingChatConnection'
import * as unreadContext from '../chat/AccountUnreadProvider'
import { AccountUnreadStore } from '../chat/accountUnreadStore'
import { createInitialChatState } from '../chat/messageReducer'
import { RealtimeChatRegion, type ChatSessionCache } from './RealtimeChatRegion'

const mocks = vi.hoisted(() => ({ callbacks: [] as ChatConnectionCallbacks[], list: vi.fn(), send: vi.fn(), lookup: vi.fn(), upload: vi.fn() }))
vi.mock('../api/sendMessage', () => ({ sendMessage: mocks.send }))
vi.mock('../api/messageResources', () => ({ getSubmission: mocks.lookup, getCommunicationCapabilities: async () => ({ maxFileBytes: 100000000 }), attachmentUrl: () => '', listAttachments: async () => ({ items: [] }), getMessage: vi.fn(), uploadAttachment: mocks.upload }))
vi.mock('../api/listMessages', () => ({ listMessages: mocks.list }))
vi.mock('../api/meetingChatConnection', () => ({ MeetingChatConnection: class {
  constructor(callbacks: ChatConnectionCallbacks) { mocks.callbacks.push(callbacks) }
  async start() { return { onlineParticipantIds: [], highWatermarkSequence: 0, agentActivities: [] } }
  async stop() {}
} }))
vi.mock('../components/MessageComposer', () => ({ MessageComposer: ({ value, onChange, onSend, send, disabled }: { value: string; onChange: (value: string) => void; onSend: () => void; send: string; disabled: boolean }) => <><textarea aria-label="draft" disabled={disabled} value={value} onChange={event => onChange(event.target.value)} /><button onClick={onSend}>Send</button><span data-testid="send-state">{send}</span></> }))
vi.mock('../components/ChatConnectionStatus', () => ({ ChatConnectionStatus: ({ onRetry }: { onRetry: () => void }) => <button onClick={onRetry}>Reconnect</button> }))
vi.mock('../components/MessageTimeline', () => ({ MessageTimeline: ({ messages, pendingMessage }: { messages: { messageId: string; content: string }[]; pendingMessage?: ReactNode }) => <><div role="log" data-testid="messages">{messages.map(m => <span key={m.messageId} data-message-id={m.messageId}>{m.content}</span>)}</div>{pendingMessage}</> }))
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

it.each(['meeting_deleting', 'meeting_deleted'])('clears the current draft when a resync confirms %s', async code => {
  mocks.list.mockResolvedValueOnce({ items: [], hasMoreBefore: false, hasMoreAfter: false })
    .mockRejectedValueOnce({ code, status: 410 })
  const unavailable = vi.fn()
  render(<RealtimeChatRegion meetingId="11111111-1111-4111-8111-111111111111" participants={[]} onMeetingUnavailable={unavailable} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'Pending content' } })
  fireEvent.click(screen.getByText('Reconnect'))
  await waitFor(() => expect(unavailable).toHaveBeenCalledOnce())
  expect(screen.getByLabelText('draft')).toHaveValue('')
  expect(screen.getByTestId('messages')).toBeEmptyDOMElement()
})

it('keeps drafts separate across public and private scopes and ignores private events in public', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  const editor = createRef<MessageComposerHandle>()
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false, visibleThroughSequence: 4 })
  render(<RealtimeChatRegion meetingId={id} composerRef={editor} participants={[{ participantId: 'bob', displayName: 'Bob', type: 'human', isCurrentUser: false, isCreator: false }]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  expect(screen.queryByRole('tablist')).toBeNull()
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'public draft' } })
  await act(async () => { mocks.callbacks[0].onMessage({ meetingId: id, messageId: 'secret', sequence: 5, visibility: 'private', recipient: { participantId: 'bob', displayName: 'Bob' }, author: { participantId: 'alice', kind: 'human', displayName: 'Alice' }, content: 'private body', mentions: [], createdAt: new Date() }) })
  expect(screen.getByTestId('messages')).toBeEmptyDOMElement()
  await act(async () => { editor.current?.privateChat?.('bob') })
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(2))
  expect(mocks.list.mock.calls[1][1]).toMatchObject({ scope: 'private', peerParticipantId: 'bob' })
  expect(screen.getByLabelText('draft')).toHaveValue('')
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'private draft' } })
  fireEvent.click(screen.getByRole('tab', { name: '公开讨论' }))
  await waitFor(() => expect(screen.getByLabelText('draft')).toHaveValue('public draft'))
  await act(async () => { editor.current?.privateChat?.('bob') })
  await waitFor(() => expect(screen.getByLabelText('draft')).toHaveValue('private draft'))
})

it('keeps an unknown submission locked until saved status is confirmed without resending', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  mocks.send.mockRejectedValue({ status: 0 })
  mocks.lookup.mockRejectedValueOnce({ status: 0 })
  render(<RealtimeChatRegion meetingId={id} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'original payload' } })
  fireEvent.click(screen.getByText('Send'))
  await waitFor(() => expect(mocks.lookup).toHaveBeenCalledTimes(1))
  expect(screen.getByTestId('send-state')).toHaveTextContent('pending_confirmation')
  expect(screen.getByLabelText('draft')).toBeDisabled()
  expect(screen.getByLabelText('draft')).toHaveValue('original payload')
  const submissionId = mocks.send.mock.calls[0][2]
  expect(mocks.lookup.mock.calls[0][1]).toBe(submissionId)
  mocks.lookup.mockResolvedValueOnce({ meetingId: id, messageId: 'saved', sequence: 1, author: { participantId: 'me', kind: 'human', displayName: 'Me' }, content: 'original payload', mentions: [], createdAt: new Date(), visibility: 'public' })
  fireEvent.click(screen.getByRole('button', { name: '发送结果待确认' }))
  fireEvent.click(await screen.findByRole('button', { name: '核对发送结果' }))
  await waitFor(() => expect(screen.getByTestId('send-state')).toHaveTextContent('idle'))
  expect(mocks.send).toHaveBeenCalledOnce()
  expect(screen.getByLabelText('draft')).toHaveValue('')
  expect(screen.getByTestId('messages')).toHaveTextContent('original payload')
  expect(screen.queryByLabelText('待发送消息')).toBeNull()
})

it('restores a scoped draft after the workspace unmounts the region to load another meeting', async () => {
  const cache: ChatSessionCache = { states: new Map(), selection: {} }
  const id = '11111111-1111-4111-8111-111111111111'
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  const first = render(<RealtimeChatRegion meetingId={id} sessionCache={cache} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'keep across route loading' } })
  first.unmount()
  render(<RealtimeChatRegion meetingId={id} sessionCache={cache} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(2))
  expect(screen.getByLabelText('draft')).toHaveValue('keep across route loading')
})

it('keeps one failed message next to its status and retries the original submission after checking', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  mocks.send.mockRejectedValueOnce({ status: 429, code: 'message_send_rate_limited', retryAfterSeconds: 1 })
  mocks.lookup.mockResolvedValueOnce(undefined)
  render(<RealtimeChatRegion meetingId={id} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'retry original' } })
  fireEvent.click(screen.getByText('Send'))
  fireEvent.click(await screen.findByRole('button', { name: '发送失败，查看原因或重试' }))
  expect(screen.getByLabelText('待发送消息')).toHaveTextContent('retry original')
  const submissionId = mocks.send.mock.calls[0][2]
  mocks.send.mockResolvedValueOnce({ message: { meetingId: id, messageId: 'saved', sequence: 1, author: { kind: 'human', displayName: 'Me' }, content: 'retry original', mentions: [], createdAt: new Date() } })
  fireEvent.click(await screen.findByRole('button', { name: '重试发送' }))
  await waitFor(() => expect(screen.queryByLabelText('待发送消息')).toBeNull())
  expect(mocks.send).toHaveBeenCalledTimes(2)
  expect(mocks.send.mock.calls[1][2]).toBe(submissionId)
  expect(mocks.lookup).toHaveBeenCalledWith(id, submissionId, expect.any(AbortSignal))
  expect(screen.getByTestId('messages')).toHaveTextContent('retry original')
})
it('renders an in-flight placeholder, clears it on success and keeps the confirmed message once', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  let complete!: (value: unknown) => void
  mocks.send.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
  render(<RealtimeChatRegion meetingId={id} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(1))
  fireEvent.change(screen.getByLabelText('draft'), { target: { value: 'slow send' } })
  fireEvent.click(screen.getByText('Send'))
  expect(await screen.findByRole('status', { name: '发送中' })).toBeVisible()
  const message = { meetingId: id, messageId: 'saved', sequence: 1, author: { kind: 'human' as const, displayName: 'Me' }, content: 'slow send', mentions: [], createdAt: new Date() }
  await act(async () => { mocks.callbacks[0].onMessage(message); complete({ message }) })
  await waitFor(() => expect(screen.queryByLabelText('待发送消息')).toBeNull())
  expect(screen.getByTestId('messages')).toHaveTextContent(/^slow send$/)
  expect(screen.queryByText('服务器已保存')).toBeNull()
})

function localAttachment(id: string) {
  return { attachmentId: id, uploadRequestId: id, name: `${id}.txt`, sizeBytes: 1, mediaType: 'application/octet-stream', state: 'local', localPreviewFile: new File(['x'], `${id}.txt`) }
}
it('uploads only after send, never submits partial attachments, and retries only failed uploads with stable ids', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  const state = { ...createInitialChatState(id), draft: 'local draft', attachments: [localAttachment('first'), localAttachment('second')] }
  const cache: ChatSessionCache = { states: new Map([[`${id}:public`, state]]), selection: {} }
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  mocks.upload.mockReset().mockResolvedValueOnce({ attachmentId: 'server-first' }).mockRejectedValueOnce(new Error('network')).mockResolvedValueOnce({ attachmentId: 'server-second' })
  mocks.send.mockResolvedValue({ message: { meetingId: id, messageId: 'saved-local', sequence: 1, author: { kind: 'human', displayName: 'Me' }, content: 'local draft', mentions: [], createdAt: new Date() } })
  render(<RealtimeChatRegion meetingId={id} sessionCache={cache} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledOnce())
  expect(mocks.upload).not.toHaveBeenCalled()
  fireEvent.click(screen.getByText('Send')); fireEvent.click(screen.getByText('Send'))
  await waitFor(() => expect(screen.getByTestId('send-state')).toHaveTextContent('failed'))
  expect(mocks.upload).toHaveBeenCalledTimes(2)
  expect(mocks.send).not.toHaveBeenCalled()
  expect(cache.states.get(`${id}:public`)?.attachments[0].uploadedAttachmentId).toBe('server-first')
  const submissionId = cache.states.get(`${id}:public`)?.failedSubmission?.clientSubmissionId
  fireEvent.click(screen.getByRole('button', { name: '发送失败，查看原因或重试' }))
  fireEvent.click(await screen.findByRole('button', { name: /重试发送/ }))
  await waitFor(() => expect(screen.getByTestId('send-state')).toHaveTextContent('idle'))
  expect(mocks.upload).toHaveBeenCalledTimes(3)
  expect(mocks.upload.mock.calls[1][2]).toBe('second')
  expect(mocks.upload.mock.calls[2][2]).toBe('second')
  expect(mocks.lookup).not.toHaveBeenCalled()
  expect(mocks.send.mock.calls[0][2]).toBe(submissionId)
  expect(mocks.send.mock.calls[0][5].attachmentIds).toEqual(['server-first', 'server-second'])
  expect(screen.getByLabelText('draft')).toHaveValue('')
  expect(cache.states.get(`${id}:public`)?.attachments).toHaveLength(0)
})
it('cancels an upload on scope unmount and retains the local payload for a deliberate retry', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  const state = { ...createInitialChatState(id), draft: 'keep local', attachments: [localAttachment('cancel')] }
  const cache: ChatSessionCache = { states: new Map([[`${id}:public`, state]]), selection: {} }
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  let finish!: (value: unknown) => void
  mocks.upload.mockReset().mockImplementation(() => new Promise(resolve => { finish = resolve }))
  const view = render(<RealtimeChatRegion meetingId={id} sessionCache={cache} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledOnce())
  fireEvent.click(screen.getByText('Send'))
  const signal = mocks.upload.mock.calls[0][3] as AbortSignal
  view.unmount()
  expect(signal.aborted).toBe(true)
  expect(cache.states.get(`${id}:public`)?.send).toBe('failed')
  expect(cache.states.get(`${id}:public`)?.failedSubmission?.uploadFailed).toBe(true)
  await act(async () => finish({ attachmentId: 'late-server-id' }))
  expect(mocks.send).not.toHaveBeenCalled()
  render(<RealtimeChatRegion meetingId={id} sessionCache={cache} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledTimes(2))
  expect(screen.getByLabelText('draft')).toHaveValue('keep local')
  expect(mocks.upload).toHaveBeenCalledOnce()
  expect(mocks.lookup).not.toHaveBeenCalled()
})
it('keeps a submitted attachment message pending on unmount and confirms it without reuploading or resending', async () => {
  const id = '11111111-1111-4111-8111-111111111111'
  const state = { ...createInitialChatState(id), draft: 'pending file', attachments: [localAttachment('pending-file')] }
  const cache: ChatSessionCache = { states: new Map([[`${id}:public`, state]]), selection: {} }
  mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
  mocks.upload.mockReset().mockResolvedValue({ attachmentId: 'uploaded-file' })
  let finish!: (value: unknown) => void
  mocks.send.mockReset().mockImplementation(() => new Promise(resolve => { finish = resolve }))
  const view = render(<RealtimeChatRegion meetingId={id} sessionCache={cache} participants={[]} />)
  await waitFor(() => expect(mocks.list).toHaveBeenCalledOnce())
  fireEvent.click(screen.getByText('Send'))
  await waitFor(() => expect(mocks.send).toHaveBeenCalledOnce())
  expect(mocks.send.mock.calls[0][5].attachmentIds).toEqual(['uploaded-file'])
  view.unmount()
  expect(cache.states.get(`${id}:public`)?.send).toBe('pending_confirmation')
  const saved = { meetingId: id, messageId: 'saved-pending', sequence: 1, author: { kind: 'human', displayName: 'Me' }, content: 'pending file', mentions: [], createdAt: new Date() }
  mocks.lookup.mockReset().mockResolvedValue(saved)
  await act(async () => finish({ message: saved }))
  render(<RealtimeChatRegion meetingId={id} sessionCache={cache} participants={[]} />)
  await waitFor(() => expect(screen.getByTestId('send-state')).toHaveTextContent('idle'))
  expect(mocks.upload).toHaveBeenCalledOnce()
  expect(mocks.send).toHaveBeenCalledOnce()
  expect(screen.getByLabelText('draft')).toHaveValue('')
})
it('hides only the visible current-scope reminder while account counts are still pending', async () => {
  const room = '11111111-1111-4111-8111-111111111111'
  const store = new AccountUnreadStore('reader', () => {})
  const bucket = { count: 1, last: 2, through: 2, read: 0 }
  const saved = { participantId: 'me', through: 2, scopes: { public: { ...bucket }, bob: { ...bucket } } }
  store.meetings.set(room, saved)
  const read = vi.spyOn(store, 'readThrough').mockImplementation(() => undefined)
  const context = vi.spyOn(unreadContext, 'useAccountUnread').mockReturnValue({ store, revision: 1 })
  const focus = vi.spyOn(document, 'hasFocus').mockReturnValue(true)
  const bounds = vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    return new DOMRect(0, this.dataset.messageId === 'offscreen' ? 300 : 0, 100, 100)
  })
  const base = { meetingId: room, visibility: 'public' as const, author: { participantId: 'alice', displayName: 'Alice', kind: 'human' as const }, mentions: [], createdAt: new Date() }
  const editor = createRef<MessageComposerHandle>()
  try {
    mocks.list.mockResolvedValue({ items: [], hasMoreBefore: false, hasMoreAfter: false })
    const view = () => <RealtimeChatRegion meetingId={room} composerRef={editor} participants={[]} />
    const page = render(view())
    await waitFor(() => expect(mocks.list).toHaveBeenCalled())
    const notify = async (messageId: string, sequence: number) => {
      await act(async () => { mocks.callbacks.at(-1)!.onMessage({ ...base, messageId, sequence, content: messageId }) })
    }
    expect(screen.getByText('公开讨论有新消息')).toBeInTheDocument()
    await notify('visible', 2)
    expect(read).toHaveBeenCalledWith(room, 'public', 2)
    expect(screen.queryByText('公开讨论有新消息')).toBeNull()
    expect(screen.getByText('参与者发来新私聊')).toBeInTheDocument()
    saved.scopes.public.last = 3
    page.rerender(view())
    await notify('offscreen', 3)
    expect(screen.getByText('公开讨论有新消息')).toBeInTheDocument()
    expect(read).not.toHaveBeenCalledWith(room, 'public', 3)
    await act(async () => {
      mocks.callbacks.at(-1)!.onMessage({ ...base, messageId: 'own', sequence: 4, content: 'own', author: { ...base.author, participantId: 'me' } })
    })
    expect(screen.getByText('公开讨论有新消息')).toBeInTheDocument()
    focus.mockReturnValue(false)
    saved.scopes.public.last = 5
    page.rerender(view())
    await notify('unfocused', 5)
    expect(read).not.toHaveBeenCalledWith(room, 'public', 5)
    await act(async () => { editor.current?.privateChat?.('bob') })
    expect(screen.getByText('参与者发来新私聊')).toBeInTheDocument()
    expect(screen.getByText('公开讨论有新消息')).toBeInTheDocument()
  } finally {
    bounds.mockRestore(); focus.mockRestore(); context.mockRestore(); store.dispose()
  }
})
