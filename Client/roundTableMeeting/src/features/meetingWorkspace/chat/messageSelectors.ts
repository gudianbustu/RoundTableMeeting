import type { ChatMessage, ChatState } from '../types/realtimeChat'
export function selectMessages(state: ChatState): ChatMessage[] { return state.orderedMessageIds.map(id => state.messagesById[id]) }
export function selectHasSequenceGap(state: ChatState): boolean { return selectMessages(state).some(message => message.sequence > state.liveBaselineSequence && message.sequence > state.lastAppliedSequence + 1) }
