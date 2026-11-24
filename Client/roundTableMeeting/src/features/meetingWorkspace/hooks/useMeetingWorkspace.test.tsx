import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { useMeetingWorkspace } from './useMeetingWorkspace'

const mocks = vi.hoisted(() => ({ list: vi.fn(), details: vi.fn(), join: vi.fn(), preview: vi.fn(), navigate: vi.fn(), location: { pathname: '/meetings/current', state: null } }))
vi.mock('react-router-dom', () => ({ useParams: () => ({ meetingId: mocks.location.pathname.split('/').at(-1) }), useNavigate: () => mocks.navigate, useLocation: () => mocks.location }))
vi.mock('../api/getMeetingDetails', () => ({ getMeetingDetails: mocks.details }))
vi.mock('../api/joinMeeting', () => ({ joinMeeting: mocks.join }))
vi.mock('../api/previewJoinMeeting', () => ({ previewJoinMeeting: mocks.preview }))
vi.mock('../api/listMeetings', () => ({ listMeetings: (...args: unknown[]) => mocks.list(...args) ?? Promise.resolve([]) }))

afterEach(() => { cleanup(); vi.resetAllMocks(); mocks.location.pathname = '/meetings/current' })

it.each([false, true])('reloads the current meeting session after joining, alreadyJoined=%s', async alreadyJoined => {
  const meeting = { meetingId: 'current', name: 'Meeting', canManageInvitation: false, participants: [] }
  mocks.details.mockResolvedValueOnce(meeting)
  mocks.join.mockResolvedValue(meeting)
  mocks.preview.mockResolvedValue({ meetingId: 'current', alreadyJoined })
  const view = renderHook(() => useMeetingWorkspace())
  await waitFor(() => expect(view.result.current.details).toEqual(meeting))
  await act(async () => { await view.result.current.previewCode('code') })
  let complete!: (value: typeof meeting) => void
  mocks.details.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
  let joining!: Promise<void>
  await act(async () => { joining = view.result.current.join('code', 'Bob') })
  await waitFor(() => expect(mocks.details).toHaveBeenCalledTimes(2))
  expect(view.result.current.detailLoading).toBe(true)
  expect(view.result.current.details).toBeUndefined()
  await act(async () => { complete(meeting); await joining })
  expect(view.result.current.detailLoading).toBe(false)
  expect(view.result.current.details).toEqual(meeting)
  expect(mocks.navigate).not.toHaveBeenCalled()
})

it('clears initial loading when a realtime refresh supersedes the first details request', async () => {
  const meeting = { meetingId: 'current', name: 'Meeting', canManageInvitation: false, participants: [] }
  mocks.details.mockImplementationOnce(() => new Promise(() => {})).mockResolvedValueOnce(meeting)
  const view = renderHook(() => useMeetingWorkspace())
  await waitFor(() => expect(view.result.current.detailLoading).toBe(true))
  act(() => view.result.current.refreshDetails())
  await waitFor(() => expect(view.result.current.details).toEqual(meeting))
  expect(view.result.current.detailLoading).toBe(false)
})

it('discards an older join preview even when its request resolves after the next code', async () => {
  mocks.details.mockResolvedValue({ meetingId: 'current', participants: [] })
  let completeOld!: (value: object) => void
  mocks.preview.mockImplementationOnce(() => new Promise(resolve => { completeOld = resolve }))
  mocks.preview.mockResolvedValueOnce({ meetingId: 'new', meetingName: '新会议' })
  const view = renderHook(() => useMeetingWorkspace())
  let oldRequest!: Promise<void>
  await act(async () => { oldRequest = view.result.current.previewCode('old') })
  await act(async () => { await view.result.current.previewCode('new') })
  await act(async () => { completeOld({ meetingId: 'old' }); await oldRequest })
  expect(view.result.current.preview?.meetingId).toBe('new')
  expect(view.result.current.previewLoading).toBe(false)
})

it('discards a preview when navigation closes the dialog', async () => {
  mocks.details.mockResolvedValue({ meetingId: 'current', participants: [] })
  let complete!: (value: object) => void
  mocks.preview.mockImplementationOnce(() => new Promise(resolve => { complete = resolve }))
  const view = renderHook(() => useMeetingWorkspace())
  let pending!: Promise<void>
  await act(async () => { pending = view.result.current.previewCode('old') })
  mocks.location.pathname = '/meetings/other'
  view.rerender()
  await act(async () => { complete({ meetingId: 'old' }); await pending })
  expect(view.result.current.preview).toBeUndefined()
  expect(view.result.current.previewLoading).toBe(false)
})

it('keeps join preview errors separate from background errors', async () => {
  const background = { code: 'server_error', message: '刷新失败' }
  mocks.details.mockRejectedValue(background)
  mocks.preview.mockRejectedValue({ code: 'meeting_not_found', message: '未找到' })
  const view = renderHook(() => useMeetingWorkspace())
  await waitFor(() => expect(view.result.current.error).toEqual(background))
  await act(async () => { await view.result.current.previewCode('unknown') })
  expect(view.result.current.joinError?.code).toBe('meeting_not_found')
  act(() => view.result.current.clearJoin())
  expect(view.result.current.joinError).toBeUndefined()
  expect(view.result.current.error).toEqual(background)
})

it('finishes initial loading on list failure and clears its error after retry', async () => {
  mocks.list.mockRejectedValueOnce({ code: 'server_error', message: '列表加载失败' })
  mocks.details.mockResolvedValue({ meetingId: 'current', participants: [] })
  const view = renderHook(() => useMeetingWorkspace())
  await waitFor(() => expect(view.result.current.listReady).toBe(true))
  expect(view.result.current.listError?.code).toBe('server_error')
  expect(view.result.current.lists).toEqual({ active: [], history: [] })
  act(() => view.result.current.refresh())
  await waitFor(() => expect(view.result.current.listError).toBeUndefined())
})
