import { describe, expect, it } from 'vitest'
import { toSystemStatusViewModel } from './getSystemStatus'

describe('toSystemStatusViewModel', () => {
  it('converts generated response shape into a feature view model', () => {
    const result = toSystemStatusViewModel({ status: 'degraded', database: { status: 'unavailable' }, environment: 'Testing' })
    expect(result).toEqual({ status: 'degraded', databaseStatus: 'unavailable', environment: 'Testing' })
    expect(result).not.toHaveProperty('database')
  })

  it('rejects incomplete generated responses', () => {
    expect(() => toSystemStatusViewModel({ status: 'available' })).toThrow('Invalid status response')
  })
})
