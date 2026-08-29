export type ApiError = {
  kind: 'problemDetails' | 'network' | 'unknown'
  status?: number
  code?: string
  message: string
  traceId?: string
  fieldErrors?: Record<string, string[]>
}
