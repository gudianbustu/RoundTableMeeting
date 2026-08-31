import { getApiClient } from '@/api/client/apiClient'
import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { CurrentUserDto } from '@/api/generated/models'
import type { LoginInput, CurrentUserViewModel } from '../types/authentication'

/** 校验生成的当前用户 DTO 并映射为界面模型 */
export function toCurrentUserViewModel(value: Partial<CurrentUserDto>): CurrentUserViewModel {
  if (!value.userId || !value.username || !value.nickname) throw new Error('Invalid current user response')
  return { userId: value.userId, username: value.username, nickname: value.nickname }
}

/** 发送登录凭据并返回规范化的当前用户 */
export async function login(input: LoginInput, signal?: AbortSignal): Promise<CurrentUserViewModel> {
  try {
    const value = await getApiClient().api.auth.login.post({ username: input.username, password: input.password }, signal ? { options: [new AbortRequestOption(signal)] } : undefined)
    return toCurrentUserViewModel(value ?? {})
  } catch (error) {
    throw toApiError(error)
  }
}
