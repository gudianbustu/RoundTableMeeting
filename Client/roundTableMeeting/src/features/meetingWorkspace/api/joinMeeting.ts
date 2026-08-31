import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options } from './helpers'
import type { JoinMeetingRequest } from '@/api/generated/models'
import type { MeetingDetails } from '../types/meetingWorkspace'
import { mapMeetingDetails } from './mappers'
export async function joinMeeting(meetingCode: string, displayName: string, signal?: AbortSignal): Promise<MeetingDetails> {
  try { const value = await getApiClient().api.meetings.join.post({ meetingCode, displayName } as JoinMeetingRequest, options(signal)); if (!value) throw new Error('Invalid join response'); return mapMeetingDetails(value) } catch (error) { throw meetingError(error) }
}
