import { act, renderHook } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { saveAppearance, useAppearance } from './appearance'
import { appearanceKey } from './appearanceColors'

afterEach(() => {
  vi.restoreAllMocks()
  saveAppearance('paper', 'system')
  localStorage.clear()
})

it('restores saved choices and handles changes and removal from another tab', () => {
  localStorage.setItem(appearanceKey, 'sage:dark')
  const { result } = renderHook(useAppearance)
  expect(result.current).toMatchObject({ palette: 'sage', mode: 'dark' })
  act(() => {
    localStorage.setItem(appearanceKey, 'invalid:invalid')
    window.dispatchEvent(new Event('storage'))
  })
  expect(result.current).toMatchObject({ palette: 'paper', preference: 'system', mode: 'light' })
  act(() => saveAppearance('slate', 'dark'))
  act(() => {
    localStorage.removeItem(appearanceKey)
    window.dispatchEvent(new Event('storage'))
  })
  expect(result.current.palette).toBe('paper')
})

it('keeps a new choice for the session when storage contains an older value and writes fail', () => {
  localStorage.setItem(appearanceKey, 'paper:dark')
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('quota') })
  const { result } = renderHook(useAppearance)
  act(() => saveAppearance('sage', 'light'))
  expect(result.current).toMatchObject({ palette: 'sage', mode: 'light' })
})

it('follows changes to the system scheme until a fixed mode is selected', () => {
  const media = Object.assign(new EventTarget(), { matches: false })
  vi.spyOn(window, 'matchMedia').mockReturnValue(media as MediaQueryList)
  const { result } = renderHook(useAppearance)
  act(() => { media.matches = true; media.dispatchEvent(new Event('change')) })
  expect(result.current.mode).toBe('dark')
  act(() => saveAppearance('slate', 'light'))
  expect(result.current.mode).toBe('light')
})
