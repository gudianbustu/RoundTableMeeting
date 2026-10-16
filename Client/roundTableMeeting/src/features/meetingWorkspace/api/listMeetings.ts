import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options, guid } from './helpers'
import type { MeetingListItemDto } from '@/api/generated/models'
import type { MeetingListItem } from '../types/meetingWorkspace'
export async function listMeetings(signal?: AbortSignal, view: 'active' | 'history' = 'active'): Promise<MeetingListItem[]> {
  try {
    const values = await getApiClient().api.meetings.get({ ...options(signal), queryParameters: { view } })
    return (values ?? []).map((value: Partial<MeetingListItemDto>) => {
      if (!value.meetingId || !value.name || !value.myDisplayName) throw new Error('Invalid meeting list response')
      return { meetingId: guid(value.meetingId), name: value.name, myDisplayName: value.myDisplayName, isCreator: Boolean(value.isCreator), joinedAt: value.joinedAt ?? undefined }
    })
  } catch (error) { throw meetingError(error) }
}
