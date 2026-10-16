import { getApiClient } from '@/api/client/apiClient'
import { meetingError } from './helpers'
export async function leaveMeeting(meetingId: string, operationId: string) {
  try { await getApiClient().api.meetings.byMeetingId(meetingId as never).leave.post({ operationId: operationId as never }) }
  catch (error) { throw meetingError(error) }
}
