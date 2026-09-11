import { afterEach, expect, it, vi } from 'vitest'
import { createUuid } from './createUuid'

afterEach(() => vi.unstubAllGlobals())

it('generates distinct UUID v4 request IDs when HTTP does not expose randomUUID', () => {
  const getRandomValues = crypto.getRandomValues.bind(crypto)
  vi.stubGlobal('crypto', { getRandomValues })

  const ids = Array.from({ length: 100 }, () => createUuid())
  expect(new Set(ids).size).toBe(ids.length)
  for (const id of ids) {
    expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  }
})

it('uses the native UUID API when available', () => {
  const id = 'c4e4b668-2e4a-4ea0-bff3-b78ed0f8e228'
  vi.stubGlobal('crypto', { randomUUID: () => id })
  expect(createUuid()).toBe(id)
})
