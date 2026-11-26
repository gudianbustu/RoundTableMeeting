import { act, renderHook } from '@testing-library/react'
import { afterEach, expect, it } from 'vitest'
import { useMeetingOrder } from './useMeetingOrder'

const meetings = ['a', 'b', 'c'].map(meetingId => ({ meetingId, name: meetingId, myDisplayName: '', isCreator: false }))
afterEach(() => localStorage.clear())

it('persists order for one account and filters removed or newly joined meetings by ID', () => {
  const { result, rerender, unmount } = renderHook(({ owner, rows }) => useMeetingOrder(rows, owner), {
    initialProps: { owner: 'one', rows: meetings },
  })
  act(() => result.current.move('c', 'a'))
  expect(result.current.ordered.map(m => m.meetingId)).toEqual(['c', 'a', 'b'])
  rerender({ owner: 'two', rows: meetings })
  expect(result.current.ordered).toEqual(meetings)
  unmount()
  const rows = [meetings[0], meetings[2], { ...meetings[1], meetingId: 'new' }]
  const reopened = renderHook(() => useMeetingOrder(rows, 'one'))
  expect(reopened.result.current.ordered.map(m => m.meetingId)).toEqual(['c', 'a', 'new'])
})
