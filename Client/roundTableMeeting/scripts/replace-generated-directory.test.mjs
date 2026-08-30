import { describe, expect, it } from 'vitest'
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { replaceGeneratedDirectory } from './replace-generated-directory.mjs'

describe('replaceGeneratedDirectory', () => {
  it('restores the previous directory when installation fails', () => {
    const root = mkdtempSync(join(tmpdir(), 'roundtable-generated-test-'))
    const source = join(root, 'source')
    const target = join(root, 'target')
    const sourceFile = join(source, 'client.ts')
    const targetFile = join(target, 'client.ts')
    mkdirSync(source, { recursive: true })
    mkdirSync(target, { recursive: true })
    writeFileSync(sourceFile, 'new')
    writeFileSync(targetFile, 'old')

    expect(() => replaceGeneratedDirectory(source, target, { failBeforeInstall: true })).toThrow()
    expect(readFileSync(targetFile, 'utf8')).toBe('old')
    expect(readdirSync(root).filter((name) => name.startsWith('.generated-'))).toHaveLength(0)
    rmSync(root, { recursive: true, force: true })
  })

  it('restores the previous directory after the backup has been moved', () => {
    const root = mkdtempSync(join(tmpdir(), 'roundtable-generated-test-'))
    const source = join(root, 'source')
    const target = join(root, 'target')
    mkdirSync(source, { recursive: true })
    mkdirSync(target, { recursive: true })
    writeFileSync(join(source, 'client.ts'), 'new')
    writeFileSync(join(target, 'client.ts'), 'old')

    expect(() => replaceGeneratedDirectory(source, target, { failAfterBackup: true })).toThrow()
    expect(readFileSync(join(target, 'client.ts'), 'utf8')).toBe('old')
    expect(readdirSync(root).filter((name) => name.startsWith('.generated-'))).toHaveLength(0)
    rmSync(root, { recursive: true, force: true })
  })

  it('restores the complete directory when installation fails after partial copy', () => {
    const root = mkdtempSync(join(tmpdir(), 'roundtable-generated-test-'))
    const source = join(root, 'source')
    const target = join(root, 'target')
    mkdirSync(source, { recursive: true })
    mkdirSync(target, { recursive: true })
    writeFileSync(join(source, 'client.ts'), 'new')
    writeFileSync(join(source, 'extra.ts'), 'extra')
    writeFileSync(join(target, 'client.ts'), 'old')
    writeFileSync(join(target, 'legacy.ts'), 'legacy')

    expect(() => replaceGeneratedDirectory(source, target, { failDuringInstall: true })).toThrow()
    expect(readFileSync(join(target, 'client.ts'), 'utf8')).toBe('old')
    expect(readFileSync(join(target, 'legacy.ts'), 'utf8')).toBe('legacy')
    expect(readdirSync(root).filter((name) => name.startsWith('.generated-'))).toHaveLength(0)
    rmSync(root, { recursive: true, force: true })
  })

  it('removes a partial target when installation fails without an existing backup', () => {
    const root = mkdtempSync(join(tmpdir(), 'roundtable-generated-test-'))
    const source = join(root, 'source')
    const target = join(root, 'target')
    mkdirSync(source, { recursive: true })
    writeFileSync(join(source, 'client.ts'), 'new')

    expect(() => replaceGeneratedDirectory(source, target, { failDuringInstall: true })).toThrow()
    expect(readdirSync(root).filter((name) => name.startsWith('.generated-'))).toHaveLength(0)
    expect(readdirSync(root).includes('target')).toBe(false)
    rmSync(root, { recursive: true, force: true })
  })
})
