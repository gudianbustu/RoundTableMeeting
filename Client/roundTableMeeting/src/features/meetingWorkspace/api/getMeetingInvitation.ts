import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options, guid } from './helpers'
import type { MeetingInvitation } from '../types/meetingWorkspace'
export async function getMeetingInvitation(meetingId: string, signal?: AbortSignal): Promise<MeetingInvitation> {
  try { const value = await getApiClient().api.meetings.byMeetingId(meetingId as never).invitation.post(options(signal)); if (!value?.meetingId || !value.meetingCode) throw new Error('Invalid invitation response'); return { meetingId: guid(value.meetingId), meetingCode: value.meetingCode } } catch (error) { throw meetingError(error) }
}
