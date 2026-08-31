import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { UpdateNicknameRequest } from '@/api/generated/models'
import type { UpdateNicknameInput, UserSummaryViewModel } from '../types/userManagement'

export async function updateNickname(userId: string, input: UpdateNicknameInput, signal?: AbortSignal): Promise<UserSummaryViewModel> {
  try {
    const body: UpdateNicknameRequest = { nickname: input.nickname }
    const user = await getApiClient().api.users.byUserId(userId).nickname.patch(body, signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    if (!user?.userId || !user.username || !user.nickname) throw new Error('Invalid user summary response')
    return { userId: user.userId, username: user.username, nickname: user.nickname }
  } catch (error) { throw toApiError(error) }
}
