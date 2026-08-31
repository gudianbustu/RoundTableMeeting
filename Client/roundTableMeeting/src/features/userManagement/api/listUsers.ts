import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { UserSummaryDto } from '@/api/generated/models'
import type { UserSummaryViewModel } from '../types/userManagement'

const toUser = (value: Partial<UserSummaryDto>): UserSummaryViewModel => {
  if (!value.userId || !value.username || !value.nickname) throw new Error('Invalid user summary response')
  return { userId: value.userId, username: value.username, nickname: value.nickname }
}

export async function listUsers(signal?: AbortSignal): Promise<UserSummaryViewModel[]> {
  try {
    const values = await getApiClient().api.users.get(signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    return (values ?? []).map((value) => toUser(value ?? {}))
  } catch (error) { throw toApiError(error) }
}
