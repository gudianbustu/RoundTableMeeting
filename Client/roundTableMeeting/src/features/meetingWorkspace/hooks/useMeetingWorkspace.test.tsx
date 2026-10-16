import { act, cleanup, renderHook, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { useMeetingWorkspace } from './useMeetingWorkspace'

const mocks = vi.hoisted(() => ({ details: vi.fn(), join: vi.fn(), preview: vi.fn(), navigate: vi.fn(), location: { pathname: '/meetings/current', state: null } }))
vi.mock('react-router-dom', () => ({ useParams: () => ({ meetingId: 'current' }), useNavigate: () => mocks.navigate, useLocation: () => mocks.location }))
vi.mock('../api/getMeetingDetails', () => ({ getMeetingDetails: mocks.details }))
vi.mock('../api/joinMeeting', () => ({ joinMeeting: mocks.join }))
vi.mock('../api/previewJoinMeeting', () => ({ previewJoinMeeting: mocks.preview }))
vi.mock('../api/listMeetings', () => ({ listMeetings: async () => [] }))

afterEach(() => { cleanup(); vi.resetAllMocks() })

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
