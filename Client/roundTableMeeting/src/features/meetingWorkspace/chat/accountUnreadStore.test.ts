import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AccountUnreadStore } from './accountUnreadStore'

const peer = '33333333-3333-4333-8333-333333333333'
let events: { sequence: number; scope: string }[] = []
let store: AccountUnreadStore
beforeEach(() => {
  localStorage.clear()
  events = []
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    const after = new URL(url, 'https://test.local').searchParams.get('afterSequence')
    const rows = events.filter(event => after !== null && event.sequence > Number(after))
    const scopes = ['public', peer].flatMap(scope => {
      const match = rows.filter(row => row.scope === scope)
      return match.length ? [{ scope: scope === 'public' ? 'public' : 'private', peerParticipantId: scope === 'public' ? null : peer,
        count: match.length, lastSequence: match[match.length - 1].sequence }] : []
    })
    return new Response(JSON.stringify({ participantId: 'me', throughSequence: events.at(-1)?.sequence ?? 0, scopes }))
  }))
  store = new AccountUnreadStore('user', () => {})
})
afterEach(() => { store.dispose(); vi.unstubAllGlobals() })

it('counts deltas once and clears only visible messages of the selected conversation', async () => {
  store.list(['room'])
  await vi.waitFor(() => expect(store.meetings.get('room')?.through).toBe(0))
  events = [{ sequence: 1, scope: 'public' }, { sequence: 2, scope: peer }, { sequence: 3, scope: 'public' }]
  store.refresh('room'); store.refresh('room')
  await vi.waitFor(() => expect(store.meetings.get('room')?.scopes.public.count).toBe(2))
  await store.readThrough('room', 'public', 1)
  expect(store.meetings.get('room')?.scopes.public.count).toBe(1)
  expect(store.meetings.get('room')?.scopes[peer].count).toBe(1)
  await store.readThrough('room', peer, 2)
  expect(store.meetings.get('room')?.scopes[peer].count).toBe(0)
})

it('migrates existing peer badges and catches up from the old device cursor', async () => {
  localStorage.setItem('rtm-message-notifications-v1:room:me', JSON.stringify({
    after: 4, pending: { [peer]: 4 }, cleared: {}, unreadPrivate: { [peer]: [2,4] },
  }))
  events = [{ sequence: 5, scope: peer }]
  store.list(['room'])
  await vi.waitFor(() => expect(store.meetings.get('room')?.scopes[peer].count).toBe(3))
})
