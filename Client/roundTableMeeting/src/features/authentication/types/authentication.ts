import type { ApiError } from '@/api/errors/ApiError'

/** 登录表单中输入的凭据 */
export type LoginInput = { username: string; password: string }
/** 认证界面组件使用的用户数据结构 */
export type CurrentUserViewModel = { userId: string; username: string; nickname: string }
/** 带判别字段的认证生命周期状态 */
export type AuthenticationState =
  | { status: 'checking' }
  | { status: 'authenticated'; user: CurrentUserViewModel }
  | { status: 'anonymous' }
  | { status: 'error'; error: ApiError }

/** AuthenticationProvider 暴露的状态和操作 */
export type AuthenticationContextValue = {
  state: AuthenticationState
  login: (username: string, password: string) => Promise<CurrentUserViewModel>
  logout: () => Promise<void>
  retry: () => Promise<void>
  isLoggingIn: boolean
  isLoggingOut: boolean
  operationError?: ApiError
}
