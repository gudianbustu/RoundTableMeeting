import { MessageNotifications } from './messageNotifications'

type Bucket = { count: number; last: number; through: number; read: number }
type Saved = { participantId: string; through: number; scopes: Record<string, Bucket> }
type Delta = { participantId: string; throughSequence: number; scopes: { scope: 'public' | 'private'; peerParticipantId: string | null; count: number; lastSequence: number }[] }
const empty = (): Bucket => ({ count: 0, last: 0, through: 0, read: 0 })
const scopeKey = (entry: Delta['scopes'][number]) => entry.scope === 'public' ? 'public' : entry.peerParticipantId!
const valid = (n: number) => Number.isSafeInteger(n) && n >= 0

async function fetchActivity(meeting: string, after: number | undefined, signal: AbortSignal): Promise<Delta> {
  const query = after === undefined ? '' : '?afterSequence=' + after
  const response = await fetch('/api/meetings/' + encodeURIComponent(meeting) + '/unread-activity' + query, { signal, credentials: 'same-origin' })
  if (!response.ok) throw new Error('unread_activity_' + response.status)
  const result: Delta = await response.json()
  if (!result.participantId || !valid(result.throughSequence) || !Array.isArray(result.scopes) ||
      result.scopes.some(s => !valid(s.count) || !valid(s.lastSequence) || s.lastSequence > result.throughSequence ||
        !(s.scope === 'public' || s.scope === 'private' && /^[\da-f-]{36}$/i.test(s.peerParticipantId ?? ''))))
    throw new Error('invalid_unread_activity')
  return result
}

export class AccountUnreadStore {
  readonly meetings = new Map<string, Saved>()
  readonly prefix: string
  private queues = new Map<string, Promise<void>>()
  private dirty = new Set<string>()
  private syncing = new Set<string>()
  private active = new Set<string>()
  private stop = new AbortController()
  private changed: () => void
  constructor(account: string, changed: () => void) {
    this.changed = changed
    this.prefix = 'rtm-account-unread-v1:' + account + ':'
  }
  dispose() { this.stop.abort() }
  list(ids: string[]) {
    const previous = this.active
    this.active = new Set(ids)
    for (const id of ids) if (!previous.has(id)) this.refresh(id)
    for (const id of previous) if (!this.active.has(id)) {
      this.meetings.delete(id)
      try { localStorage.removeItem(this.prefix + id) } catch { /* Optional storage. */ }
    }
    this.changed()
  }
  refreshAll() { for (const id of this.active) this.refresh(id) }
  changedInOtherTab(key: string | null) {
    if (!key?.startsWith(this.prefix)) return
    const id = key.slice(this.prefix.length)
    if (!this.active.has(id)) return
    const value = this.load(id)
    if (value) { this.meetings.set(id, value); this.changed() }
  }
  private load(id: string): Saved | undefined {
    try {
      const raw = localStorage.getItem(this.prefix + id)
      if (raw) {
        const value = JSON.parse(raw) as Saved
        if (value.participantId && valid(value.through) && value.scopes && Object.values(value.scopes).every(b =>
          b && valid(b.count) && valid(b.last) && valid(b.through) && valid(b.read))) return value
      }
    } catch { /* The current tab can continue in memory. */ }
    return this.meetings.get(id)
  }
  private save(id: string, state: Saved) {
    if (this.stop.signal.aborted || !this.active.has(id)) return
    this.meetings.set(id, state)
    try { localStorage.setItem(this.prefix + id, JSON.stringify(state)) } catch { /* Optional device storage. */ }
    this.changed()
  }
  private queue(id: string, operation: () => Promise<void>) {
    const run = async () => {
      if (this.stop.signal.aborted || !this.active.has(id)) return
      if (navigator.locks) await navigator.locks.request(this.prefix + id, { signal: this.stop.signal }, operation)
      else await operation()
    }
    const pending = (this.queues.get(id) ?? Promise.resolve()).then(run).catch(() => {})
    this.queues.set(id, pending)
    void pending.then(() => { if (this.queues.get(id) === pending) this.queues.delete(id) })
    return pending
  }
  refresh(id: string) {
    if (!this.active.has(id)) return
    this.dirty.add(id)
    if (this.syncing.has(id)) return
    this.syncing.add(id)
    void this.queue(id, async () => {
      while (this.dirty.delete(id) && !this.stop.signal.aborted) await this.synchronize(id)
    }).finally(() => { this.syncing.delete(id); if (this.dirty.has(id) && this.active.has(id) && !this.stop.signal.aborted) this.refresh(id) })
  }
  private async synchronize(id: string) {
    let state = this.load(id)
    let delta = await fetchActivity(id, state?.through, this.stop.signal)
    if (!state || state.participantId !== delta.participantId) {
      let storage: Storage | undefined
      try { storage = localStorage } catch { /* Optional device storage. */ }
      const old = new MessageNotifications(id, delta.participantId, storage)
      if (state && old.state.after === undefined) delta = await fetchActivity(id, undefined, this.stop.signal)
      state = { participantId: delta.participantId, through: old.state.after ?? delta.throughSequence, scopes: {} }
      const counts = new Map([...old.unreadPrivate].map(([key, seq]) => [key, seq.size]))
      counts.set('public', old.unreadPublic.size)
      for (const [key, count] of counts) if (count) state.scopes[key] = {
        count, last: old.state.pending[key] ?? state.through, through: state.through, read: old.state.cleared[key] ?? 0,
      }
      if (old.state.after !== undefined) delta = await fetchActivity(id, old.state.after, this.stop.signal)
    }
    for (const entry of delta.scopes) {
      const key = scopeKey(entry), bucket = state.scopes[key] ?? empty()
      if (entry.lastSequence <= Math.max(bucket.through, bucket.read)) continue
      // A visible conversation may have advanced ahead of the meeting-wide poll.
      // Ask for only its unseen suffix if the aggregate straddles that boundary.
      const since = Math.max(bucket.through, bucket.read)
      const slice = since > state.through ? await fetchActivity(id, since, this.stop.signal) : delta
      const row = slice === delta ? entry : slice.scopes.find(item => scopeKey(item) === key)
      if (row) state.scopes[key] = { ...bucket, count: bucket.count + row.count, last: row.lastSequence, through: slice.throughSequence }
    }
    state.through = Math.max(state.through, delta.throughSequence)
    this.save(id, state)
  }
  readThrough(id: string, scope: string, sequence: number) {
    if (!valid(sequence) || !sequence) return
    return this.queue(id, async () => {
      let state = this.load(id)
      if (!state) { await this.synchronize(id); state = this.load(id) }
      if (!state) return
      const bucket = state.scopes[scope] ?? empty()
      if (sequence <= bucket.read) return
      let count = 0, through = Math.max(bucket.through, sequence), last = bucket.last
      if (sequence < bucket.last) {
        const delta = await fetchActivity(id, sequence, this.stop.signal)
        const row = delta.scopes.find(item => scopeKey(item) === scope)
        count = row?.count ?? 0; through = delta.throughSequence; last = row?.lastSequence ?? sequence
      }
      state.scopes[scope] = { count, through, last, read: sequence }
      this.save(id, state)
    })
  }
}
