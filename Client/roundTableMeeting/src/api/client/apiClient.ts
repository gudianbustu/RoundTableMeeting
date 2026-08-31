import { DefaultRequestAdapter } from '@microsoft/kiota-bundle'
import { AnonymousAuthenticationProvider } from '@microsoft/kiota-abstractions'
import { HttpClient, type Middleware } from '@microsoft/kiota-http-fetchlibrary'
import { createRoundTableMeetingApiClient, type RoundTableMeetingApiClient } from '@/api/generated/roundTableMeetingApiClient'
import { notifyUnauthorized } from './authenticationEvents'

let client: RoundTableMeetingApiClient | undefined

/** 为 Kiota 请求添加同源 Cookie、取消信号和未授权通知 */
class RequestOptionsMiddleware implements Middleware {
  /** 指向请求管道中的下一个 Kiota 中间件 */
  next: Middleware | undefined = undefined

  /** 执行请求，并在认证端点之外发布未授权响应 */
  async execute(url: string, requestInit: RequestInit, requestOptions?: Record<string, { getKey(): string; value?: AbortSignal }>) {
    const signal = requestOptions?.['request-signal']?.value
    const response = await (this.next?.execute(url, { ...requestInit, credentials: 'same-origin', signal }, requestOptions) ?? Promise.reject(new Error('Kiota request middleware is not configured.')))
    const pathname = new URL(url, window.location.origin).pathname
    if (response.status === 401 && !pathname.startsWith('/api/auth/')) notifyUnauthorized()
    return response
  }
}

/** 返回延迟初始化的单例 API 客户端 */
export function getApiClient(): RoundTableMeetingApiClient {
  if (!client) {
    const adapter = new DefaultRequestAdapter(
      new AnonymousAuthenticationProvider(),
      undefined,
      undefined,
      new HttpClient((url, init) => fetch(url, init), new RequestOptionsMiddleware()),
    )
    adapter.baseUrl = window.location.origin
    client = createRoundTableMeetingApiClient(adapter)
  }
  return client
}
