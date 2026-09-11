import type { ApiError } from '@/api/errors/ApiError'

export type UserSummaryViewModel = { userId: string; username: string; nickname: string; isAdmin: boolean }
export type CreateUserInput = { username: string; nickname: string; initialPassword: string }
export type UpdateNicknameInput = { nickname: string }
export type UserManagementLoadState =
  | { status: 'loading' }
  | { status: 'ready'; users: UserSummaryViewModel[] }
  | { status: 'error'; error: ApiError }

export type UserSummaryDto = { userId?: string; username?: string; nickname?: string }
export type CreateUserRequest = { username?: string; nickname?: string; initialPassword?: string }
export type UpdateNicknameRequest = { nickname?: string }

export type UpdateAdminInput = { isAdmin: boolean }
