import { render, screen, waitFor, cleanup } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useState } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { AuthenticationContext } from './context/AuthenticationContext'
import { useAuthentication } from './hooks/useAuthentication'
import { LoginPage } from './pages/LoginPage'
import { LoginForm } from './components/LoginForm'
import { AuthGate } from './components/AuthGate'
import { CurrentUserBar } from './components/CurrentUserBar'
import { AuthenticatedHomePage } from './pages/AuthenticatedHomePage'
import { PersonalSettingsPage } from '@/features/accountSettings/pages/PersonalSettingsPage'
import { AuthenticatedLayout } from '@/app/layouts/AuthenticatedLayout'
import type { AuthenticationContextValue } from './types/authentication'

const user = { userId: 'user-1', username: 'alice', nickname: 'Alice', isAdmin: true }

function value(overrides: Partial<AuthenticationContextValue> = {}): AuthenticationContextValue {
  return {
    state: { status: 'anonymous' },
    login: vi.fn().mockResolvedValue(user),
    logout: vi.fn().mockResolvedValue(undefined),
    retry: vi.fn().mockResolvedValue(undefined),
    identityRevision: 0, refreshRevision: 0, invalidateAdminAccess: vi.fn(), refreshCurrentUser: vi.fn().mockResolvedValue(undefined),
    isLoggingIn: false,
    isLoggingOut: false,
    ...overrides,
  }
}

function withContext(context: AuthenticationContextValue, ui: React.ReactElement) {
  return render(<AuthenticationContext.Provider value={context}>{ui}</AuthenticationContext.Provider>)
}

afterEach(() => cleanup())

