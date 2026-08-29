import { DefaultRequestAdapter } from '@microsoft/kiota-bundle'
import { AnonymousAuthenticationProvider } from '@microsoft/kiota-abstractions'
import { HttpClient, type Middleware } from '@microsoft/kiota-http-fetchlibrary'
import { createRoundTableMeetingApiClient, type RoundTableMeetingApiClient } from '@/api/generated/roundTableMeetingApiClient'

let client: RoundTableMeetingApiClient | undefined

class RequestOptionsMiddleware implements Middleware {
  next: Middleware | undefined = undefined

  execute(url: string, requestInit: RequestInit, requestOptions?: Record<string, { getKey(): string; value?: AbortSignal }>) {
    const signal = requestOptions?.['request-signal']?.value
    return this.next?.execute(url, { ...requestInit, signal }, requestOptions) ?? Promise.reject(new Error('Kiota request middleware is not configured.'))
  }
}

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
