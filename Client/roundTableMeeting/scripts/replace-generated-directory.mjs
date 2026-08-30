import { cpSync, existsSync, readdirSync, rmSync } from 'node:fs'
import { randomUUID } from 'node:crypto'
import { dirname, join } from 'node:path'

export function replaceGeneratedDirectory(sourceDirectory, targetDirectory, options = {}) {
  const parentDirectory = dirname(targetDirectory)
  const replacementDirectory = join(parentDirectory, '.generated-replacement-' + randomUUID())
  const backupDirectory = join(parentDirectory, '.generated-backup-' + randomUUID())
  let backedUp = false
  let installStarted = false
  let installed = false

  try {
    cpSync(sourceDirectory, replacementDirectory, { recursive: true })
    if (options.failBeforeInstall) throw new Error('Deterministic generated-directory replacement failure.')
    if (existsSync(targetDirectory)) {
      cpSync(targetDirectory, backupDirectory, { recursive: true })
      backedUp = true
      rmSync(targetDirectory, { recursive: true, force: true })
    }
    if (options.failAfterBackup) throw new Error('Deterministic post-backup replacement failure.')
    if (options.failDuringInstall) {
      installStarted = true
      const firstEntry = readdirSync(replacementDirectory)[0]
      if (firstEntry) cpSync(join(replacementDirectory, firstEntry), join(targetDirectory, firstEntry), { recursive: true })
      throw new Error('Deterministic installation failure.')
    }
    installStarted = true
    cpSync(replacementDirectory, targetDirectory, { recursive: true })
    rmSync(replacementDirectory, { recursive: true, force: true })
    installed = true
    if (backedUp) rmSync(backupDirectory, { recursive: true, force: true })
  } catch (error) {
    if ((installStarted || installed) && existsSync(targetDirectory)) rmSync(targetDirectory, { recursive: true, force: true })
    if (backedUp && existsSync(backupDirectory)) {
      cpSync(backupDirectory, targetDirectory, { recursive: true })
      rmSync(backupDirectory, { recursive: true, force: true })
    }
    else if (existsSync(backupDirectory)) rmSync(backupDirectory, { recursive: true, force: true })
    if (existsSync(replacementDirectory)) rmSync(replacementDirectory, { recursive: true, force: true })
    throw error
  }
}
