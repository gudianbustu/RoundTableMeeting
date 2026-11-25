import { describe, expect, it, vi } from 'vitest'
import { listMessages, mapMessage, RealtimeChatApiError } from './listMessages'

const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('@/api/client/apiClient', () => ({
  getApiClient: () => ({ api: { meetings: { byMeetingId: () => ({ messages: { get } }) } } }),
}))

const meetingId = '11111111-1111-4111-8111-111111111111'
const participantId = '22222222-2222-4222-8222-222222222222'
const messageId = '33333333-3333-4333-8333-333333333333'

function wire(content: string, offsets: number[]) {
  return {
    messageId, meetingId, sequence: 1,
    author: { kind: 'agent', participantId, displayName: 'Agent A' },
    content, createdAt: '2026-09-11T00:00:00Z',
    mentions: offsets.map(startOffset => ({ participantId, displayName: 'B', startOffset, length: 2 })),
  }
}

describe('Agent collaboration history', () => {
  it.each([
    ['@B 😀 @B', [0, 6]],
    ['😀@B', [2]],
    ['e\u0301 @B', [3]],
  ] as const)('preserves UTF-16 mention ranges in %s', (content, offsets) => {
    const message = mapMessage(wire(content, [...offsets]))
    expect(message.author.kind).toBe('agent')
    expect(message.mentions.map(mention => message.content.slice(mention.startOffset, mention.startOffset + mention.length)))
      .toEqual(offsets.map(() => '@B'))
  })

  it('loads a legal Agent reply through the complete history response mapper', async () => {
    get.mockResolvedValueOnce({ items: [wire('😀 @B', [3])], hasMoreBefore: false, hasMoreAfter: false, firstSequence: 1, lastSequence: 1 })
    const page = await listMessages(meetingId)
    expect(page.items).toHaveLength(1)
    expect(page.items[0].mentions[0].startOffset).toBe(3)
  })

  it('still rejects an out-of-bounds range rather than accepting malformed history', async () => {
    get.mockResolvedValueOnce({ items: [wire('😀 @B', [4])], hasMoreBefore: false, hasMoreAfter: false })
    await expect(listMessages(meetingId)).rejects.toBeInstanceOf(RealtimeChatApiError)
  })
})

it('accepts Kiota null-object decoding for public recipients but rejects missing private recipients', () => {
  const payload = { ...wire('public', []), visibility: 'public', recipient: {} }
  expect(mapMessage(payload).recipient).toBeUndefined()
  expect(() => mapMessage({ ...payload, visibility: 'private' })).toThrow(RealtimeChatApiError)
})

it('maps the explicit original text attachment on history and realtime payloads', () => {
  const id = '44444444-4444-4444-8444-444444444444'
  const payload = wire('短预览', [])
  expect(mapMessage(payload).originalTextAttachmentId).toBeUndefined()
  expect(mapMessage({ ...payload, originalTextAttachmentId: id }).originalTextAttachmentId).toBe(id)
  expect(mapMessage({ ...payload, additionalData: { originalTextAttachmentId: id } }).originalTextAttachmentId).toBe(id)
})
