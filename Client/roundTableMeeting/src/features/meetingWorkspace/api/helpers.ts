import { AbortRequestOption } from '@/api/client/requestOptions'
import { toApiError } from '@/api/errors/toApiError'
import type { MeetingWorkspaceApiError } from '../types/meetingWorkspace'

export const options = (signal?: AbortSignal) => signal ? { options: [new AbortRequestOption(signal)] } : undefined
export function guid(value: unknown): string {
  if (typeof value === 'string') return value
  if (value && typeof value === 'object' && 'toString' in value) return String(value)
  throw new Error('Invalid meeting identifier')
}
export function meetingError(error: unknown): MeetingWorkspaceApiError {
  const result = toApiError(error) as MeetingWorkspaceApiError
  const headers = (error && typeof error === 'object' && 'responseHeaders' in error) ? (error as { responseHeaders?: Record<string, string> }).responseHeaders : undefined
  const retry = headers?.['Retry-After'] ?? headers?.['retry-after']
  if (retry) {
    const seconds = Number(retry)
    if (Number.isFinite(seconds) && seconds >= 0) result.retryAfterSeconds = Math.ceil(seconds)
    else {
      const date = Date.parse(retry)
      if (Number.isFinite(date)) result.retryAfterSeconds = Math.max(0, Math.ceil((date - Date.now()) / 1000))
    }
  }
  return result
}
