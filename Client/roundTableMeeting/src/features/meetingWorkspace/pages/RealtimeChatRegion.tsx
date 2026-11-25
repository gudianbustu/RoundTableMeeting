import { useAccountUnread } from '../chat/AccountUnreadProvider'
import type { CSSProperties, ReactNode, Ref } from 'react'
import { useMessageNotifications } from '../hooks/useMessageNotifications'
import { useCallback, useEffect, useImperativeHandle, useLayoutEffect, useMemo, useReducer, useRef, useState } from 'react'
import { CloseOutlined } from '@ant-design/icons'
import { Alert, Badge, Button, Dropdown, Tabs, Tag } from 'antd'
import { createUuid } from '@/shared/utils/createUuid'
import { listMessages, type MessagePageRequest } from '../api/listMessages'
import { sendMessage } from '../api/sendMessage'
import { getMessage, getSubmission, uploadAttachment } from '../api/messageResources'
import { MeetingChatConnection, type Subscription } from '../api/meetingChatConnection'
import { chatReducer, createInitialChatState } from '../chat/messageReducer'
import { selectMessages } from '../chat/messageSelectors'
import { ChatConnectionStatus } from '../components/ChatConnectionStatus'
import { MessageComposer, type MessageComposerHandle } from '../components/MessageComposer'
import { PendingMessage } from '../components/PendingMessage'
import { DraftAttachments } from '../components/DraftAttachments'
import { MessageTimeline } from '../components/MessageTimeline'
import { AttachmentUpload, MeetingAttachmentList, type AttachmentUploadHandle } from '../components/AttachmentControls'
import type { AgentActivity, ChatMessage, ChatScrollPosition, ChatState, FailedSubmission } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'
import { getMentionCandidates } from '../chat/mentionCandidates'

