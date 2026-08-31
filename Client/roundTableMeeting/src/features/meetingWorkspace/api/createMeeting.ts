import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options, guid } from './helpers'
import type { CreateMeetingRequest } from '@/api/generated/models'
import type { CreateMeetingInput, MeetingInvitation, MeetingDetails } from '../types/meetingWorkspace'
import { mapMeetingDetails } from './mappers'
export async function createMeeting(input: CreateMeetingInput, signal?: AbortSignal): Promise<{ meeting: MeetingDetails; invitation: MeetingInvitation }> {
  try { const body: CreateMeetingRequest = { creationRequestId: input.creationRequestId as never, name: input.name, description: input.description }; const value = await getApiClient().api.meetings.post(body, options(signal)); if (!value?.meeting || !value.invitation?.meetingId || !value.invitation.meetingCode) throw new Error('Invalid create response'); return { meeting: mapMeetingDetails(value.meeting), invitation: { meetingId: guid(value.invitation.meetingId), meetingCode: value.invitation.meetingCode } } } catch (error) { throw meetingError(error) }
}
