import type { ApiError } from './ApiError'

type ErrorCandidate = {
  code?: unknown
  detail?: unknown
  traceId?: unknown
  responseStatusCode?: unknown
  errors?: unknown
  responseBody?: unknown
}

/** 检查未知值是否可以作为对象记录读取 */
function isRecord(value: unknown): value is object {
  return value !== null && typeof value === 'object'
}

/** 将 Kiota 或 problem-details 字段错误转换为可预测的记录 */
function normalizeFieldErrors(value: unknown): Record<string, string[]> | undefined {
  if (!isRecord(value)) return undefined
  const holder = value as { additionalData?: unknown }
  const source = isRecord(holder.additionalData) ? holder.additionalData : value
  const result: Record<string, string[]> = {}
  for (const [key, messages] of Object.entries(source)) {
    if (key === 'additionalData') continue
    const values = Array.isArray(messages)
      ? messages.filter((message): message is string => typeof message === 'string' && message.length > 0)
      : typeof messages === 'string' && messages.length > 0
        ? [messages]
        : []
    if (values.length > 0) result[key] = values
  }
  return Object.keys(result).length > 0 ? result : undefined
}

/** 将传输错误、problem-details 错误和未知错误转换为 ApiError */
export function toApiError(error: unknown): ApiError {
  if (error && typeof error === 'object' && 'name' in error && error.name === 'AbortError') throw error
  if (error instanceof TypeError) return { kind: 'network', message: '服务暂时不可访问，请稍后重试。' }

  const candidate = (error && typeof error === 'object' ? error : {}) as ErrorCandidate
  const status = typeof candidate.responseStatusCode === 'number' ? candidate.responseStatusCode : undefined
  if (typeof candidate.code === 'string' && candidate.code.length > 0) {
    return {
      kind: 'problemDetails',
      status,
      code: candidate.code,
      message: typeof candidate.detail === 'string' && candidate.detail.length > 0 ? candidate.detail : '请求未能完成。',
      traceId: typeof candidate.traceId === 'string' ? candidate.traceId : undefined,
      fieldErrors: normalizeFieldErrors(candidate.errors),
    }
  }
  if (typeof candidate.responseBody === 'string' && candidate.responseBody.trim().length > 0) {
    try {
      const body = JSON.parse(candidate.responseBody) as ErrorCandidate
      if (typeof body.code === 'string' && body.code.length > 0) {
        return {
          kind: 'problemDetails',
          status,
          code: body.code,
          message: typeof body.detail === 'string' && body.detail.length > 0 ? body.detail : '请求未能完成。',
          traceId: typeof body.traceId === 'string' ? body.traceId : undefined,
          fieldErrors: normalizeFieldErrors(body.errors),
        }
      }
    } catch {
      return { kind: 'unknown', status, message: '请求未能完成，请稍后重试。' }
    }
  }
  return { kind: 'unknown', status, message: '请求未能完成，请稍后重试。' }
}
