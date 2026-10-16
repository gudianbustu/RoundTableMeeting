import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { AgentActivity, AgentActivityState, ChatMessage, ConnectionState, PresenceState } from '../types/realtimeChat'
import { mapMessage } from './listMessages'
export type ChatConnectionCallbacks = { onState: (state: ConnectionState) => void; onMessage: (message: ChatMessage) => void; onPresence: (participantId: string, state: PresenceState) => void; onAgentActivity: (activity: AgentActivity) => void; onDeleted: () => void; onMembershipLeft?: () => void; onResubscribed?: (subscription: Subscription) => void }
export type Subscription = { onlineParticipantIds: string[]; highWatermarkSequence: number; agentActivities: AgentActivity[] }
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
const isGuid = (value: unknown): value is string => typeof value === 'string' && guidPattern.test(value)

export function mapAgentActivity(value: any): AgentActivity {
  const changedAt = new Date(value?.changedAt)
  const state: AgentActivityState = ['waiting', 'processing', 'blocked', 'disconnected'].includes(value?.state) ? value.state : 'unknown'
  if (!isGuid(value?.meetingId) || !isGuid(value?.participantId) || Number.isNaN(changedAt.getTime())) throw new Error('invalid_agent_activity')
  if (value.revision !== undefined && (!Number.isSafeInteger(value.revision) || value.revision < 0)) throw new Error('invalid_agent_activity_revision')
  const nextRetryAt = value.nextRetryAt == null ? undefined : new Date(value.nextRetryAt)
  if (nextRetryAt && Number.isNaN(nextRetryAt.getTime())) throw new Error('invalid_agent_activity_retry_time')
  return {
    meetingId: value.meetingId, participantId: value.participantId, state,
    activeTaskId: isGuid(value?.activeTaskId) ? value.activeTaskId : undefined,
    failureCode: typeof value?.failureCode === 'string' ? value.failureCode : undefined, changedAt,
    revision: value.revision, reason: typeof value.reason === 'string' ? value.reason : undefined,
    retryExhausted: typeof value.retryExhausted === 'boolean' ? value.retryExhausted : undefined,
    nextRetryAt, connectionDisposition: typeof value.connectionDisposition === 'string' ? value.connectionDisposition : undefined,
  }
}

function mapSubscription(value: any): Subscription {
  if (!Array.isArray(value?.onlineParticipantIds) || !Number.isSafeInteger(Number(value?.highWatermarkSequence))) throw new Error('invalid_chat_subscription')
  return { onlineParticipantIds: value.onlineParticipantIds.filter(isGuid), highWatermarkSequence: Number(value.highWatermarkSequence), agentActivities: Array.isArray(value?.agentActivities) ? value.agentActivities.map(mapAgentActivity) : [] }
}
export class MeetingChatConnection {
  private readonly connection: HubConnection
  private readonly callbacks: ChatConnectionCallbacks
  private meetingId?: string
  private startPromise?: Promise<void>
  private stopRequested = false
  constructor(callbacks: ChatConnectionCallbacks) {
    this.callbacks = callbacks
    this.connection = new HubConnectionBuilder().withUrl('/hubs/meeting-chat', { withCredentials: true }).withAutomaticReconnect([0, 1000, 3000, 10000]).configureLogging(LogLevel.Warning).build()
    this.connection.onreconnecting(() => callbacks.onState('reconnecting')); this.connection.onreconnected(async () => { if (!this.meetingId) return; try { const subscription = await this.subscribe(this.meetingId); callbacks.onResubscribed?.(subscription); callbacks.onState('connected') } catch (error) { if ((error as { message?: string }).message?.includes('meeting_not_found')) { callbacks.onState('rejected'); callbacks.onDeleted() } else callbacks.onState('disconnected') } }); this.connection.onclose(() => callbacks.onState('disconnected'))
    this.connection.on('messageAppended', value => callbacks.onMessage(mapMessage(value))); this.connection.on('participantPresenceChanged', (event: any) => callbacks.onPresence(event.participantId, event.state === 'online' ? 'online' : 'offline')); this.connection.on('agentActivityChangedV1', (value: any) => callbacks.onAgentActivity(mapAgentActivity(value))); this.connection.on('meetingDeleted', callbacks.onDeleted); this.connection.on('meetingDeleting', callbacks.onDeleted); this.connection.on('membershipLeft', () => callbacks.onMembershipLeft?.())
  }
  private async subscribe(meetingId: string): Promise<Subscription> { return mapSubscription(await this.connection.invoke<Subscription>('SubscribeMeeting', meetingId)) }
  async start(meetingId: string): Promise<Subscription> {
    this.meetingId = meetingId
    this.stopRequested = false
    this.callbacks.onState('connecting')
    const pending = this.connection.start()
    this.startPromise = pending
    try {
      await pending
      if (this.stopRequested) throw new DOMException('Connection start cancelled', 'AbortError')
      const result = await this.subscribe(meetingId)
      if (this.stopRequested) throw new DOMException('Connection subscription cancelled', 'AbortError')
      this.callbacks.onState('connected')
      return result
    } finally {
      if (this.startPromise === pending) this.startPromise = undefined
    }
  }
  async stop(): Promise<void> {
    this.stopRequested = true
    await this.startPromise?.catch(() => undefined)
    if (this.connection.state !== HubConnectionState.Disconnected) await this.connection.stop()
  }
}
