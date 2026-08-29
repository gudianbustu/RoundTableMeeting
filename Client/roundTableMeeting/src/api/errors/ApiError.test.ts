import { describe, expect, it } from 'vitest'
import { toApiError } from './toApiError'

describe('toApiError', () => {
  it('maps typed problem details', () => {
    expect(toApiError({ code: 'validation_failed', detail: 'Invalid', traceId: 'abc', responseStatusCode: 400 })).toEqual({ kind: 'problemDetails', status: 400, code: 'validation_failed', message: 'Invalid', traceId: 'abc', fieldErrors: undefined })
  })

  it('maps fetch failures without exposing original details', () => {
    expect(toApiError(new TypeError('secret internal url'))).toEqual({ kind: 'network', message: '服务暂时不可访问，请稍后重试。' })
  })

  it('keeps cancellation outside error states', () => {
    expect(() => toApiError(Object.assign(new Error('aborted'), { name: 'AbortError' }))).toThrow('aborted')
  })

  it('normalizes generated additional data field errors', () => {
    expect(toApiError({ code: 'validation_failed', errors: { additionalData: { name: ['Required'] } }, responseStatusCode: 400 })).toMatchObject({ kind: 'problemDetails', status: 400, code: 'validation_failed', fieldErrors: { name: ['Required'] } })
  })

  it('reads the generated errors holder without assuming a Record type', () => {
    const generatedErrors = { additionalData: { name: ['Required'], empty: [''] } }
    expect(toApiError({ code: 'validation_failed', errors: generatedErrors })).toMatchObject({
      fieldErrors: { name: ['Required'] },
    })
  })

  it('normalizes raw and generated-like field error shapes', () => {
    expect(toApiError({ code: 'validation_failed', errors: { name: ['Required'], email: 'Invalid' } })).toMatchObject({
      fieldErrors: { name: ['Required'], email: ['Invalid'] },
    })
    expect(toApiError({ code: 'validation_failed', errors: { additionalData: { name: ['Required'] } } })).toMatchObject({
      fieldErrors: { name: ['Required'] },
    })
  })

  it('keeps status for empty, non-json, and untyped responses', () => {
    expect(toApiError({ responseStatusCode: 502, responseBody: '' })).toMatchObject({ kind: 'unknown', status: 502 })
    expect(toApiError({ responseStatusCode: 502, responseBody: '<html>error</html>' })).toMatchObject({ kind: 'unknown', status: 502 })
    expect(toApiError({ responseStatusCode: 502, responseBody: JSON.stringify({ detail: 'not typed' }) })).toMatchObject({ kind: 'unknown', status: 502 })
  })
})
