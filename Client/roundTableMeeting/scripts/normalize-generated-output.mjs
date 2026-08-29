import { basename } from 'node:path'

const developmentBaseUrlPattern = /https?:\/\/(?:localhost|127\.0\.0\.1):\d+/gi
const openApiDocumentUrlPattern = /https?:\/\/(?:localhost|127\.0\.0\.1):\d+\/openapi\/v1\.json/gi

export function normalizeGeneratedContent(file, content) {
  let normalized = content
  const name = basename(file).toLowerCase()
  if (name === 'roundtablemeetingapiclient.ts') {
    normalized = normalized.replace(
      /requestAdapter\.baseUrl\s*=\s*[\"']https?:\/\/(?:localhost|127\.0\.0\.1):\d+[\"'];/gi,
      'requestAdapter.baseUrl = window.location.origin;',
    )
  }
  if (name === 'kiota-lock.json') {
    normalized = normalized.replace(openApiDocumentUrlPattern, '/openapi/v1.json')
  }
  if (developmentBaseUrlPattern.test(normalized)) {
    developmentBaseUrlPattern.lastIndex = 0
    throw new Error('Generated output contains a development absolute URL: ' + file)
  }
  return normalized
}
