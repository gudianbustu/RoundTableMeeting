import { screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { Route, Routes } from 'react-router-dom'
import { renderWithProviders } from '@/test/renderWithProviders'
import { NotFoundPage } from '@/features/systemStatus'
import { RouteErrorElement } from './RouteErrorElement'

const useRouteErrorMock = vi.hoisted(() => vi.fn())
vi.mock('react-router-dom', async (importOriginal) => ({
  ...(await importOriginal<typeof import('react-router-dom')>()),
  useRouteError: useRouteErrorMock,
}))

describe('client routes', () => {
  it('renders the status route in MemoryRouter', () => {
    renderWithProviders(<Routes><Route path="/system/status" element={<h1>状态页</h1>} /></Routes>, ['/system/status'])
    expect(screen.getByRole('heading', { name: '状态页' })).toBeInTheDocument()
  })

  it('renders an explicit not-found page', () => {
    renderWithProviders(<NotFoundPage />, ['/missing'])
    expect(screen.getByRole('heading', { name: '页面不存在' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '返回系统状态' })).toHaveAttribute('href', '/system/status')
  })

  it('renders a safe route error boundary', () => {
    useRouteErrorMock.mockReturnValue(new Error('secret stack'))
    renderWithProviders(<RouteErrorElement />)
    expect(screen.getByRole('alert')).toHaveTextContent('页面发生了意外错误。')
    expect(screen.queryByText('secret stack')).not.toBeInTheDocument()
  })
})
