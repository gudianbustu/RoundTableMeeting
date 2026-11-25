import type { ChatMessage } from '../types/realtimeChat'

// The badge displays 999+ beyond this point, including after reload.
const unreadBadgeLimit = 1000

export type NotificationState = { after?: number; pending: Record<string, number>; cleared: Record<string, number> }
export class MessageNotifications {
  state: NotificationState = { pending: {}, cleared: {} }
  readonly unreadPrivate = new Map<string, Set<number>>()
  readonly unreadPublic = new Set<number>()
  readonly meetingId: string
  readonly participantId: string
  readonly storage?: Storage
  constructor(meetingId: string, participantId: string, storage?: Storage) {
    this.meetingId = meetingId; this.participantId = participantId; this.storage = storage
    try {
      const saved = JSON.parse(storage?.getItem(this.key) ?? 'null')
      if (saved && Number.isSafeInteger(saved.after) && saved.after >= 0) {
        this.state.after = saved.after
        if (Array.isArray(saved.unreadPublic)) {
          for (const sequence of saved.unreadPublic) {
            if (this.unreadPublic.size >= unreadBadgeLimit) break
            if (Number.isSafeInteger(sequence) && sequence > 0) this.unreadPublic.add(sequence)
          }
        }
        for (const field of ['pending', 'cleared'] as const) {
          for (const [key, value] of Object.entries(saved[field] ?? {})) {
            if (key !== '__proto__' && key !== 'constructor' && Number.isSafeInteger(value) && (value as number) >= 0) this.state[field][key] = value as number
          }
        }
        for (const [peer, latest] of Object.entries(this.state.pending)) {
          if (peer === 'public' || latest <= (this.state.cleared[peer] ?? 0)) continue
          // Older clients retained only the latest pending sequence for each private conversation.
          const savedSequences = saved.unreadPrivate?.[peer]
          const sequences = new Set<number>()
          for (const value of Array.isArray(savedSequences) ? savedSequences : saved.unreadPrivate === undefined ? [latest] : []) {
            if (Number.isSafeInteger(value) && value > (this.state.cleared[peer] ?? 0) && value <= latest) sequences.add(value)
            if (sequences.size === unreadBadgeLimit) break
          }
          if (sequences.size) this.unreadPrivate.set(peer, sequences)
        }
      }
    } catch { /* Storage is optional; retain in-memory state. */ }
  }
  get key() { return `rtm-message-notifications-v1:${this.meetingId}:${this.participantId}` }
  save() { try { this.storage?.setItem(this.key, JSON.stringify({ ...this.state, unreadPublic: [...this.unreadPublic],
      unreadPrivate: Object.fromEntries(Array.from(this.unreadPrivate, ([peer, sequences]) => [peer, [...sequences]])) })) } catch { /* Private browsing or quota. */ } }
  clear(scope: string) {
    this.state.cleared[scope] = Math.max(this.state.cleared[scope] ?? 0, this.state.pending[scope] ?? 0)
    if (scope === 'public') this.unreadPublic.clear()
    else this.unreadPrivate.delete(scope)
    delete this.state.pending[scope]; this.save()
  }
  receive(message: ChatMessage, activeScope: string | undefined, selectedScope = activeScope) {
    if (message.meetingId !== this.meetingId || message.author.participantId === this.participantId) return
    const scope = message.visibility === 'private'
      ? message.recipient?.participantId === this.participantId ? message.author.participantId : undefined
      : 'public'
    if (!scope || message.sequence <= (this.state.cleared[scope] ?? 0)) return
    if (scope === 'public' && this.unreadPublic.size < unreadBadgeLimit && selectedScope && selectedScope !== 'public' && message.sequence > (this.state.after ?? 0)) {
      this.unreadPublic.add(message.sequence)
    }
    if (scope !== 'public' && scope !== activeScope && message.sequence > (this.state.after ?? 0)) {
      const unread = this.unreadPrivate.get(scope) ?? new Set<number>()
      if (unread.size < unreadBadgeLimit) unread.add(message.sequence)
      this.unreadPrivate.set(scope, unread)
    }
    this.state.pending[scope] = Math.max(this.state.pending[scope] ?? 0, message.sequence)
    if (scope === activeScope) this.clear(scope)
    this.save()
  }
  remove() { this.unreadPrivate.clear(); this.unreadPublic.clear(); this.state = { pending: {}, cleared: {} }; try { this.storage?.removeItem(this.key) } catch { /* Optional storage. */ } }
}
