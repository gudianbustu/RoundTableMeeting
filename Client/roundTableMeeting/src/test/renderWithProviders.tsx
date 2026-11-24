import { ConfigProvider } from 'antd'
import { MemoryRouter } from 'react-router-dom'
import { render } from '@testing-library/react'
import { appearanceTheme } from '@/app/theme/appearance'
import { colors } from '@/app/theme/appearanceColors'

const theme = appearanceTheme(colors.paper.light, 'light')

/** 使用生产主题和内存路由器渲染组件 */
export function renderWithProviders(ui: React.ReactElement, initialEntries = ['/']) {
  return render(<ConfigProvider theme={theme}><MemoryRouter initialEntries={initialEntries}>{ui}</MemoryRouter></ConfigProvider>)
}
