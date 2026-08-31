/** API 适配器与界面视图共用的规范化错误契约 */
export type ApiError = {
  kind: 'problemDetails' | 'network' | 'unknown'
  status?: number
  code?: string
  message: string
  traceId?: string
  fieldErrors?: Record<string, string[]>
}
