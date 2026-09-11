export type MessageAuthorKind = 'human' | 'agent' | 'system'
export type ConnectionState = 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'disconnected' | 'rejected'
export type HistoryState = 'idle' | 'loading' | 'appending' | 'caughtUp' | 'error'
export type SendState = 'idle' | 'sending' | 'failed'
export type PresenceState = 'online' | 'offline'
export type AgentActivityState = 'waiting' | 'processing' | 'blocked' | 'disconnected' | 'unknown'

export type MessageMention = { participantId: string; displayName: string; startOffset: number; length: number }
export type MessageAuthor = { participantId?: string; kind: MessageAuthorKind; displayName: string }
export type ChatMessage = { messageId: string; meetingId: string; sequence: number; author: MessageAuthor; content: string; mentions: MessageMention[]; createdAt: Date }
export type MessagePage = { items: ChatMessage[]; hasMoreBefore: boolean; hasMoreAfter: boolean; firstSequence?: number; lastSequence?: number; nextBeforeSequence?: number; nextAfterSequence?: number }
export type MentionCandidate = { participantId: string; startOffset: number; length: number; displayName: string }
export type FailedSubmission = { clientSubmissionId: string; content: string; candidates: MentionCandidate[]; errorCode?: string; retryAfterSeconds?: number }
export type PresenceSnapshot = { onlineParticipantIds: string[]; highWatermarkSequence: number }
export type AgentActivity = {
  meetingId: string; participantId: string; state: AgentActivityState; activeTaskId?: string; failureCode?: string; changedAt: Date
  revision?: number; reason?: string; retryExhausted?: boolean; nextRetryAt?: Date; connectionDisposition?: string
}

export type ChatState = {
  meetingId: string; generation: number; messagesById: Record<string, ChatMessage>; orderedMessageIds: string[]
  earliestLoadedSequence?: number; liveBaselineSequence: number; lastAppliedSequence: number; highWatermarkSequence: number
  hasEarlierMessages: boolean; connection: ConnectionState; history: HistoryState; presence: Record<string, PresenceState>; agentActivities: Record<string, AgentActivity>
  draft: string; send: SendState; failedSubmission?: FailedSubmission; newMessageCount: number
}
