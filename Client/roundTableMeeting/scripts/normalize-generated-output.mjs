import { basename } from 'node:path'

const developmentBaseUrlPattern = /https?:\/\/(?:localhost|127\.0\.0\.1):\d+/gi
const openApiDocumentUrlPattern = /https?:\/\/(?:localhost|127\.0\.0\.1):\d+\/openapi\/v1\.json/gi

/** 移除 Kiota 输出中的机器相关开发环境 URL */
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
    const lock = JSON.parse(normalized)
    lock.descriptionLocation = '/openapi/v1.json'
    normalized = JSON.stringify(lock, null, 2) + '\n'
  }
  if (developmentBaseUrlPattern.test(normalized)) {
    developmentBaseUrlPattern.lastIndex = 0
    throw new Error('Generated output contains a development absolute URL: ' + file)
  }
  return normalized
}
