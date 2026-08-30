import { execFileSync } from 'node:child_process'
import { cpSync, existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { replaceGeneratedDirectory } from './replace-generated-directory.mjs'
import { normalizeGeneratedContent } from './normalize-generated-output.mjs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const repositoryRoot = resolve(root, '..', '..')
const output = join(root, 'src', 'api', 'generated')
const staging = mkdtempSync(join(tmpdir(), 'roundtable-kiota-'))
const url = process.env.OPENAPI_URL || 'http://localhost:5043/openapi/v1.json'
const timeout = Number(process.env.OPENAPI_READY_TIMEOUT_MS || 30000)
const started = Date.now()

try {
  let document
  while (Date.now() - started < timeout) {
    try {
      const response = await fetch(url)
      if (response.ok) {
        document = await response.json()
        break
      }
    } catch {
      // Keep polling while the service starts.
    }
    await new Promise((resolvePromise) => setTimeout(resolvePromise, 250))
  }

  if (!document || typeof document.openapi !== 'string' || !document.openapi.startsWith('3.0') || typeof document.paths !== 'object') {
    throw new Error('OpenAPI endpoint was not ready or returned an invalid 3.0 document: ' + url)
  }

  execFileSync('dotnet', [
    'tool', 'run', 'kiota', 'generate',
    '--language', 'TypeScript',
    '--openapi', url,
    '--output', staging,
    '--class-name', 'RoundTableMeetingApiClient',
    '--clean-output',
    '--additional-data',
  ], { cwd: repositoryRoot, stdio: 'inherit' })

  rmSync(join(staging, '.kiota.log'), { force: true })

  const files = []
  const visit = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const file = join(directory, entry.name)
      if (entry.isDirectory()) visit(file)
      else if (entry.isFile()) files.push(file)
    }
  }
  visit(staging)
  if (!files.some((file) => file.toLowerCase().endsWith('roundtablemeetingapiclient.ts'))) {
    throw new Error('Kiota output did not contain the expected client entry point.')
  }
  for (const file of files) {
    let content = normalizeGeneratedContent(file, readFileSync(file, 'utf8'))
    content = content.replace(/\r?\n/g, '\r\n').replace(/[ \t]+\r?\n/g, '\r\n')
    writeFileSync(file, content, { encoding: 'utf8' })
  }

  replaceGeneratedDirectory(staging, output, {
    failBeforeInstall: process.env.KIOTA_TEST_FAIL_SWAP === '1',
    failAfterBackup: process.env.KIOTA_TEST_FAIL_AFTER_BACKUP === '1',
    failDuringInstall: process.env.KIOTA_TEST_FAIL_DURING_INSTALL === '1',
  })
} catch (error) {
  console.error(error instanceof Error ? error.message : 'Kiota generation failed.')
  process.exitCode = 1
} finally {
  rmSync(staging, { recursive: true, force: true })
}
