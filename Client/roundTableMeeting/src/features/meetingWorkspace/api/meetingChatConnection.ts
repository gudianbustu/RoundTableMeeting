import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import type { ChatMessage, ConnectionState, PresenceState } from '../types/realtimeChat'
import { mapMessage } from './listMessages'
export type ChatConnectionCallbacks = { onState: (state: ConnectionState) => void; onMessage: (message: ChatMessage) => void; onPresence: (participantId: string, state: PresenceState) => void; onDeleted: () => void; onResubscribed?: (subscription: Subscription) => void }
export type Subscription = { onlineParticipantIds: string[]; highWatermarkSequence: number }
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
    this.connection.on('messageAppended', value => callbacks.onMessage(mapMessage(value))); this.connection.on('participantPresenceChanged', (event: any) => callbacks.onPresence(event.participantId, event.state === 'online' ? 'online' : 'offline')); this.connection.on('meetingDeleted', callbacks.onDeleted)
  }
  private subscribe(meetingId: string): Promise<Subscription> { return this.connection.invoke<Subscription>('SubscribeMeeting', meetingId) }
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
