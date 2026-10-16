import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { copyText } from './copyText'

beforeEach(() => { Object.defineProperty(navigator, 'clipboard', { configurable: true, get: () => undefined }) })
afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = '' })
it('uses the modern API when available', async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText } as unknown as Clipboard)
  await copyText('中文\nline 2')
  expect(writeText).toHaveBeenCalledWith('中文\nline 2')
  expect(document.querySelector('textarea')).toBeNull()
})
it('copies synchronously without Clipboard API and restores modal focus and selection', async () => {
  vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue(undefined as unknown as Clipboard)
  document.body.innerHTML = '<div role="dialog"><input value="draft text"></div>'
  const input = document.querySelector('input')!
  input.focus(); input.setSelectionRange(1, 4)
  const exec = vi.fn(() => {
    const field = document.activeElement as HTMLTextAreaElement
    expect(field.closest('[role="dialog"]')).not.toBeNull()
    expect(field.value).toBe('完整提示词\n中文 😀')
    expect(field.selectionEnd).toBe(field.value.length)
    return true
  })
  Object.defineProperty(document, 'execCommand', { configurable: true, value: exec })
  const result = copyText('完整提示词\n中文 😀')
  expect(exec).toHaveBeenCalledWith('copy')
  await result
  expect(document.activeElement).toBe(input)
  expect(input.selectionStart).toBe(1)
  expect(input.selectionEnd).toBe(4)
  expect(document.querySelector('textarea')).toBeNull()
})
it('falls back if the modern API rejects', async () => {
  vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue({ writeText: vi.fn().mockRejectedValue(new Error('denied')) } as unknown as Clipboard)
  const exec = vi.fn(() => true)
  Object.defineProperty(document, 'execCommand', { configurable: true, value: exec })
  await copyText('code')
  expect(exec).toHaveBeenCalledWith('copy')
})
it.each([false, 'throw'])('rejects unsuccessful fallback (%s) and removes temporary content', async outcome => {
  vi.spyOn(navigator, 'clipboard', 'get').mockReturnValue(undefined as unknown as Clipboard)
  Object.defineProperty(document, 'execCommand', { configurable: true, value: () => { if (outcome === 'throw') throw new Error('blocked'); return false } })
  await expect(copyText('private value')).rejects.toThrow()
  expect(document.querySelector('textarea')).toBeNull()
})
