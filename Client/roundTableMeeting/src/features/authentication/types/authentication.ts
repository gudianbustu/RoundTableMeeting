import type { ApiError } from '@/api/errors/ApiError'

export type LoginInput = { username: string; password: string }
export type CurrentUserViewModel = { userId: string; username: string; nickname: string }
export type AuthenticationState =
  | { status: 'checking' }
  | { status: 'authenticated'; user: CurrentUserViewModel }
  | { status: 'anonymous' }
  | { status: 'error'; error: ApiError }

export type AuthenticationContextValue = {
  state: AuthenticationState
  login: (username: string, password: string) => Promise<CurrentUserViewModel>
  logout: () => Promise<void>
  retry: () => Promise<void>
  isLoggingIn: boolean
  isLoggingOut: boolean
  operationError?: ApiError
}
