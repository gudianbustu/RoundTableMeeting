import { describe, expect, it } from 'vitest'
import { chatReducer, createInitialChatState } from './messageReducer'

const message = (id: string, sequence: number) => ({ messageId: id, meetingId: '11111111-1111-4111-8111-111111111111', sequence, author: { participantId: '22222222-2222-4222-8222-222222222222', kind: 'human' as const, displayName: 'Alice' }, content: id, mentions: [], createdAt: new Date() })
const activity = (state: 'waiting' | 'processing' | 'disconnected', changedAt: string) => ({ meetingId: '11111111-1111-4111-8111-111111111111', participantId: '33333333-3333-4333-8333-333333333333', state, changedAt: new Date(changedAt) })

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

  it('keeps Agent activity separate from human presence and rejects stale events', () => {
    const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
    let state = createInitialChatState(scope.meetingId, scope.generation)
    state = chatReducer(state, { ...scope, type: 'agentActivitySnapshot', activities: [activity('waiting', '2026-09-05T01:00:00Z')] })
    state = chatReducer(state, { ...scope, type: 'presenceChanged', participantId: '33333333-3333-4333-8333-333333333333', state: 'offline' })
    state = chatReducer(state, { ...scope, type: 'agentActivityChanged', activity: activity('processing', '2026-09-05T01:00:02Z') })
    state = chatReducer(state, { ...scope, type: 'agentActivityChanged', activity: activity('disconnected', '2026-09-05T01:00:01Z') })
    expect(state.agentActivities['33333333-3333-4333-8333-333333333333'].state).toBe('processing')
    expect(state.presence['33333333-3333-4333-8333-333333333333']).toBe('offline')
  })

  it('replaces missing snapshot entries without overwriting a newer live activity', () => {
    const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
    let state = createInitialChatState(scope.meetingId, scope.generation)
    state.agentActivities.old = { ...activity('waiting', '2026-09-05T01:00:00Z'), participantId: 'old' }
    state = chatReducer(state, { ...scope, type: 'agentActivityChanged', activity: activity('processing', '2026-09-05T01:00:03Z') })
    state = chatReducer(state, { ...scope, type: 'agentActivitySnapshot', activities: [activity('waiting', '2026-09-05T01:00:02Z')] })
    expect(state.agentActivities.old).toBeUndefined()
    expect(state.agentActivities['33333333-3333-4333-8333-333333333333'].state).toBe('processing')
  })

  it('marks cached Agent activity unknown while the realtime connection is unavailable', () => {
    const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
    let state = createInitialChatState(scope.meetingId, scope.generation)
    state = chatReducer(state, { ...scope, type: 'agentActivitySnapshot', activities: [activity('waiting', '2026-09-05T01:00:00Z')] })
    state = chatReducer(state, { ...scope, type: 'connectionStateChanged', state: 'reconnecting' })
    expect(state.agentActivities['33333333-3333-4333-8333-333333333333'].state).toBe('unknown')
  })
})
