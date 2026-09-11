import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { UpdateAdminRequest } from '@/api/generated/models'
import type { UpdateAdminInput, UserSummaryViewModel } from '../types/userManagement'

export async function updateAdmin(userId: string, input: UpdateAdminInput, signal?: AbortSignal): Promise<UserSummaryViewModel> {
  try {
    const body: UpdateAdminRequest = { isAdmin: input.isAdmin }
    const user = await getApiClient().api.users.byUserId(userId).admin.patch(body, signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    if (!user?.userId || !user.username || !user.nickname || typeof user.isAdmin !== 'boolean') throw new Error('Invalid user summary response')
    return { userId: user.userId, username: user.username, nickname: user.nickname, isAdmin: user.isAdmin }
  } catch (error) { throw toApiError(error) }
}
