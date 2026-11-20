import { getApiClient } from '@/api/client/apiClient'
import { meetingError } from './helpers'

export async function updateMeetingHistory(id: string, action: 'rejoin' | 'hide') {
  const meeting = getApiClient().api.meetings.byMeetingId(id as never)
  try {
    if (action === 'rejoin') await meeting.rejoin.post()
    else await meeting.history.delete()
  } catch (error) { throw meetingError(error) }
}
