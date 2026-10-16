import type { AgentActivity, ChatAttachment, ChatMessage, ChatScrollPosition, ChatState, ConnectionState, FailedSubmission, HistoryState, MessagePage, PresenceState, SendState } from '../types/realtimeChat'

export function createInitialChatState(meetingId: string, generation = 1): ChatState {
  return { meetingId, generation, messagesById: {}, orderedMessageIds: [], liveBaselineSequence: 0, lastAppliedSequence: 0, highWatermarkSequence: 0, hasEarlierMessages: false, connection: 'idle', history: 'idle', presence: {}, agentActivities: {}, attachments: [], draft: '', send: 'idle', newMessageCount: 0 }
}

type Scoped = { meetingId: string; generation: number }
export type ChatAction =
  | (Scoped & { type: 'historyPageMerged'; page: MessagePage; initial?: boolean; caughtUp?: boolean })
  | (Scoped & { type: 'messageEventReceived'; message: ChatMessage })
  | (Scoped & { type: 'presenceChanged'; participantId: string; state: PresenceState })
  | (Scoped & { type: 'presenceSnapshot'; participantIds: string[]; highWatermarkSequence: number })
  | (Scoped & { type: 'agentActivitySnapshot'; activities: AgentActivity[] })
  | (Scoped & { type: 'agentActivityChanged'; activity: AgentActivity })
  | (Scoped & { type: 'connectionStateChanged'; state: ConnectionState })
  | (Scoped & { type: 'connectionStarted' })
  | (Scoped & { type: 'historyStateChanged'; state: HistoryState })
  | (Scoped & { type: 'sendStarted'; submission: FailedSubmission; attachments?: ChatAttachment[] })
  | (Scoped & { type: 'sendSucceeded'; message: ChatMessage })
  | (Scoped & { type: 'sendFailed'; submission: FailedSubmission })
  | (Scoped & { type: 'draftChanged'; draft: string })
  | (Scoped & { type: 'attachmentsChanged'; attachments: ChatAttachment[] })
  | (Scoped & { type: 'replyChanged'; message?: ChatMessage })
  | (Scoped & { type: 'scrollPositionChanged'; position: ChatScrollPosition })
  | (Scoped & { type: 'newMessageNoticeRead' })
  | (Scoped & { type: 'meetingDeleted' })
  | { type: 'reset'; meetingId: string; generation: number; initialState?: ChatState }

function merge(state: ChatState, messages: ChatMessage[], initial = false): ChatState {
  const next = { ...state, messagesById: { ...state.messagesById } }
  if (initial && messages.length > 0) next.liveBaselineSequence = Math.min(...messages.map(message => message.sequence)) - 1
  for (const message of messages) {
    if (message.meetingId !== state.meetingId || !Number.isSafeInteger(message.sequence) || message.sequence < 1) continue
    if (!next.messagesById[message.messageId]) next.messagesById[message.messageId] = message
  }
  next.orderedMessageIds = Object.keys(next.messagesById).sort((a, b) => next.messagesById[a].sequence - next.messagesById[b].sequence)
  const sequences = next.orderedMessageIds.map(id => next.messagesById[id].sequence)
  if (sequences.length) {
    const first = Math.min(...sequences); const last = Math.max(...sequences)
    next.earliestLoadedSequence = next.earliestLoadedSequence === undefined ? first : Math.min(next.earliestLoadedSequence, first)
    let cursor = next.liveBaselineSequence
    while (next.orderedMessageIds.some(id => next.messagesById[id].sequence === cursor + 1)) cursor++
    next.lastAppliedSequence = Math.max(next.lastAppliedSequence, cursor)
    if (last > next.highWatermarkSequence) next.highWatermarkSequence = last
  } else if (initial && state.orderedMessageIds.length === 0) { next.liveBaselineSequence = 0; next.lastAppliedSequence = 0 }
  return next
}

function retainActivity(current: AgentActivity | undefined, incoming: AgentActivity): boolean {
  if (!current) return false
  if (current.revision !== undefined) {
    if (incoming.revision === undefined) return true
    return current.revision > incoming.revision || current.revision === incoming.revision && current.state !== 'unknown'
  }
  if (incoming.revision !== undefined) return false
  return current.changedAt.getTime() > incoming.changedAt.getTime()
}

