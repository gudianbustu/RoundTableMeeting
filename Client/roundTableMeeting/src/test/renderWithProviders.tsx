import { ConfigProvider } from 'antd'
import { MemoryRouter } from 'react-router-dom'
import { render } from '@testing-library/react'
import { theme } from '@/app/theme'

export function renderWithProviders(ui: React.ReactElement, initialEntries = ['/']) {
  return render(<ConfigProvider theme={theme}><MemoryRouter initialEntries={initialEntries}>{ui}</MemoryRouter></ConfigProvider>)
}
