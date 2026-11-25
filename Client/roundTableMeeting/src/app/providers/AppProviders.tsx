import { useLayoutEffect, type PropsWithChildren } from 'react'
import { ConfigProvider } from 'antd'
import { appearanceTheme, useAppearance } from '../theme/appearance'
import { AuthenticationProvider } from '@/features/authentication'

export function AppProviders({ children }: PropsWithChildren) {
  const { colors, mode } = useAppearance()
  useLayoutEffect(() => {
    const root = document.documentElement
    root.dataset.appearanceMode = mode
    root.style.colorScheme = mode
    for (const [name, value] of Object.entries(colors)) root.style.setProperty('--rt-' + name, value)
  }, [colors, mode])
  return <ConfigProvider theme={appearanceTheme(colors, mode)}><AuthenticationProvider>{children}</AuthenticationProvider></ConfigProvider>
}
