import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { CurrentUserDto } from '@/api/generated/models'
import { toCurrentUserViewModel } from './login'

export async function getCurrentUser(signal?: AbortSignal) {
  try {
    const value = await getApiClient().api.auth.me.get(signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    return toCurrentUserViewModel((value ?? {}) as Partial<CurrentUserDto>)
  } catch (error) {
    throw toApiError(error)
  }
}
