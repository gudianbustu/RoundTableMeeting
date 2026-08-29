import { cpSync, existsSync, renameSync, rmSync } from 'node:fs'
import { randomUUID } from 'node:crypto'
import { dirname, join } from 'node:path'

export function replaceGeneratedDirectory(sourceDirectory, targetDirectory, options = {}) {
  const parentDirectory = dirname(targetDirectory)
  const replacementDirectory = join(parentDirectory, '.generated-replacement-' + randomUUID())
  const backupDirectory = join(parentDirectory, '.generated-backup-' + randomUUID())
  let backedUp = false
  let installed = false

  try {
    cpSync(sourceDirectory, replacementDirectory, { recursive: true })
    if (options.failBeforeInstall) throw new Error('Deterministic generated-directory replacement failure.')
    if (existsSync(targetDirectory)) {
      renameSync(targetDirectory, backupDirectory)
      backedUp = true
    }
    if (options.failAfterBackup) throw new Error('Deterministic post-backup replacement failure.')
    renameSync(replacementDirectory, targetDirectory)
    installed = true
    if (backedUp) rmSync(backupDirectory, { recursive: true, force: true })
  } catch (error) {
    if (installed && existsSync(targetDirectory)) rmSync(targetDirectory, { recursive: true, force: true })
    if (backedUp && existsSync(backupDirectory)) renameSync(backupDirectory, targetDirectory)
    if (existsSync(replacementDirectory)) rmSync(replacementDirectory, { recursive: true, force: true })
    throw error
  }
}
