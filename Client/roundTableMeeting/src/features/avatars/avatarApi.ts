import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import { notifyUnauthorized } from '@/api/client/authenticationEvents'
import { avatarUrl, type AvatarChoice } from './avatar'

function avatarError(cause: unknown) {
  const error = toApiError(cause)
  return new Error([error.message, ...Object.values(error.fieldErrors ?? {}).flat(), error.code].filter(Boolean).join(' '))
}

export async function listAvatars(signal?: AbortSignal): Promise<AvatarChoice[]> {
  const items = await getApiClient().api.avatars.get(signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    .catch(cause => { throw avatarError(cause) })
  if (!Array.isArray(items)) throw new Error('invalid_avatar_catalog')
  return items.map(item => {
    const imageUrl = avatarUrl(item?.imageUrl)
    if (typeof item?.id !== 'string' || !item.id || typeof item.name !== 'string' || !imageUrl) throw new Error('invalid_avatar')
    return { id: item.id, name: item.name, imageUrl, category: typeof item.category === 'string' ? item.category : undefined }
  })
}

async function upload(path: string, file: File, name?: string) {
  const body = new FormData()
  body.append('file', file)
  if (name !== undefined) body.append('name', name)
  const response = await fetch(path, { method: 'POST', body, credentials: 'same-origin' })
  if (response.status === 401) notifyUnauthorized()
  if (!response.ok) throw avatarError({ responseStatusCode: response.status, responseBody: await response.text() })
  return response.json()
}

export const uploadPublicAvatar = (name: string, file: File) => upload('/api/avatars', file, name)
export const uploadPersonalAvatar = (file: File) => upload('/api/account/avatar', file)
export const selectPersonalAvatar = (avatarId: string) => getApiClient().api.account.avatar.put({ avatarId })
  .catch(cause => { throw avatarError(cause) })

export async function bindAgentAvatar(meetingId: string, displayName: string, avatarId: string) {
  const result = await getApiClient().api.meetings.byMeetingId(meetingId as never).agentInvitations.post({ displayName, avatarId })
    .catch(cause => { throw avatarError(cause) })
  if (!result?.invitationKey) throw new Error('invalid_invitation')
  return result.invitationKey
}
