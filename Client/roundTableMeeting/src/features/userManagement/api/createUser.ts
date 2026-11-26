import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { CreateUserRequest, UserSummaryDto } from '@/api/generated/models'
import type { CreateUserInput, UserSummaryViewModel } from '../types/userManagement'

const toUser = (value: Partial<UserSummaryDto>): UserSummaryViewModel => {
  if (!value.userId || !value.username || !value.nickname || typeof value.isAdmin !== 'boolean') throw new Error('Invalid user summary response')
  return { userId: value.userId, username: value.username, nickname: value.nickname, isAdmin: value.isAdmin }
}

export async function createUser(input: CreateUserInput, signal?: AbortSignal): Promise<UserSummaryViewModel> {
  try {
    const body: CreateUserRequest = { username: input.username, nickname: input.nickname }
    const value = await getApiClient().api.users.post(body, signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    return toUser(value ?? {})
  } catch (error) { throw toApiError(error) }
}
