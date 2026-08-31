import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options } from './helpers'
export async function deleteMeeting(meetingId: string, confirmationName: string, signal?: AbortSignal): Promise<void> {
  try { await getApiClient().api.meetings.byMeetingId(meetingId as never).deletion.post({ confirmationName }, options(signal)) } catch (error) { throw meetingError(error) }
}
