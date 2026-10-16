import { beforeEach, expect, it } from 'vitest'
import { MessageNotifications } from './messageNotifications'
import type { ChatMessage } from '../types/realtimeChat'
const message = (sequence: number, visibility: 'public' | 'private' = 'private', sender = 'bob', recipient = 'me'): ChatMessage => ({ meetingId: 'meeting', messageId: `m${sequence}`, sequence, visibility, recipient: { participantId: recipient, displayName: recipient }, author: { participantId: sender, displayName: sender, kind: 'human' }, content: 'secret body', mentions: [], createdAt: new Date() })
beforeEach(() => localStorage.clear())
it('notifies other scopes without counting, and never exposes another pair or own sends', () => {
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