export function chatReducer(state: ChatState, action: ChatAction): ChatState {
  if (action.type === 'connectionStarted') {
    if (action.meetingId !== state.meetingId || action.generation <= state.generation) return state
    return { ...state, generation: action.generation, connection: 'connecting', send: state.send === 'sending' ? 'pending_confirmation' : state.send, failedSubmission: state.send === 'sending' && state.failedSubmission ? { ...state.failedSubmission, pendingConfirmation: true } : state.failedSubmission }
  }
  if (action.type !== 'reset' && (action.meetingId !== state.meetingId || action.generation !== state.generation)) return state
  switch (action.type) {
    case 'reset': return action.initialState ? { ...action.initialState, generation: action.generation, send: action.initialState.send === 'sending' ? 'pending_confirmation' : action.initialState.send } : createInitialChatState(action.meetingId, action.generation)
    case 'historyPageMerged': {
      const next = merge(state, action.page.items, action.initial)
      if ((action.initial || action.caughtUp) && action.page.visibleThroughSequence !== undefined) next.lastAppliedSequence = Math.max(next.lastAppliedSequence, action.page.visibleThroughSequence)
      return { ...next, hasEarlierMessages: action.caughtUp ? state.hasEarlierMessages : action.page.hasMoreBefore, history: action.page.hasMoreAfter ? 'appending' : next.history }
    }
    case 'messageEventReceived': return action.message.meetingId !== state.meetingId || state.messagesById[action.message.messageId] ? state : merge({ ...state, newMessageCount: state.newMessageCount + 1 }, [action.message])
    case 'presenceChanged': return { ...state, presence: { ...state.presence, [action.participantId]: action.state } }
    case 'presenceSnapshot': { const presence: Record<string, PresenceState> = {}; for (const id of action.participantIds) presence[id] = 'online'; return { ...state, presence, highWatermarkSequence: Math.max(state.highWatermarkSequence, action.highWatermarkSequence) } }
    case 'agentActivitySnapshot': { const agentActivities: Record<string, AgentActivity> = {}; for (const activity of action.activities) { if (activity.meetingId !== state.meetingId) continue; const current = state.agentActivities[activity.participantId]; agentActivities[activity.participantId] = retainActivity(current, activity) ? current : activity } return { ...state, agentActivities } }
    case 'agentActivityChanged': { const current = state.agentActivities[action.activity.participantId]; if (action.activity.meetingId !== state.meetingId || retainActivity(current, action.activity)) return state; return { ...state, agentActivities: { ...state.agentActivities, [action.activity.participantId]: action.activity } } }
    case 'connectionStateChanged': { if (action.state !== 'reconnecting' && action.state !== 'disconnected' && action.state !== 'rejected') return { ...state, connection: action.state }; const agentActivities: Record<string, AgentActivity> = {}; for (const activity of Object.values(state.agentActivities)) agentActivities[activity.participantId] = { ...activity, state: 'unknown' }; return { ...state, connection: action.state, agentActivities } }
    case 'historyStateChanged': return { ...state, history: action.state }
    case 'draftChanged': return state.send === 'pending_confirmation' || state.send === 'sending' ? state : { ...state, draft: action.draft, failedSubmission: undefined, send: 'idle', savedSubmission: false }
    case 'attachmentsChanged': return state.send === 'pending_confirmation' || state.send === 'sending' ? state : { ...state, attachments: action.attachments, failedSubmission: undefined, send: 'idle', savedSubmission: false }
    case 'replyChanged': return state.send === 'pending_confirmation' || state.send === 'sending' ? state : { ...state, replyTo: action.message, failedSubmission: undefined, send: 'idle', savedSubmission: false }
    case 'scrollPositionChanged': return JSON.stringify(state.scrollPosition) === JSON.stringify(action.position) ? state : { ...state, scrollPosition: action.position }
    case 'sendStarted': return { ...state, send: 'sending', failedSubmission: action.submission, attachments: action.attachments ?? state.attachments }
    case 'sendSucceeded': return { ...merge(state, [action.message]), send: 'idle', draft: '', attachments: [], replyTo: undefined, savedSubmission: true, failedSubmission: undefined }
    case 'sendFailed': return { ...state, send: action.submission.pendingConfirmation ? 'pending_confirmation' : 'failed', failedSubmission: action.submission }
    case 'newMessageNoticeRead': return { ...state, newMessageCount: 0 }
    case 'meetingDeleted': return createInitialChatState(state.meetingId, state.generation)
  }
}

export type { ChatMessage, ChatScrollPosition, ChatState, ConnectionState, FailedSubmission, HistoryState, SendState }
