import { describe, expect, it } from 'vitest'
import { mapAgentActivity } from './meetingChatConnection'

const data = { meetingId: '11111111-1111-4111-8111-111111111111', participantId: '22222222-2222-4222-8222-222222222222', state: 'blocked', changedAt: '2026-09-09T00:00:00Z' }

describe('Agent activity protocol mapping', () => {
  it('keeps legacy compatibility and maps versioned recovery facts without coercing revisions', () => {
    expect(mapAgentActivity(data).revision).toBeUndefined()
    const mapped = mapAgentActivity({ ...data, revision: 12, reason: 'retry_pending', retryExhausted: false, nextRetryAt: '2026-09-09T00:00:02Z', connectionDisposition: 'taken_over' })
    expect(mapped).toMatchObject({ revision: 12, reason: 'retry_pending', retryExhausted: false, connectionDisposition: 'taken_over' })
    expect(mapped.nextRetryAt?.toISOString()).toBe('2026-09-09T00:00:02.000Z')
  })
  it.each([-1, 1.5, Number.MAX_SAFE_INTEGER + 1, '12', null])('rejects an invalid revision %s', revision => {
    expect(() => mapAgentActivity({ ...data, revision })).toThrow('invalid_agent_activity_revision')
  })
})
