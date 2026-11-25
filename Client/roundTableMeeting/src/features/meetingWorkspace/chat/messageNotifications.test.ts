import { beforeEach, expect, it } from 'vitest'
import { MessageNotifications } from './messageNotifications'
import type { ChatMessage } from '../types/realtimeChat'
const message = (sequence: number, visibility: 'public' | 'private' = 'private', sender = 'bob', recipient = 'me'): ChatMessage => ({ meetingId: 'meeting', messageId: `m${sequence}`, sequence, visibility, recipient: { participantId: recipient, displayName: recipient }, author: { participantId: sender, displayName: sender, kind: 'human' }, content: 'secret body', mentions: [], createdAt: new Date() })
beforeEach(() => localStorage.clear())
it('notifies other scopes, and never exposes another pair or own sends', () => {
  const tracker = new MessageNotifications('meeting', 'me')
  tracker.receive(message(1), 'public')
  tracker.receive(message(2, 'private', 'alice', 'charlie'), 'public')
  tracker.receive(message(3, 'private', 'me', 'bob'), 'public')
  tracker.receive(message(4, 'public'), 'bob')
  expect(tracker.state.pending).toEqual({ bob: 1, public: 4 })
})
it('clearing a scope prevents replay from relighting it but accepts newer messages', () => {
  const tracker = new MessageNotifications('meeting', 'me')
  tracker.receive(message(4), 'public'); tracker.clear('bob'); tracker.receive(message(4), 'public')
  expect(tracker.state.pending).toEqual({})
  tracker.receive(message(5), 'public'); expect(tracker.state.pending).toEqual({ bob: 5 })
})
it('visible conversation consumes the hint while a hidden page retains it', () => {
  const tracker = new MessageNotifications('meeting', 'me')
  tracker.receive(message(1), 'bob'); expect(tracker.state.pending).toEqual({})
  tracker.receive(message(2), undefined); expect(tracker.state.pending).toEqual({ bob: 2 })
})
it('persists only positions, isolates identities and meetings, and removes deleted meeting state', () => {
  const tracker = new MessageNotifications('meeting', 'me', localStorage)
  tracker.state.after = 3; tracker.receive(message(4), 'public')
  expect(localStorage.getItem(tracker.key)).not.toContain('secret body')
  expect(new MessageNotifications('meeting', 'me', localStorage).state).toEqual(tracker.state)
  expect(new MessageNotifications('meeting', 'other', localStorage).state.pending).toEqual({})
  expect(new MessageNotifications('other', 'me', localStorage).state.after).toBeUndefined()
  tracker.remove(); expect(localStorage.getItem(tracker.key)).toBeNull()
})
it('ignores foreign meeting events and corrupt persisted state', () => {
  localStorage.setItem('rtm-message-notifications-v1:meeting:me', '{bad')
  const tracker = new MessageNotifications('meeting', 'me', localStorage)
  tracker.receive({ ...message(1), meetingId: 'other' }, 'public')
  expect(tracker.state.pending).toEqual({})
})

it('counts distinct public events in private chats including replay and clears on return', () => {
  const tracker = new MessageNotifications('meeting', 'me', localStorage)
  tracker.state.after = 6
  tracker.receive(message(5, 'public'), 'bob')
  tracker.receive(message(7, 'public'), 'bob')
  tracker.receive(message(9, 'public'), undefined, 'alice')
  tracker.receive(message(8, 'public'), 'bob')
  tracker.receive(message(7, 'public'), 'bob')
  tracker.receive(message(10, 'private'), 'alice')
  expect(tracker.unreadPublic.size).toBe(3)
  const restored = new MessageNotifications('meeting', 'me', localStorage)
  expect(restored.unreadPublic.size).toBe(3)
  const otherDevice = new MessageNotifications('meeting', 'me')
  otherDevice.clear('public')
  expect(restored.unreadPublic.size).toBe(3)
  restored.clear('public')
  restored.receive(message(8, 'public'), 'alice')
  restored.receive(message(11, 'public'), undefined, 'public')
  expect(restored.unreadPublic.size).toBe(0)
})

it('bounds the displayed public unread counter and clears through the latest sequence', () => {
  const tracker = new MessageNotifications('meeting', 'me', localStorage)
  tracker.state.after = 0
  for (let sequence = 1; sequence <= 1020; sequence++) tracker.receive(message(sequence, 'public'), 'bob')
  expect(new MessageNotifications('meeting', 'me', localStorage).unreadPublic.size).toBe(1000)
  tracker.clear('public')
  tracker.receive(message(1019, 'public'), 'bob')
  expect(tracker.unreadPublic.size).toBe(0)
})

it('keeps two private peers and public unread independent across replay, reload and clearing', () => {
  const tracker = new MessageNotifications('meeting', 'me', localStorage)
  tracker.state.after = 1
  for (const [seq, peer] of [[2, 'alice'], [4, 'bob'], [3, 'alice'], [4, 'bob']] as const)
    tracker.receive(message(seq, 'private', peer), 'charlie')
  tracker.receive(message(5, 'public'), 'charlie')
  const restored = new MessageNotifications('meeting', 'me', localStorage)
  expect(restored.unreadPrivate.get('alice')?.size).toBe(2)
  expect(restored.unreadPrivate.get('bob')?.size).toBe(1)
  restored.clear('alice')
  restored.receive(message(3, 'private', 'alice'), 'bob')
  expect(restored.unreadPrivate.has('alice')).toBe(false)
  expect(restored.unreadPrivate.get('bob')?.size).toBe(1)
  expect(restored.unreadPublic.size).toBe(1)
})

it('restores one known pending event from older storage and bounds each peer separately', () => {
  const initial = new MessageNotifications('meeting', 'me', localStorage)
  localStorage.setItem(initial.key, JSON.stringify({ after: 7, pending: { bob: 7 }, cleared: {} }))
  const tracker = new MessageNotifications('meeting', 'me', localStorage)
  expect(tracker.unreadPrivate.get('bob')?.size).toBe(1)
  for (let sequence = 8; sequence <= 1010; sequence++) tracker.receive(message(sequence), 'public')
  tracker.receive(message(1011, 'private', 'alice'), 'public')
  const restored = new MessageNotifications('meeting', 'me', localStorage)
  expect(restored.unreadPrivate.get('bob')?.size).toBe(1000)
  expect(restored.unreadPrivate.get('alice')?.size).toBe(1)
})