describe('authentication pages and components', () => {
  it('renders LoginPage loading without flashing the form during checking', () => {
    withContext(value({ state: { status: 'checking' } }), <MemoryRouter><LoginPage /></MemoryRouter>)
    expect(screen.getByLabelText('正在加载')).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: '用户名' })).not.toBeInTheDocument()
  })

  it('renders LoginPage form for anonymous users', () => {
    withContext(value(), <MemoryRouter><LoginPage /></MemoryRouter>)
    expect(screen.getByRole('heading', { name: '圆桌会议' })).toBeInTheDocument()
    expect(screen.getByRole('textbox', { name: '用户名' })).toBeInTheDocument()
    expect(screen.getByLabelText('密码')).toBeInTheDocument()
  })

  it('renders LoginPage loading state without form while checking', () => {
    withContext(value({ state: { status: 'checking' } }), <MemoryRouter><LoginPage /></MemoryRouter>)
    expect(screen.getByLabelText('正在加载')).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: '用户名' })).not.toBeInTheDocument()
  })

  it('does not submit LoginForm with empty fields and limits input lengths', async () => {
    const submit = vi.fn().mockResolvedValue(undefined)
    const actor = userEvent.setup()
    render(<LoginForm onSubmit={submit} loading={false} />)
    await actor.click(screen.getByRole('button', { name: /登\s*录/ }))
    expect(submit).not.toHaveBeenCalled()
    expect(await screen.findByText('请输入用户名。')).toBeInTheDocument()
    expect(screen.getByLabelText('用户名')).toHaveAttribute('maxlength', '128')
    expect(screen.getByLabelText('密码')).toHaveAttribute('maxlength', '256')
  })

  it('prevents duplicate LoginForm submissions while loading', async () => {
    let updateLoading: ((loading: boolean) => void) | undefined
    const submit = vi.fn().mockImplementation(() => {
      updateLoading?.(true)
      return new Promise(() => undefined)
    })
    const actor = userEvent.setup()
    function Wrapper() {
      const [loading, setLoading] = useState(false)
      updateLoading = setLoading
      return <LoginForm onSubmit={submit} loading={loading} />
    }
    render(<Wrapper />)
    await actor.type(screen.getByLabelText('用户名'), 'alice')
    await actor.type(screen.getByLabelText('密码'), 'secret')
    const button = screen.getByRole('button', { name: /登\s*录/ })
    await actor.click(button)
    await waitFor(() => expect(button).toBeDisabled())
    await actor.click(button)
    expect(submit).toHaveBeenCalledTimes(1)
  })

  it('maps AuthGate checking, anonymous and error states to their boundaries', async () => {
    const checking = withContext(value({ state: { status: 'checking' } }), <MemoryRouter><AuthGate /></MemoryRouter>)
    expect(screen.getByLabelText('正在加载')).toBeInTheDocument()
    checking.unmount()

    const retry = vi.fn().mockResolvedValue(undefined)
    withContext(value({ state: { status: 'error', error: { kind: 'network', message: 'network' } }, retry }), <MemoryRouter initialEntries={['/room?tab=chat#messages']}><Routes><Route element={<AuthGate />}><Route path="*" element={<span>protected</span>} /></Route><Route path="/login" element={<span>login route</span>} /></Routes></MemoryRouter>)
    expect(await screen.findByText('服务暂时不可访问')).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: /重\s*试/ }))
    expect(retry).toHaveBeenCalledOnce()
    cleanup()

    withContext(value(), <MemoryRouter initialEntries={['/room?tab=chat#messages']}><Routes><Route element={<AuthGate />}><Route path="*" element={<span>protected</span>} /></Route><Route path="/login" element={<span>login route</span>} /></Routes></MemoryRouter>)
    expect(await screen.findByText('login route')).toBeInTheDocument()
  })

  it('rejects an external returnTo and falls back to the root path', async () => {
    const context = value({ state: { status: 'authenticated', user } })
    withContext(context, <MemoryRouter initialEntries={[{ pathname: '/login', state: { returnTo: { pathname: 'https://evil.example', search: '', hash: '' } } }]}><Routes><Route path="/login" element={<LoginPage />} /><Route path="/" element={<span>safe root</span>} /></Routes></MemoryRouter>)

    expect(await screen.findByText('safe root')).toBeInTheDocument()
  })

  it('rejects an invalid returnTo search and falls back to the root path', async () => {
    const context = value({ state: { status: 'authenticated', user } })
    withContext(context, <MemoryRouter initialEntries={[{ pathname: '/login', state: { returnTo: { pathname: '/room', search: 'tab=chat', hash: '' } } }]}><Routes><Route path="/login" element={<LoginPage />} /><Route path="/" element={<span>safe root</span>} /></Routes></MemoryRouter>)

    expect(await screen.findByText('safe root')).toBeInTheDocument()
  })

  it('rejects an invalid returnTo hash and falls back to the root path', async () => {
    const context = value({ state: { status: 'authenticated', user } })
    withContext(context, <MemoryRouter initialEntries={[{ pathname: '/login', state: { returnTo: { pathname: '/room', search: '?tab=chat', hash: 'messages' } } }]}><Routes><Route path="/login" element={<LoginPage />} /><Route path="/" element={<span>safe root</span>} /></Routes></MemoryRouter>)

    expect(await screen.findByText('safe root')).toBeInTheDocument()
  })

  it('renders AuthenticatedLayout and Home only with authenticated user data', () => {
    const context = value({ state: { status: 'authenticated', user } })
    withContext(context, <MemoryRouter><Routes><Route element={<AuthenticatedLayout />}><Route path="*" element={<AuthenticatedHomePage />} /></Route></Routes></MemoryRouter>)
    expect(screen.getByRole('link', { name: '圆桌会议' })).toBeInTheDocument()
    expect(screen.getAllByText('Alice')).toHaveLength(2)
    expect(screen.getByText('user-1')).toBeInTheDocument()
    expect(screen.getAllByText('alice')).toHaveLength(1)
  })

  it.each([true, false])('shows both user management entries only for admins (isAdmin=%s)', async isAdmin => {
    const context = value({ state: { status: 'authenticated', user: { ...user, isAdmin } } })
    withContext(context, <MemoryRouter><CurrentUserBar /><PersonalSettingsPage /></MemoryRouter>)
    await userEvent.setup().click(screen.getByRole('button', { name: 'Alice' }))
    await screen.findByRole('link', { name: '个人设置' })
    const entries = screen.queryAllByRole('link', { name: /用户管理/ })
    expect(entries).toHaveLength(isAdmin ? 2 : 0)
    entries.forEach(link => expect(link).toHaveAttribute('href', '/users'))
  })

  it('keeps CurrentUserBar visible on logout failure and disables duplicate logout', async () => {
    const logout = vi.fn().mockRejectedValue({ kind: 'network', message: 'network' })
    const context = value({ state: { status: 'authenticated', user }, logout, isLoggingOut: true, operationError: { kind: 'network', message: 'network' } })
    withContext(context, <MemoryRouter><CurrentUserBar /></MemoryRouter>)
    expect(screen.getByText('Alice')).toBeInTheDocument()
    await userEvent.setup().hover(screen.getByRole('button', { name: /Alice/ }))
    expect(await screen.findByRole('menuitem', { name: '退出' })).toHaveAttribute('aria-disabled', 'true')
    expect(screen.getByRole('alert')).toHaveTextContent('退出失败，请重试。')
  })

  it('triggers the provider logout operation from CurrentUserBar', async () => {
    const logout = vi.fn().mockResolvedValue(undefined)
    const context = value({ state: { status: 'authenticated', user }, logout })
    withContext(context, <MemoryRouter><CurrentUserBar /></MemoryRouter>)
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Alice' }))
    expect(await screen.findByRole('link', { name: '个人设置' })).toHaveAttribute('href', '/settings/profile')
    await userEvent.setup().click(await screen.findByRole('menuitem', { name: '退出' }))
    expect(logout).toHaveBeenCalledOnce()
  })

  it('shows the unsupported media type copy and throws without a provider', () => {
    const context = value({ operationError: { kind: 'problemDetails', code: 'unsupported_media_type', status: 415, message: 'raw' } })
    withContext(context, <MemoryRouter><LoginPage /></MemoryRouter>)
    expect(screen.getByText('请求格式不受支持，请刷新页面后重试。')).toBeInTheDocument()

    function MissingProvider() { useAuthentication(); return null }
    expect(() => render(<MissingProvider />)).toThrow('useAuthentication must be used within AuthenticationProvider')
  })

  it('binds validation field errors and keeps raw server details out of the alert', async () => {
    const error = {
      kind: 'problemDetails' as const,
      status: 400,
      code: 'validation_failed',
      message: 'raw backend validation detail',
      fieldErrors: {
        username: ['用户名字段错误'],
        password: ['密码字段错误'],
      },
    }

    render(<LoginForm onSubmit={vi.fn().mockResolvedValue(undefined)} loading={false} error={error} />)

    expect(await screen.findByText('用户名字段错误')).toBeInTheDocument()
    expect(screen.getByText('密码字段错误')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('请检查输入信息。')
    expect(screen.queryByText('raw backend validation detail')).not.toBeInTheDocument()
  })

  it('clears stale field errors when the error changes or disappears', async () => {
    const validationError = {
      kind: 'problemDetails' as const,
      status: 400,
      code: 'validation_failed',
      message: 'validation',
      fieldErrors: {
        username: ['用户名字段错误'],
        password: ['密码字段错误'],
      },
    }
    const { rerender } = render(<LoginForm onSubmit={vi.fn().mockResolvedValue(undefined)} loading={false} error={validationError} />)

    expect(await screen.findByText('用户名字段错误')).toBeInTheDocument()
    expect(screen.getByText('密码字段错误')).toBeInTheDocument()

    rerender(<LoginForm onSubmit={vi.fn().mockResolvedValue(undefined)} loading={false} error={undefined} />)
    await waitFor(() => {
      expect(screen.queryByText('用户名字段错误')).not.toBeInTheDocument()
      expect(screen.queryByText('密码字段错误')).not.toBeInTheDocument()
    })

    rerender(<LoginForm onSubmit={vi.fn().mockResolvedValue(undefined)} loading={false} error={{ kind: 'problemDetails', status: 401, code: 'invalid_credentials', message: 'invalid' }} />)
    expect(screen.queryByText('用户名字段错误')).not.toBeInTheDocument()
    expect(screen.queryByText('密码字段错误')).not.toBeInTheDocument()
  })
})