export type ChatSessionCache = { states: Map<string, ChatState>; selection: Record<string, string | undefined>; tabs?: Record<string, string[]> }
type ChatProps = { onPeerChanged?: (peerId?: string) => void; sessionCache?: ChatSessionCache; composerRef?: Ref<MessageComposerHandle>; composerTools?: ReactNode; meetingId: string; participants: Participant[]; readOnly?: boolean; onMeetingUnavailable?: () => void; onMeetingDetailsChanged?: () => void; onMembershipChanged?: () => void; onPresenceChanged?: (presence: Record<string, 'online' | 'offline'>) => void; onAgentActivitiesChanged?: (activities: Record<string, AgentActivity>) => void }
export function RealtimeChatRegion(props: ChatProps) {
  const [selection, setSelection] = useState<Record<string, string | undefined>>(props.sessionCache?.selection ?? {})
  const [tabs, setTabs] = useState<Record<string, string[]>>(props.sessionCache?.tabs ?? {})
  const cache = useRef(props.sessionCache?.states ?? new Map<string, ChatState>())
  const editor = useRef<MessageComposerHandle>(null)
  const peerId = selection[props.meetingId]
  useLayoutEffect(() => { props.onPeerChanged?.(peerId) }, [props.onPeerChanged, peerId])
  const peer = props.participants.find(p => p.participantId === peerId)
  const accountUnread = useAccountUnread()
  const shared = accountUnread?.store
  const saved = shared?.meetings.get(props.meetingId)
  const legacyNotices = useMessageNotifications(props.meetingId, props.participants.find(p => p.isCurrentUser)?.participantId, peerId ?? 'public', !props.readOnly && accountUnread === undefined)
  const syncUnread = useCallback(async () => shared?.refresh(props.meetingId), [props.meetingId, shared])
  const notices = shared ? {
    publicUnread: saved?.scopes.public?.count ?? 0,
    privateUnread: (id: string) => saved?.scopes[id]?.count ?? 0,
    pending: Object.fromEntries(Object.entries(saved?.scopes ?? {}).filter(([, bucket]) => bucket.count > 0)),
    receive: syncUnread, synchronize: syncUnread, retry: syncUnread, remove: syncUnread, failed: false,
  } : legacyNotices
  const key = `${props.meetingId}:${peerId ?? 'public'}`
  const { meetingId, sessionCache } = props
  const updateTabs = useCallback((update: (current: string[]) => string[]) => {
    setTabs(previous => {
      const next = { ...previous, [meetingId]: update(previous[meetingId] ?? []) }
      if (sessionCache) sessionCache.tabs = next
      return next
    })
  }, [meetingId, sessionCache])
  const selectPeer = useCallback((id?: string) => {
    if (id) updateTabs(current => current.includes(id) ? current : [...current, id])
    setSelection(previous => {
      const next = { ...previous, [meetingId]: id }
      if (sessionCache) sessionCache.selection = next
      return next
    })
  }, [meetingId, sessionCache, updateTabs])
  const closePeer = (id: string) => {
    if (peerId === id) selectPeer()
    updateTabs(current => current.filter(item => item !== id))
  }
  useImperativeHandle(props.composerRef, () => ({ mention: name => editor.current?.mention(name), privateChat: id => selectPeer(id) }), [selectPeer])
  const retain = useCallback((state: ChatState) => { cache.current.set(key, state) }, [key])
  const unavailable = useCallback(() => {
    notices.remove()
    for (const savedKey of cache.current.keys()) if (savedKey.startsWith(props.meetingId + ':')) cache.current.delete(savedKey)
    if (props.sessionCache) delete props.sessionCache.selection[props.meetingId]
    if (props.sessionCache?.tabs) delete props.sessionCache.tabs[props.meetingId]
    props.onMeetingUnavailable?.()
  }, [props.meetingId, props.onMeetingUnavailable, props.sessionCache, notices.remove])
  const privateBadge = (id: string) => {
    const count = notices.privateUnread(id)
    return count > 0 && <Badge count={count} overflowCount={999} title={`私聊未读 ${count >= 1000 ? '999+' : count} 条`} />
  }
  return <div className="scoped-chat">
    {Boolean(tabs[meetingId]?.length) && <Tabs className="conversation-tabs" aria-label="会话" type="editable-card" hideAdd activeKey={peerId ?? 'public'}
      onChange={id => selectPeer(id === 'public' ? undefined : id)}
      onEdit={(id, action) => { if (action === 'remove' && typeof id === 'string') closePeer(id) }}
      items={[
        { key: 'public', label: <span>公开讨论 {notices.publicUnread > 0 && <Badge count={notices.publicUnread} overflowCount={999} title={`公屏未读 ${notices.publicUnread >= 1000 ? "999+" : notices.publicUnread} 条`} />}</span>, closable: false },
        ...(tabs[meetingId] ?? []).map(id => ({ key: id, label: <span className="private-tab-name">{props.participants.find(p => p.participantId === id)?.displayName ?? '参与者'} {privateBadge(id)}</span> })),
      ]} />}
    <ScopedChat key={`${key}:${props.readOnly ? 'history' : 'active'}`}  {...props} notificationControl={<div aria-live="polite" className="chat-message-notifications">
      {Object.keys(notices.pending).map(id => <Button key={id} size="small" onClick={() => selectPeer(id === 'public' ? undefined : id)}>{id === 'public' ? '公开讨论有新消息' : `${props.participants.find(p => p.participantId === id)?.displayName ?? '参与者'}发来新私聊`}{id !== 'public' && privateBadge(id)}</Button>)}
      {notices.failed && <Button size="small" onClick={notices.retry}>新消息提醒同步失败，重试</Button>}
    </div>} onNotificationMessage={notices.receive} onNotificationSnapshot={notices.synchronize} composerRef={editor} peerId={peerId} scopeControl={peerId && <span>私聊 · 仅你与{peer?.displayName ?? '对方'}可见</span>} initialState={cache.current.get(key)} onStateChanged={retain} onPrivateChat={selectPeer} onMeetingUnavailable={unavailable} />
  </div>
}

