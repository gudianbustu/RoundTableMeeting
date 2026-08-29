import { getApiClient } from '@/api/client/apiClient'
import { toApiError } from '@/api/errors/toApiError'
import type { SystemStatusViewModel } from '../types/systemStatus'
import type { SystemStatusDto } from '@/api/generated/models'

class AbortRequestOption {
  readonly value: AbortSignal
  constructor(value: AbortSignal) { this.value = value }
  getKey() { return 'request-signal' }
}

export function toSystemStatusViewModel(value: Partial<SystemStatusDto>): SystemStatusViewModel {
  if (!value.database?.status || !value.status || !value.environment) throw new Error('Invalid status response')
  return {
    status: value.status as SystemStatusViewModel['status'],
    databaseStatus: value.database.status as SystemStatusViewModel['databaseStatus'],
    environment: value.environment,
  }
}

export async function getSystemStatus(signal?: AbortSignal): Promise<SystemStatusViewModel> {
  try {
    const value = await getApiClient().api.system.status.get({ options: signal ? [new AbortRequestOption(signal)] : undefined })
    return toSystemStatusViewModel(value ?? {})
  } catch (error) {
    throw toApiError(error)
  }
}
