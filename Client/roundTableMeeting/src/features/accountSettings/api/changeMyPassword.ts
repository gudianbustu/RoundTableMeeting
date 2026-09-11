import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { ChangeMyPasswordInput } from '../types/accountSettings'

export async function changeMyPassword(input: ChangeMyPasswordInput, signal?: AbortSignal) {
  try {
    await getApiClient().api.account.changePassword.post({ currentPassword: input.currentPassword, newPassword: input.newPassword }, signal ? { options: [new AbortRequestOption(signal)] } : undefined)

  } catch (error) { throw toApiError(error) }
}
