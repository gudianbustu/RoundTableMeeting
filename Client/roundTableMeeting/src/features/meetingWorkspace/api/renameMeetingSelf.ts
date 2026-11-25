import { getApiClient } from '@/api/client/apiClient'
import { meetingError } from './helpers'

export async function renameMeetingSelf(meetingId: string, displayName: string) {
  try {
    await getApiClient().api.meetings.byMeetingId(meetingId as never).participants.me.displayName.put({ displayName })
  } catch (error) { throw meetingError(error) }
}
