import type { PropsWithChildren } from 'react'
import { ConfigProvider } from 'antd'
import { theme } from '../theme'
import { AuthenticationProvider } from '@/features/authentication'

export function AppProviders({ children }: PropsWithChildren) {
  return <ConfigProvider theme={theme}><AuthenticationProvider>{children}</AuthenticationProvider></ConfigProvider>
}