function ScopedChat({ notificationControl, onNotificationMessage, onNotificationSnapshot, scopeControl, composerRef, composerTools, meetingId, participants, peerId, readOnly = false, initialState, onStateChanged, onPrivateChat, onMeetingUnavailable, onMeetingDetailsChanged, onMembershipChanged, onPresenceChanged, onAgentActivitiesChanged }: ChatProps & { notificationControl: ReactNode; onNotificationMessage: (message: ChatMessage) => void; onNotificationSnapshot: (through: number) => Promise<void>; scopeControl: ReactNode; peerId?: string; initialState?: ChatState; onStateChanged: (state: ChatState) => void; onPrivateChat: (id?: string) => void }) {
  const [readingWidth, setReadingWidth] = useState<number>()
  const savedInitial = useRef(initialState)
  const generation = useRef(initialState?.generation ?? 1)
  const controller = useRef<AbortController | undefined>(undefined)
  const connection = useRef<MeetingChatConnection | undefined>(undefined)
  const lastApplied = useRef(initialState?.lastAppliedSequence ?? 0)
  const gapRepairing = useRef(false)
  const pendingCheck = useRef<string | undefined>(undefined)
  const activeSend = useRef<{ controller: AbortController; submission: FailedSubmission; phase: 'upload' | 'message'; generation: number } | undefined>(undefined)
  const checkingSend = useRef(false)
  const uploader = useRef<AttachmentUploadHandle>(null)
  const [focusRevision, setFocusRevision] = useState(0)
  const [focusMessageId, setFocusMessageId] = useState<string>()
  const [state, dispatch] = useReducer(chatReducer, initialState ?? createInitialChatState(meetingId, generation.current))
  const requestScope = useMemo(() => ({ scope: peerId ? 'private' as const : 'public' as const, peerParticipantId: peerId }), [peerId])
  const matches = useCallback((message: ChatMessage) => message.meetingId === meetingId && (peerId
    ? message.visibility === 'private' && (message.author.participantId === peerId || message.recipient?.participantId === peerId)
    : (message.visibility ?? 'public') === 'public'), [meetingId, peerId])
  const scope = useCallback(() => ({ meetingId, generation: generation.current }), [meetingId])
  const retainScroll = useCallback((position: ChatScrollPosition) => dispatch({ ...scope(), type: 'scrollPositionChanged', position }), [scope])
  const reportUnavailable = useCallback((expected: number) => {
    if (expected !== generation.current) return
    generation.current += 1; controller.current?.abort(); void connection.current?.stop(); connection.current = undefined
    dispatch({ type: 'reset', meetingId, generation: generation.current }); onMeetingUnavailable?.()
  }, [meetingId, onMeetingUnavailable])
  const handleError = useCallback((error: unknown, expected: number) => {
    const code = (error as { code?: string }).code
    if (['meeting_not_found', 'meeting_deleting', 'meeting_deleted'].includes(code ?? '')) reportUnavailable(expected)
    if (code === 'membership_left') onMembershipChanged?.()
  }, [onMembershipChanged, reportUnavailable])
  const load = useCallback(async (request: MessagePageRequest, initial = false) => {
    const requested = scope(); controller.current?.abort(); const current = new AbortController(); controller.current = current
    dispatch({ ...requested, type: 'historyStateChanged', state: initial ? 'loading' : 'appending' })
    try {
      const page = await listMessages(meetingId, { ...request, ...requestScope }, current.signal)
      if (current.signal.aborted) return false
      dispatch({ ...requested, type: 'historyPageMerged', page, initial }); dispatch({ ...requested, type: 'historyStateChanged', state: 'caughtUp' }); return true
    } catch (error) {
      if (!current.signal.aborted) { dispatch({ ...requested, type: 'historyStateChanged', state: 'error' }); handleError(error, requested.generation) }
      return false
    }
  }, [meetingId, requestScope, scope, handleError])
  const catchUp = useCallback(async (through: number, expected: number) => {
    controller.current?.abort(); const current = new AbortController(); controller.current = current
    let after = lastApplied.current
    while (!current.signal.aborted && expected === generation.current && after < through) {
      const page = await listMessages(meetingId, { ...requestScope, afterSequence: after, throughSequence: through, limit: 100 }, current.signal)
      dispatch({ meetingId, generation: expected, type: 'historyPageMerged', page, caughtUp: !page.hasMoreAfter })
      if (!page.hasMoreAfter || !page.lastSequence || page.lastSequence <= after) break
      after = page.lastSequence
    }
    dispatch({ meetingId, generation: expected, type: 'historyStateChanged', state: 'caughtUp' })
  }, [meetingId, requestScope])
  const synchronize = useCallback(async (snapshot: Subscription, expected: number, initial = false) => {
    if (expected !== generation.current) return
    dispatch({ meetingId, generation: expected, type: 'presenceSnapshot', participantIds: snapshot.onlineParticipantIds, highWatermarkSequence: snapshot.highWatermarkSequence })
    dispatch({ meetingId, generation: expected, type: 'agentActivitySnapshot', activities: snapshot.agentActivities })
    void onNotificationSnapshot(snapshot.highWatermarkSequence)
    if (initial) await load({ throughSequence: snapshot.highWatermarkSequence, limit: 100 }, true)
    else await catchUp(snapshot.highWatermarkSequence, expected)
  }, [meetingId, load, catchUp, onNotificationSnapshot])
  const connect = useCallback(async () => {
    if (readOnly) { await load({ limit: 100 }, true); return }
    const expected = ++generation.current
    dispatch({ type: 'connectionStarted', meetingId, generation: expected })
    await connection.current?.stop(); if (expected !== generation.current) return
    const scoped = () => ({ meetingId, generation: expected })
    const hub = new MeetingChatConnection({
      onState: value => dispatch({ ...scoped(), type: 'connectionStateChanged', state: value }),
      onMessage: message => { if (expected !== generation.current) return; onNotificationMessage(message); if (message.author.kind === 'system') onMeetingDetailsChanged?.(); if (matches(message)) dispatch({ ...scoped(), type: 'messageEventReceived', message }) },
      onPresence: (participantId, value) => dispatch({ ...scoped(), type: 'presenceChanged', participantId, state: value }),
      onAgentActivity: activity => dispatch({ ...scoped(), type: 'agentActivityChanged', activity }),
      onInformationChanged: () => { if (expected === generation.current) onMeetingDetailsChanged?.() },
      onParticipantRenamed: () => { if (expected === generation.current) onMeetingDetailsChanged?.() },
      onMembershipLeft: () => { if (expected === generation.current) onMembershipChanged?.() },
      onDeleted: () => reportUnavailable(expected),
      onResubscribed: snapshot => { onMeetingDetailsChanged?.(); void synchronize(snapshot, expected).catch(error => handleError(error, expected)) },
    })
    connection.current = hub
    try {
      const snapshot = await hub.start(meetingId)
      if (expected !== generation.current) { await hub.stop(); return }
      onMeetingDetailsChanged?.()
      await synchronize(snapshot, expected, true)
    } catch (error) {
      await hub.stop().catch(() => undefined)
      if (expected !== generation.current) return
      if ((error as { message?: string }).message?.includes('meeting_not_found')) { dispatch({ ...scoped(), type: 'connectionStateChanged', state: 'rejected' }); onMembershipChanged?.() }
      else dispatch({ ...scoped(), type: 'connectionStateChanged', state: 'disconnected' })
    }
  }, [readOnly, load, meetingId, matches, synchronize, reportUnavailable, onMeetingDetailsChanged, onMembershipChanged, handleError, onNotificationMessage])
  useEffect(() => {
    generation.current += 1; dispatch({ type: 'reset', meetingId, generation: generation.current, initialState: savedInitial.current }); void connect()
    return () => { generation.current += 1; controller.current?.abort(); void connection.current?.stop(); connection.current = undefined }
  }, [connect, meetingId])
  const latestState = useRef(state)
  useLayoutEffect(() => { latestState.current = state; onStateChanged(state) }, [onStateChanged, state])
  useEffect(() => () => {
    const active = activeSend.current
    if (!active) return
    active.controller.abort()
    const uploadFailed = active.phase === 'upload'
    onStateChanged({ ...latestState.current, attachments: active.submission.localAttachments ?? latestState.current.attachments,
      send: uploadFailed ? 'failed' : 'pending_confirmation',
      failedSubmission: { ...active.submission, uploadFailed, uploadProgress: undefined, pendingConfirmation: !uploadFailed, errorCode: uploadFailed ? 'attachment_upload_interrupted' : undefined } })
    activeSend.current = undefined
  }, [onStateChanged])
  useEffect(() => {
    const active = activeSend.current
    if (!active || active.generation === state.generation) return
    active.controller.abort(); activeSend.current = undefined
    dispatch({ meetingId, generation: state.generation, type: 'sendFailed', submission: { ...active.submission, uploadFailed: active.phase === 'upload', uploadProgress: undefined, pendingConfirmation: active.phase === 'message', errorCode: 'attachment_upload_interrupted' } })
  }, [state.generation, meetingId])
  const participantNames = useMemo(() => new Map(participants.map(member => [member.participantId, member.displayName])), [participants])
  const messages = useMemo(() => {
    return selectMessages(state).map(message => {
      const name = message.author.participantId && participantNames.get(message.author.participantId)
      return name && name !== message.author.displayName ? { ...message, author: { ...message.author, displayName: name } } : message
    })
  }, [state.messagesById, state.orderedMessageIds, participantNames])
  const unreadStore = useAccountUnread()?.store
  const region = useRef<HTMLElement>(null)
  const initialized = unreadStore?.meetings.has(meetingId)
  useEffect(() => {
    const log = region.current?.querySelector<HTMLElement>('[role="log"]')
    if (!log || !unreadStore || !initialized || readOnly || !window.IntersectionObserver) return
    const sequences = new Map(messages.map(message => [message.messageId, message.sequence]))
    const visible = new Set<number>()
    const markRead = () => {
      if (visible.size && document.visibilityState === 'visible' && document.hasFocus())
        void unreadStore.readThrough(meetingId, peerId ?? 'public', Math.max(...visible))
    }
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        const sequence = sequences.get((entry.target as HTMLElement).dataset.messageId ?? '')
        if (sequence !== undefined) {
          if (entry.isIntersecting && entry.intersectionRatio > 0) visible.add(sequence)
          else visible.delete(sequence)
        }
      }
      markRead()
    }, { root: log })
    log.querySelectorAll('[data-message-id]').forEach(row => observer.observe(row))
    document.addEventListener('visibilitychange', markRead)
    window.addEventListener('focus', markRead)
    return () => { observer.disconnect(); document.removeEventListener('visibilitychange', markRead); window.removeEventListener('focus', markRead) }
  }, [unreadStore, initialized, meetingId, peerId, messages, readOnly])
  useEffect(() => { lastApplied.current = state.lastAppliedSequence }, [state.lastAppliedSequence])
  useEffect(() => {
    if (readOnly || state.highWatermarkSequence <= state.lastAppliedSequence || state.history !== 'caughtUp' || gapRepairing.current) return
    gapRepairing.current = true; const expected = generation.current
    void catchUp(state.highWatermarkSequence, expected).catch(error => handleError(error, expected)).finally(() => { gapRepairing.current = false })
  }, [readOnly, state.highWatermarkSequence, state.lastAppliedSequence, state.history, catchUp, handleError])
  useEffect(() => { onPresenceChanged?.(readOnly ? {} : state.presence) }, [onPresenceChanged, readOnly, state.presence])
  useEffect(() => { onAgentActivitiesChanged?.(state.agentActivities) }, [onAgentActivitiesChanged, state.agentActivities])
  const confirm = useCallback(async (submission: FailedSubmission, expected: number) => {
    try {
      const found = await getSubmission(meetingId, submission.clientSubmissionId, AbortSignal.timeout(15000))
      if (found) dispatch({ meetingId, generation: expected, type: 'sendSucceeded', message: found })
      else dispatch({ meetingId, generation: expected, type: 'sendFailed', submission: { ...submission, pendingConfirmation: false, errorCode: 'submission_not_saved' } })
      return found ? 'saved' : 'not_found'
    } catch (error) { handleError(error, expected); return 'unknown' }
  }, [meetingId, handleError])
  useEffect(() => {
    if (state.failedSubmission?.localAttachments?.some(file => !file.uploadedAttachmentId)) return
    if (state.send !== 'pending_confirmation' || !state.failedSubmission || pendingCheck.current === state.failedSubmission.clientSubmissionId) return
    pendingCheck.current = state.failedSubmission.clientSubmissionId
    void confirm(state.failedSubmission, generation.current)
  }, [state.send, state.failedSubmission, confirm])
  const transmit = async (submission: FailedSubmission, expected: number) => {
    if (activeSend.current || checkingSend.current || expected !== generation.current) return
    const operation: NonNullable<typeof activeSend.current> = { controller: new AbortController(), submission: { ...submission, uploadFailed: false, pendingConfirmation: false, uploadProgress: undefined }, phase: 'upload' as 'upload' | 'message', generation: expected }
    activeSend.current = operation
    let files = [...(submission.localAttachments ?? [])]
    const interrupted = () => operation.controller.signal.aborted || expected !== generation.current
    const started = () => dispatch({ meetingId, generation: expected, type: 'sendStarted', submission: operation.submission, attachments: files })
    started()
    try {
      for (let index = 0; index < files.length; index++) {
        if (interrupted()) return
        const file = files[index]
        if (file.uploadedAttachmentId) continue
        if (!file.localPreviewFile) throw { code: 'local_attachment_missing' }
        operation.submission = { ...operation.submission, uploadProgress: `正在上传附件 ${index + 1}/${files.length}：${file.name}` }
        started()
        const uploaded = await uploadAttachment(meetingId, file.localPreviewFile, file.uploadRequestId ?? file.attachmentId, operation.controller.signal)
        if (interrupted()) return
        files = files.map((item, at) => at === index ? { ...item, uploadedAttachmentId: uploaded.attachmentId } : item)
        operation.submission = { ...operation.submission, localAttachments: files }
        started()
      }
      if (interrupted()) return
      operation.submission = { ...operation.submission, uploadProgress: undefined, attachmentIds: files.length ? files.map(file => file.uploadedAttachmentId!) : submission.attachmentIds ?? [] }
      operation.phase = 'message'
      started()
      const result = await sendMessage(meetingId, operation.submission.content, operation.submission.clientSubmissionId, operation.submission.candidates, AbortSignal.any([operation.controller.signal, AbortSignal.timeout(30000)]), operation.submission)
      if (!interrupted()) dispatch({ meetingId, generation: expected, type: 'sendSucceeded', message: result.message })
    } catch (error) {
      if (interrupted()) return
      const apiError = error as { code?: string; status?: number; retryAfterSeconds?: number }
      handleError(error, expected)
      const uploadFailed = operation.phase === 'upload'
      const unknown = !uploadFailed && (!apiError.status || apiError.status >= 500 || apiError.status === 408)
      dispatch({ meetingId, generation: expected, type: 'sendFailed', submission: { ...operation.submission, uploadFailed, uploadProgress: undefined, pendingConfirmation: unknown, errorCode: uploadFailed ? apiError.code === 'local_attachment_missing' ? apiError.code : 'attachment_upload_failed' : apiError.code, retryAfterSeconds: apiError.retryAfterSeconds } })
    } finally { if (activeSend.current === operation) activeSend.current = undefined }
  }
  const submit = () => {
    if (readOnly || activeSend.current || checkingSend.current || state.send === 'sending' || state.send === 'pending_confirmation' || !state.draft.trim() && !state.attachments.length) return
    const candidates = getMentionCandidates(state.draft, peerId ? participants.filter(p => p.isCurrentUser || p.participantId === peerId) : participants, !peerId)
    const submission: FailedSubmission = { clientSubmissionId: createUuid(), content: state.draft, candidates, visibility: peerId ? 'private' : 'public', recipientParticipantId: peerId, localAttachments: [...state.attachments], replyToMessageId: state.replyTo?.messageId }
    void transmit(submission, generation.current)
  }
  const retry = async () => {
    const submission = state.failedSubmission; if (!submission || activeSend.current || checkingSend.current) return
    const expected = generation.current
    if (submission.uploadFailed) { if (!readOnly) await transmit(submission, expected); return }
    checkingSend.current = true
    let result: string
    try { result = await confirm(submission, expected) } finally { checkingSend.current = false }
    if (result === 'not_found' && state.send !== 'pending_confirmation' && !readOnly && expected === generation.current) await transmit(submission, expected)
  }
  const locate = async (id: string) => {
    const expected = generation.current
    const target = await getMessage(meetingId, id)
    if (!matches(target)) throw new Error('Message is outside this conversation')
    const page = await listMessages(meetingId, { ...requestScope, beforeSequence: target.sequence + 1, limit: 50 })
    if (expected !== generation.current) return
    dispatch({ meetingId, generation: expected, type: 'historyPageMerged', page }); setFocusMessageId(id); setFocusRevision(value => value + 1)
  }
  const browseAttachments = <MeetingAttachmentList meetingId={meetingId} {...requestScope} onLocate={locate} />
  const locked = readOnly || state.send === 'sending' || state.send === 'pending_confirmation'
  return <section ref={region} className={peerId ? 'realtime-chat-region chat-private' : 'realtime-chat-region'}
    style={{ '--reading-width': readingWidth === undefined ? undefined : readingWidth + 'px' } as CSSProperties}>
    {readOnly ? state.history === 'error' && <Alert type="error" title="历史消息加载失败" action={<Button onClick={() => void connect()}>重试</Button>} /> : <ChatConnectionStatus connection={state.connection} history={state.history} error={state.history === 'error' ? '消息历史加载失败。' : undefined} onRetry={() => void connect()} />}
    {(readOnly || scopeControl) && <div className="composer-context-toolbar">{readOnly ? <Dropdown trigger={['click']} menu={{
      selectedKeys: [peerId ?? 'public'],
      items: [{ key: 'public', label: '公开讨论' }, ...participants.filter(p => !p.isCurrentUser).map(p =>
        ({ key: p.participantId, label: '私聊 · ' + p.displayName }))],
      onClick: ({ key }) => onPrivateChat(key === 'public' ? undefined : key),
    }}><Button type="text" size="small" aria-label="选择历史会话">{peerId ? '私聊 · ' + (participants.find(p => p.participantId === peerId)?.displayName ?? '参与者') : '历史记录'} · 仅可查看退出前有权访问的消息 ⌄</Button></Dropdown> : scopeControl}</div>}
    <MessageTimeline onWidthChange={setReadingWidth} onLocate={locate} pendingMessage={state.failedSubmission && state.send !== 'idle' ? <PendingMessage key={state.failedSubmission.clientSubmissionId} submission={state.failedSubmission} status={state.send} attachments={state.attachments} readOnly={readOnly} onRetry={retry} /> : undefined} messages={messages} hasEarlierMessages={state.hasEarlierMessages} loading={state.history === 'loading' || state.history === 'appending'} onLoadEarlier={() => { if (state.earliestLoadedSequence) void load({ beforeSequence: state.earliestLoadedSequence, limit: 100 }) }} newMessageCount={0} onNewMessageClick={() => {}} onReply={message => dispatch({ ...scope(), type: 'replyChanged', message })} onPrivateChat={onPrivateChat} currentParticipantId={participants.find(p => p.isCurrentUser)?.participantId} focusMessageId={focusMessageId} focusRevision={focusRevision} canReply={!locked} scrollPosition={state.scrollPosition} onScrollPositionChanged={retainScroll} />
    {notificationControl}
    {readOnly ? <div className="workspace-history-end">已退出会议，记录截止于退出时。{browseAttachments}</div> : <MessageComposer publicChat={!peerId} placeholder={peerId ? '发给' + (participantNames.get(peerId) ?? '对方') + '…' : '发到公开讨论…'} onCancelReply={state.replyTo ? () => dispatch({ ...scope(), type: 'replyChanged', message: undefined }) : undefined} composerRef={composerRef} onFiles={files => uploader.current?.addFiles(files)} attachments={<> {state.replyTo && <div className="message-reply-context"><Tag>回复 {participantNames.get(state.replyTo.author.participantId ?? '') ?? state.replyTo.author.displayName}</Tag><span>{state.replyTo.content.slice(0, 120) || '附件消息'}</span><Button type="text" size="small" aria-label="取消回复" title="取消回复" icon={<CloseOutlined />} disabled={locked} onClick={() => dispatch({ ...scope(), type: 'replyChanged', message: undefined })} /></div>}<DraftAttachments attachments={state.attachments} disabled={locked} onChange={attachments => dispatch({ ...scope(), type: 'attachmentsChanged', attachments })} /></>} tools={<>{!readOnly && <AttachmentUpload uploadRef={uploader} attachments={state.attachments} disabled={locked} onChange={attachments => dispatch({ ...scope(), type: 'attachmentsChanged', attachments })} />}{browseAttachments}{composerTools}</>} value={state.draft} send={state.send} participants={peerId ? participants.filter(p => p.isCurrentUser || p.participantId === peerId) : participants} hasAttachments={state.attachments.length > 0} disabled={locked} onChange={draft => dispatch({ ...scope(), type: 'draftChanged', draft })} onSend={submit} />}
  </section>
}
