import { ConfigProvider } from 'antd'
import { MemoryRouter } from 'react-router-dom'
import { render } from '@testing-library/react'
import { theme } from '@/app/theme'

/** 使用生产主题和内存路由器渲染组件 */
export function renderWithProviders(ui: React.ReactElement, initialEntries = ['/']) {
  return render(<ConfigProvider theme={theme}><MemoryRouter initialEntries={initialEntries}>{ui}</MemoryRouter></ConfigProvider>)
}
