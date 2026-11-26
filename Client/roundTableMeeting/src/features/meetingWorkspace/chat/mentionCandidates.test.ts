import { expect, it } from 'vitest'
import { replyMentionName } from './mentionCandidates'

it('adds a reply mention by participant identity without duplicating or notifying oneself', () => {
  const person = { participantId: 'agent', displayName: '小栗', type: 'agent' as const, isCurrentUser: false, isCreator: false }
  expect(replyMentionName('agent', '保留草稿', [person], true)).toBe('小栗')
  expect(replyMentionName('agent', '草稿 @小栗 ', [person], false)).toBeUndefined()
  expect(replyMentionName('agent', '', [{ ...person, isCurrentUser: true }], true)).toBeUndefined()
  expect(replyMentionName('agent', '', [{ ...person, membershipState: 'left' }], true)).toBeUndefined()
  expect(replyMentionName('agent', '', [{ ...person, displayName: '所有人' }], true)).toBeUndefined()
  expect(replyMentionName('agent', '', [{ ...person, displayName: '所有人' }], false)).toBe('所有人')
})
