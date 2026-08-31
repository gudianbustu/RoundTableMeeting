import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from '@/test/renderWithProviders'
import { AuthenticationContext } from '@/features/authentication/context/AuthenticationContext'
import { SystemStatusPage } from './SystemStatusPage'

const getSystemStatus = vi.hoisted(() => vi.fn())

vi.mock('../api/getSystemStatus', () => ({ getSystemStatus }))

describe('SystemStatusPage', () => {
  beforeEach(() => {
    getSystemStatus.mockReset()
  })

  afterEach(() => {
    cleanup()
  })

  it('shows loading before the API resolves', () => {
    getSystemStatus.mockReturnValue(new Promise(() => undefined))
    renderWithProviders(<SystemStatusPage />)
    expect(screen.getByLabelText('正在加载')).toBeInTheDocument()
    expect(screen.queryByText('框架运行状态')).not.toBeInTheDocument()
  })

  it('shows the available status contract', async () => {
    getSystemStatus.mockResolvedValue({ status: 'available', databaseStatus: 'available', environment: 'Testing' })
    renderWithProviders(<SystemStatusPage />)
    expect(await screen.findByText('框架运行状态')).toBeInTheDocument()
    expect(screen.getByRole('row', { name: /服务端.*可访问/ })).toBeInTheDocument()
    expect(screen.getByRole('row', { name: /数据库.*可访问/ })).toBeInTheDocument()
    expect(screen.getByText('Testing')).toBeInTheDocument()
    expect(screen.getByText('已加载')).toBeInTheDocument()
  })

  it('renders a long environment name without truncating its content', async () => {
    const environment = 'Production-Region-East-LongEnvironmentName-Without-Spaces'
    getSystemStatus.mockResolvedValue({ status: 'available', databaseStatus: 'available', environment })

    renderWithProviders(<SystemStatusPage />)

    expect(await screen.findByText(environment)).toHaveTextContent(environment)
  })

  it('shows degraded database state separately from network failure', async () => {
    getSystemStatus.mockResolvedValue({ status: 'degraded', databaseStatus: 'unavailable', environment: 'Testing' })
    renderWithProviders(<SystemStatusPage />)
    expect(await screen.findByText('服务端已连接，但数据库处于不可用状态。')).toBeInTheDocument()
    expect(screen.getByRole('row', { name: /服务端.*降级/ })).toBeInTheDocument()
    expect(screen.getByRole('row', { name: /数据库.*不可用/ })).toBeInTheDocument()
    expect(screen.queryByText('服务暂时不可访问')).not.toBeInTheDocument()
  })

  it('shows a safe network error and retries once', async () => {
    getSystemStatus.mockRejectedValueOnce({ kind: 'network', message: '服务暂时不可访问，请稍后重试。' })
      .mockResolvedValueOnce({ status: 'available', databaseStatus: 'available', environment: 'Testing' })
    const user = userEvent.setup()
    renderWithProviders(<SystemStatusPage />)
    expect(await screen.findByText('服务暂时不可访问')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /重\s*试/ }))
    await waitFor(() => expect(screen.getByText('框架运行状态')).toBeInTheDocument())
    expect(getSystemStatus).toHaveBeenCalledTimes(2)
  })

  it('shows safe ProblemDetails copy and trace id without raw details', async () => {
    getSystemStatus.mockRejectedValue({ kind: 'problemDetails', code: 'internal_error', status: 500, message: 'secret raw json', traceId: 'trace-123' })
    renderWithProviders(<SystemStatusPage />)
    expect(await screen.findByText('服务端发生错误')).toBeInTheDocument()
    expect(screen.getByText('服务端发生错误').parentElement).toHaveTextContent('关联标识：trace-123')
    expect(screen.queryByText('secret raw json')).not.toBeInTheDocument()
  })

  it('uses generic copy for unknown errors and status-aware ProblemDetails copy', async () => {
    getSystemStatus.mockRejectedValueOnce({ kind: 'problemDetails', code: 'new_code', status: 404, message: 'secret' })
    const first = renderWithProviders(<SystemStatusPage />)
    expect(await screen.findByText('请求的资源不存在')).toBeInTheDocument()
    first.unmount()

    getSystemStatus.mockRejectedValueOnce({ kind: 'unknown', status: 502, message: 'raw details' })
    renderWithProviders(<SystemStatusPage />)
    expect(await screen.findByText('请求未能完成')).toBeInTheDocument()
    expect(screen.queryByText('raw details')).not.toBeInTheDocument()
  })

  it('does not enter an error state for an AbortError', async () => {
    getSystemStatus.mockRejectedValue(Object.assign(new Error('aborted'), { name: 'AbortError' }))
    renderWithProviders(<SystemStatusPage />)
    await act(async () => { await Promise.resolve() })
    expect(screen.getByLabelText('正在加载')).toBeInTheDocument()
    expect(screen.queryByText('服务暂时不可访问')).not.toBeInTheDocument()
  })

  it('does not update after an aborted request', async () => {
    let resolveRequest: ((value: { status: 'available'; databaseStatus: 'available'; environment: string }) => void) | undefined
    getSystemStatus.mockReturnValue(new Promise((resolve) => { resolveRequest = resolve }))
    const view = renderWithProviders(<SystemStatusPage />)
    view.unmount()
    await act(async () => { resolveRequest?.({ status: 'available', databaseStatus: 'available', environment: 'Testing' }) })
    expect(document.body.textContent).not.toContain('框架运行状态')
  })

  it('renders independently when authentication initialization is in error', async () => {
    getSystemStatus.mockResolvedValue({ status: 'available', databaseStatus: 'available', environment: 'Testing' })
    const authenticationValue = {
      state: { status: 'error' as const, error: { kind: 'network' as const, message: 'network' } },
      login: vi.fn(), logout: vi.fn(), retry: vi.fn(), refreshCurrentUser: vi.fn(), isLoggingIn: false, isLoggingOut: false,
    }

    render(<AuthenticationContext.Provider value={authenticationValue}><SystemStatusPage /></AuthenticationContext.Provider>)

    expect(await screen.findByText('框架运行状态')).toBeInTheDocument()
  })
})
