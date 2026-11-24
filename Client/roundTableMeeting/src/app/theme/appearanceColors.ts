export const palettes = {
  paper: '暖纸白墨炭黑',
  slate: '石板蓝灰',
  sage: '鼠尾草绿',
} as const
export type Palette = keyof typeof palettes
export type Mode = 'light' | 'dark'
export type ModePreference = Mode | 'system'
export type Colors = {
  page: string; surface: string; subtle: string; primary: string; hover: string; accent: string
  text: string; secondary: string; muted: string; border: string; success: string; warning: string; danger: string
}
export const colors: Record<Palette, Record<Mode, Colors>> = {
  paper: {
    light: {
      page: '#F7F6F2', surface: '#FFFFFF', subtle: '#EFECE4', primary: '#1F2421', hover: '#343B37', accent: '#C46D4E',
      text: '#1F2421', secondary: '#5C635F', muted: '#8F9691', border: '#E6E3DA',
      success: '#3B7A57', warning: '#C98A2E', danger: '#C44536',
    },
    dark: {
      page: '#202320', surface: '#282C28', subtle: '#343831', primary: '#E8E4D8', hover: '#f5f3ed', accent: '#E5A68D',
      text: '#EDECE5', secondary: '#C0C2B9', muted: '#A1A69D', border: '#454A43',
      success: '#83be95', warning: '#dbb370', danger: '#e99889',
    },
  },
  slate: {
    light: {
      page: '#F4F6F8', surface: '#FFFFFF', subtle: '#E9EFF4', primary: '#2C4656', hover: '#3A5A6E', accent: '#4B768F',
      text: '#1E293B', secondary: '#526071', muted: '#8A99A8', border: '#E2E8F0',
      success: '#2F855A', warning: '#B7791F', danger: '#C53030',
    },
    dark: {
      page: '#171d22', surface: '#202830', subtle: '#2b3641', primary: '#bed4e3', hover: '#d8e8f1', accent: '#99bfd8',
      text: '#e5ecf1', secondary: '#becbd5', muted: '#9faebc', border: '#3d4b57',
      success: '#81c09e', warning: '#ddb271', danger: '#e89898',
    },
  },
  sage: {
    light: {
      page: '#F5F7F5', surface: '#FFFFFF', subtle: '#EBF0EC', primary: '#3D5B50', hover: '#4C7063', accent: '#5A8072',
      text: '#1E2B26', secondary: '#566660', muted: '#8A9993', border: '#DFE6E1',
      success: '#3D7A5A', warning: '#C0842D', danger: '#C04A3E',
    },
    dark: {
      page: '#171e1a', surface: '#202923', subtle: '#2c3830', primary: '#bdd6c6', hover: '#d4e6da', accent: '#9bc3ae',
      text: '#e4eee7', secondary: '#bdcec1', muted: '#9eb3a5', border: '#3e5043',
      success: '#88c59e', warning: '#dbb270', danger: '#e99b91',
    },
  },
}

export const appearanceKey = 'roundtable.appearance.v1'
