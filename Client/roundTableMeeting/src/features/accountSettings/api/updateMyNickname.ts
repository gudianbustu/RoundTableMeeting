import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import { toCurrentUserViewModel } from '@/features/authentication/api/login'
import type { UpdateMyNicknameInput } from '../types/accountSettings'

export async function updateMyNickname(input: UpdateMyNicknameInput, signal?: AbortSignal) {
  try {
    const user = await getApiClient().api.account.nickname.patch({ nickname: input.nickname }, signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    return toCurrentUserViewModel(user ?? {})
  } catch (error) { throw toApiError(error) }
}
