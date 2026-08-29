import { describe, expect, it } from 'vitest'
import { normalizeGeneratedContent } from './normalize-generated-output.mjs'

describe('normalizeGeneratedContent', () => {
  it('normalizes localhost and loopback URLs on custom ports', () => {
    expect(normalizeGeneratedContent(
      'roundTableMeetingApiClient.ts',
      'requestAdapter.baseUrl = "http://127.0.0.1:5055";',
    )).toBe('requestAdapter.baseUrl = window.location.origin;')
    expect(normalizeGeneratedContent(
      'kiota-lock.json',
      '{"descriptionUrl":"http://localhost:5055/openapi/v1.json"}',
    )).toBe('{"descriptionUrl":"/openapi/v1.json"}')
  })

  it('rejects development URLs that remain anywhere in generated output', () => {
    expect(() => normalizeGeneratedContent('models.ts', 'const url = "http://localhost:5055/private";')).toThrow(/development absolute URL/)
  })

  it('does not treat Kiota diagnostic log files as contract files', () => {
    expect(normalizeGeneratedContent('.kiota.log', 'Kiota diagnostic output')).toBe('Kiota diagnostic output')
  })
})
