import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { useMessageNotifications } from './useMessageNotifications'
import { MessageNotifications } from '../chat/messageNotifications'
import type { ChatMessage } from '../types/realtimeChat'
const list = vi.hoisted(() => vi.fn())
vi.mock('../api/listMessages', () => ({ listMessages: list }))
const message = (sequence: number, visibility: 'private' | 'public' = 'private'): ChatMessage => ({ meetingId: 'meeting', messageId: `m${sequence}`, sequence, visibility, recipient: { participantId: 'me', displayName: 'Me' }, author: { participantId: 'bob', displayName: 'Bob', kind: 'human' }, content: 'secret', mentions: [], createdAt: new Date() })
beforeEach(() => { localStorage.clear(); list.mockReset() })
afterEach(cleanup)
function baseline(after = 0) { const state = new MessageNotifications('meeting', 'me', localStorage); state.state.after = after; state.save() }
it('first visit establishes a baseline without relabeling old history as new', async () => {
  const { result } = renderHook(() => useMessageNotifications('meeting', 'me', 'public', true))
  await act(() => result.current.synchronize(50))
  expect(list).not.toHaveBeenCalled()
  expect(new MessageNotifications('meeting', 'me', localStorage).state.after).toBe(50)
})
it('restores offline private hints across multiple pages and persists complete progress', async () => {
  baseline()
  list.mockResolvedValueOnce({ items: Array.from({ length: 100 }, (_, i) => message(i + 1)), hasMoreAfter: true, lastSequence: 100, nextAfterSequence: 100 })
    .mockResolvedValueOnce({ items: [message(101)], hasMoreAfter: false, lastSequence: 101 })
  const { result } = renderHook(() => useMessageNotifications('meeting', 'me', 'public', true))
  await act(() => result.current.synchronize(101))
  expect(list).toHaveBeenNthCalledWith(2, 'meeting', expect.objectContaining({ scope: 'all', afterSequence: 100 }), expect.any(AbortSignal))
  expect(result.current.pending).toEqual({ bob: 101 })
  expect(new MessageNotifications('meeting', 'me', localStorage).state.after).toBe(101)
})
it('failed catchup never skips ahead to a live event and retry recovers the missing private message', async () => {
  baseline(5); list.mockRejectedValueOnce(new Error('network'))
  const { result } = renderHook(() => useMessageNotifications('meeting', 'me', 'public', true))
  act(() => result.current.receive(message(8, 'public')))
  await waitFor(() => expect(result.current.failed).toBe(true))
  expect(new MessageNotifications('meeting', 'me', localStorage).state.after).toBe(5)
  list.mockResolvedValueOnce({ items: [message(6), message(8, 'public')], hasMoreAfter: false })
  act(() => result.current.retry())
  await waitFor(() => expect(result.current.failed).toBe(false))
  expect(result.current.pending).toEqual({ bob: 6 })
})
it('switching scopes clears only its hint and replay does not bring it back', async () => {
  baseline(); list.mockResolvedValue({ items: [message(1)], hasMoreAfter: false })
  const { result, rerender } = renderHook(({ scope }) => useMessageNotifications('meeting', 'me', scope, true), { initialProps: { scope: 'public' } })
  await act(() => result.current.synchronize(1)); expect(result.current.pending.bob).toBe(1)
  rerender({ scope: 'bob' }); expect(result.current.pending.bob).toBeUndefined()
  rerender({ scope: 'public' }); act(() => result.current.receive(message(1)))
  expect(result.current.pending).toEqual({})
})
it('stale async pages cannot update a new meeting and readonly does not fetch', async () => {
  baseline(); let resolve!: (value: unknown) => void
  list.mockReturnValue(new Promise(r => { resolve = r }))
  const { result, rerender } = renderHook(({ id, enabled }) => useMessageNotifications(id, 'me', 'public', enabled), { initialProps: { id: 'meeting', enabled: true } })
  let pending!: Promise<void>; act(() => { pending = result.current.synchronize(1) })
  rerender({ id: 'other', enabled: false })
  await act(async () => { resolve({ items: [message(1)], hasMoreAfter: false }); await pending })
  await act(() => result.current.synchronize(2))
  expect(result.current.pending).toEqual({}); expect(list).toHaveBeenCalledTimes(1)
})
