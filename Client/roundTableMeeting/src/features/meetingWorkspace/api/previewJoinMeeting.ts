import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options, guid } from './helpers'
import type { PreviewJoinRequest } from '@/api/generated/models'
import type { JoinPreview } from '../types/meetingWorkspace'
export async function previewJoinMeeting(meetingCode: string, signal?: AbortSignal): Promise<JoinPreview> {
  try { const body: PreviewJoinRequest = { meetingCode }; const value = await getApiClient().api.meetings.join.preview.post(body, options(signal)); if (!value?.meetingId || !value.meetingName || !value.defaultDisplayName) throw new Error('Invalid join preview response'); return { meetingId: guid(value.meetingId), meetingName: value.meetingName, description: value.description ?? undefined, defaultDisplayName: value.defaultDisplayName, alreadyJoined: Boolean(value.alreadyJoined), existingDisplayName: value.existingDisplayName ?? undefined } } catch (error) { throw meetingError(error) }
}
