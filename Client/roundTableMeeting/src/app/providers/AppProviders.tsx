import type { PropsWithChildren } from 'react'
import { ConfigProvider } from 'antd'
import { theme } from '../theme'

export function AppProviders({ children }: PropsWithChildren) {
  return <ConfigProvider theme={theme}>{children}</ConfigProvider>
}
