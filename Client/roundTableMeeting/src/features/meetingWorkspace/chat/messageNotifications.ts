import type { ChatMessage } from '../types/realtimeChat'

export type NotificationState = { after?: number; pending: Record<string, number>; cleared: Record<string, number> }
export class MessageNotifications {
  state: NotificationState = { pending: {}, cleared: {} }
  readonly meetingId: string
  readonly participantId: string
  readonly storage?: Storage
  constructor(meetingId: string, participantId: string, storage?: Storage) {
    this.meetingId = meetingId; this.participantId = participantId; this.storage = storage
    try {
      const saved = JSON.parse(storage?.getItem(this.key) ?? 'null')
      if (saved && Number.isSafeInteger(saved.after) && saved.after >= 0) {
        this.state.after = saved.after
        for (const field of ['pending', 'cleared'] as const) {
          for (const [key, value] of Object.entries(saved[field] ?? {})) {
            if (key !== '__proto__' && key !== 'constructor' && Number.isSafeInteger(value) && (value as number) >= 0) this.state[field][key] = value as number
          }
        }
      }
    } catch { /* Storage is optional; retain in-memory state. */ }
  }
  get key() { return `rtm-message-notifications-v1:${this.meetingId}:${this.participantId}` }
  save() { try { this.storage?.setItem(this.key, JSON.stringify(this.state)) } catch { /* Private browsing or quota. */ } }
  clear(scope: string) {
    this.state.cleared[scope] = Math.max(this.state.cleared[scope] ?? 0, this.state.pending[scope] ?? 0)
    delete this.state.pending[scope]; this.save()
  }
  receive(message: ChatMessage, activeScope: string | undefined) {
    if (message.meetingId !== this.meetingId || message.author.participantId === this.participantId) return
    const scope = message.visibility === 'private'
      ? message.recipient?.participantId === this.participantId ? message.author.participantId : undefined
      : 'public'
    if (!scope || message.sequence <= (this.state.cleared[scope] ?? 0)) return
    this.state.pending[scope] = Math.max(this.state.pending[scope] ?? 0, message.sequence)
    if (scope === activeScope) this.clear(scope)
    this.save()
  }
  remove() { this.state = { pending: {}, cleared: {} }; try { this.storage?.removeItem(this.key) } catch { /* Optional storage. */ } }
}
