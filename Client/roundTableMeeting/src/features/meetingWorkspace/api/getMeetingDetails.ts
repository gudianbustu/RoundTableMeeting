import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options } from './helpers'
import type { MeetingDetails } from '../types/meetingWorkspace'
import { mapMeetingDetails } from './mappers'
export async function getMeetingDetails(meetingId: string, signal?: AbortSignal): Promise<MeetingDetails> {
  try {
    const value = await getApiClient().api.meetings.byMeetingId(meetingId as never).get(options(signal))
    if (!value) throw new Error('Invalid meeting details response')
    return mapMeetingDetails(value)
  } catch (error) { throw meetingError(error) }
}
