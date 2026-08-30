import type { RequestOption } from '@microsoft/kiota-abstractions'

export class AbortRequestOption implements RequestOption {
  readonly value: AbortSignal

  constructor(value: AbortSignal) { this.value = value }

  getKey() { return 'request-signal' }
}
