import { describe, expect, it } from 'vitest'
import { getApiClient } from './apiClient'

describe('getApiClient', () => {
  it('returns a module singleton', () => {
    expect(getApiClient()).toBe(getApiClient())
  })
})
