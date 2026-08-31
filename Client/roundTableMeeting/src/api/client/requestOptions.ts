import type { RequestOption } from '@microsoft/kiota-abstractions'

/** 将 AbortSignal 适配到 Kiota 请求选项管道 */
export class AbortRequestOption implements RequestOption {
  /** 用于取消底层 fetch 请求的信号 */
  readonly value: AbortSignal

  constructor(value: AbortSignal) { this.value = value }

  /** 返回请求中间件读取的选项键 */
  getKey() { return 'request-signal' }
}
