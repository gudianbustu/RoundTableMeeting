import { describe, expect, it } from 'vitest'
import { chatReducer, createInitialChatState } from './messageReducer'

const message = (id: string, sequence: number) => ({ messageId: id, meetingId: '11111111-1111-4111-8111-111111111111', sequence, author: { participantId: '22222222-2222-4222-8222-222222222222', kind: 'human' as const, displayName: 'Alice' }, content: id, mentions: [], createdAt: new Date() })
const activity = (state: 'waiting' | 'processing' | 'disconnected', changedAt: string) => ({ meetingId: '11111111-1111-4111-8111-111111111111', participantId: '33333333-3333-4333-8333-333333333333', state, changedAt: new Date(changedAt) })

describe('chatReducer', () => {
  it('preserves an uncertain submission across reconnect and ignores its old response until an idempotent retry completes', () => {
    const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
    const submission = { clientSubmissionId: 'same-submission', content: 'Keep pending question', candidates: [] }
    let state = chatReducer(createInitialChatState(scope.meetingId), { ...scope, type: 'draftChanged', draft: submission.content })
    state = chatReducer(state, { ...scope, type: 'sendStarted', submission })
    state = chatReducer(state, { ...scope, generation: 2, type: 'connectionStarted' })
    expect(state).toMatchObject({ generation: 2, draft: submission.content, send: 'pending_confirmation', failedSubmission: submission })
    expect(chatReducer(state, { ...scope, generation: 2, type: 'draftChanged', draft: 'must stay immutable' })).toBe(state)
    expect(chatReducer(state, { ...scope, type: 'sendSucceeded', message: message('accepted', 1) })).toBe(state)
    state = chatReducer(state, { ...scope, generation: 2, type: 'sendStarted', submission: state.failedSubmission! })
    state = chatReducer(state, { ...scope, generation: 2, type: 'sendSucceeded', message: message('accepted', 1) })
    expect(state).toMatchObject({ draft: '', send: 'idle', orderedMessageIds: ['accepted'] })
    expect(state.failedSubmission).toBeUndefined()
  })

  it('uses revision over timestamps and restores an unchanged version after reconnect', () => {
    const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
    const current = { ...activity('disconnected', '2026-09-09T00:00:00Z'), revision: 12 }
    let state = chatReducer(createInitialChatState(scope.meetingId), { ...scope, type: 'agentActivityChanged', activity: current })
    state = chatReducer(state, { ...scope, type: 'agentActivityChanged', activity: { ...activity('processing', '2026-09-10T00:00:00Z'), revision: 11 } })
    state = chatReducer(state, { ...scope, type: 'agentActivitySnapshot', activities: [{ ...activity('waiting', '2026-09-10T00:00:00Z'), revision: 11 }] })
    state = chatReducer(state, { ...scope, type: 'presenceChanged', participantId: current.participantId, state: 'online' })
    expect(state.agentActivities[current.participantId]).toEqual(current)
    state = chatReducer(state, { ...scope, type: 'agentActivityChanged', activity: activity('waiting', '2026-09-11T00:00:00Z') })
    expect(state.agentActivities[current.participantId]).toEqual(current)
    state = chatReducer(state, { ...scope, type: 'connectionStateChanged', state: 'reconnecting' })
    expect(state.agentActivities[current.participantId].state).toBe('unknown')
    state = chatReducer(state, { ...scope, type: 'agentActivitySnapshot', activities: [current] })
    expect(state.agentActivities[current.participantId]).toEqual(current)
    const next = { ...activity('processing', '2026-09-08T00:00:00Z'), revision: 13 }
    state = chatReducer(state, { ...scope, type: 'agentActivityChanged', activity: next })
    expect(state.agentActivities[current.participantId]).toEqual(next)
  })

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

it('locks reply, attachments and text while a submission is in flight', () => {
  const scope = { meetingId: '11111111-1111-4111-8111-111111111111', generation: 1 }
  const state = chatReducer(createInitialChatState(scope.meetingId), { ...scope, type: 'sendStarted', submission: { clientSubmissionId: 'pending', content: 'original', candidates: [] } })
  expect(chatReducer(state, { ...scope, type: 'replyChanged', message: message('reply', 1) })).toBe(state)
  expect(chatReducer(state, { ...scope, type: 'attachmentsChanged', attachments: [] })).toBe(state)
  expect(chatReducer(state, { ...scope, type: 'draftChanged', draft: 'another' })).toBe(state)
})
