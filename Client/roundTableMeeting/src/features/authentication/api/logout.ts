import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'

/** 清除服务端认证 Cookie */
export async function logout(signal?: AbortSignal): Promise<void> {
  try {
    await getApiClient().api.auth.logout.post(signal ? { options: [new AbortRequestOption(signal)] } : undefined)
  } catch (error) {
    throw toApiError(error)
  }
}
