import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'

export async function resetPassword(userId: string, signal?: AbortSignal): Promise<void> {
  try {
    await getApiClient().api.users.byUserId(userId).resetPassword.post(signal ? { options: [new AbortRequestOption(signal)] } : undefined)
  } catch (error) { throw toApiError(error) }
}
