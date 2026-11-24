import { useSyncExternalStore } from 'react'
import { theme as antTheme, type ThemeConfig } from 'antd'

import { appearanceKey as key, colors, palettes, type Colors, type Mode, type ModePreference, type Palette } from './appearanceColors'
export { palettes, type Palette, type Mode, type ModePreference } from './appearanceColors'

const changed = 'roundtable-appearance-changed'
let unsavedValue: string | undefined
function read() {
  if (unsavedValue !== undefined) return unsavedValue
  try { return localStorage.getItem(key) ?? 'paper:system' } catch { return 'paper:system' }
}
function subscribe(onChange: () => void) {
  window.addEventListener('storage', onChange)
  window.addEventListener(changed, onChange)
  return () => {
    window.removeEventListener('storage', onChange)
    window.removeEventListener(changed, onChange)
  }
}
export function saveAppearance(palette: Palette, mode: ModePreference) {
  const value = palette + ':' + mode
  try {
    localStorage.setItem(key, value)
    unsavedValue = undefined
  } catch { unsavedValue = value }
  window.dispatchEvent(new Event(changed))
}
const systemScheme = '(prefers-color-scheme: dark)'
function subscribeSystem(onChange: () => void) {
  const query = window.matchMedia(systemScheme)
  query.addEventListener('change', onChange)
  return () => query.removeEventListener('change', onChange)
}
function systemDark() { return window.matchMedia(systemScheme).matches }

export function useAppearance() {
  const [storedPalette, storedMode] = useSyncExternalStore(subscribe, read).split(':')
  const palette: Palette = Object.hasOwn(palettes, storedPalette) ? storedPalette as Palette : 'paper'
  const preference: ModePreference = storedMode === 'dark' || storedMode === 'light' ? storedMode : 'system'
  const prefersDark = useSyncExternalStore(subscribeSystem, systemDark)
  const mode: Mode = preference === 'system' ? (prefersDark ? 'dark' : 'light') : preference
  return { palette, preference, mode, colors: colors[palette][mode] }
}
export function appearanceTheme(c: Colors, mode: Mode): ThemeConfig {
  return {
    algorithm: mode === 'dark' ? antTheme.darkAlgorithm : antTheme.defaultAlgorithm,
    token: {
      colorPrimary: c.primary, colorPrimaryHover: c.hover, colorInfo: c.accent, colorLink: c.accent,
      colorText: c.text, colorTextSecondary: c.secondary, colorTextPlaceholder: c.muted,
      colorBgBase: c.surface, colorBgContainer: c.surface, colorBgElevated: c.surface, colorBgLayout: c.page,
      colorFillAlter: c.subtle, colorBorder: c.border, colorBorderSecondary: c.border,
      colorSuccess: c.success, colorWarning: c.warning, colorError: c.danger,
      colorTextLightSolid: mode === 'dark' ? c.page : '#ffffff', borderRadius: 8,
      fontFamily: '-apple-system, BlinkMacSystemFont, "PingFang SC", "Microsoft YaHei", sans-serif',
    },
  }
}
