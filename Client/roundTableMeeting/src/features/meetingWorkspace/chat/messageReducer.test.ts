import { describe, expect, it } from 'vitest'
import { chatReducer, createInitialChatState } from './messageReducer'

const message = (id: string, sequence: number) => ({ messageId: id, meetingId: '11111111-1111-4111-8111-111111111111', sequence, author: { participantId: '22222222-2222-4222-8222-222222222222', kind: 'human' as const, displayName: 'Alice' }, content: id, mentions: [], createdAt: new Date() })

describe('chatReducer', () => {
  it('deduplicates events and advances only across contiguous sequences', () => {
    const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
    let state = createInitialChatState(scope.meetingId, scope.generation)
    state = chatReducer(state, { ...scope, type: 'historyPageMerged', initial: true, page: { items: [message('two', 2)], hasMoreBefore: false, hasMoreAfter: false } })
    expect(state.lastAppliedSequence).toBe(2)
    state = chatReducer(state, { ...scope, type: 'messageEventReceived', message: message('one', 1) })
    expect(state.lastAppliedSequence).toBe(2)
    state = chatReducer(state, { ...scope, type: 'messageEventReceived', message: message('one', 1) })
    expect(state.newMessageCount).toBe(1)
  })

  it('replaces presence snapshot instead of retaining stale online participants', () => {
    const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
    let state = createInitialChatState(scope.meetingId, scope.generation)
    state = chatReducer(state, { ...scope, type: 'presenceChanged', participantId: 'old', state: 'online' })
    state = chatReducer(state, { ...scope, type: 'presenceSnapshot', participantIds: ['new'], highWatermarkSequence: 0 })
    expect(state.presence).toEqual({ new: 'online' })
  })
})
