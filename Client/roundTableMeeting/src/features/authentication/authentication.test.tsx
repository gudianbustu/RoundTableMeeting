import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup } from '@testing-library/react'
import { StrictMode } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { notifyUnauthorized } from '@/api/client/authenticationEvents'
import type { ApiError } from '@/api/errors/ApiError'
import { AuthenticationContext } from './context/AuthenticationContext'
import { useAuthentication } from './hooks/useAuthentication'
import { AuthenticationProvider } from './providers/AuthenticationProvider'
import { AuthGate } from './components/AuthGate'
import { LoginForm } from './components/LoginForm'
import { LoginPage } from './pages/LoginPage'

const getCurrentUser = vi.hoisted(() => vi.fn())
const login = vi.hoisted(() => vi.fn())
const logout = vi.hoisted(() => vi.fn())

vi.mock('./api/getCurrentUser', () => ({ getCurrentUser }))
vi.mock('./api/login', () => ({ login }))
vi.mock('./api/logout', () => ({ logout }))

const user = { userId: 'user-1', username: 'alice', nickname: 'Alice' }
const invalidCredentials: ApiError = { kind: 'problemDetails', status: 401, code: 'invalid_credentials', message: 'invalid' }
const networkError: ApiError = { kind: 'network', message: 'network' }

function Harness() {
  const authentication = useAuthentication()
  return <><output data-testid="state">{authentication.state.status}|{authentication.isLoggingIn ? 'login' : ''}|{authentication.isLoggingOut ? 'logout' : ''}|{authentication.operationError?.kind ?? ''}</output><button onClick={() => void authentication.login('alice', 'secret').catch(() => undefined)}>start-login</button></>
}

function renderProvider() {
  return render(<AuthenticationProvider><Harness /></AuthenticationProvider>)
}

function renderWithAuthentication(value: React.ComponentProps<typeof AuthenticationContext.Provider>['value'], initialEntries = ['/']) {
  return render(<AuthenticationContext.Provider value={value}><MemoryRouter initialEntries={initialEntries}><Routes><Route path="*" element={<LoginPage />} /></Routes></MemoryRouter></AuthenticationContext.Provider>)
}

describe('authentication provider', () => {
  afterEach(() => {
    cleanup()
  })
  beforeEach(() => {
    getCurrentUser.mockReset()
    login.mockReset()
    logout.mockReset()
  })

  it('starts in checking and reaches authenticated after me succeeds', async () => {
    getCurrentUser.mockResolvedValue(user)
    renderProvider()
    expect(screen.getByTestId('state')).toHaveTextContent('checking')
    expect(await screen.findByTestId('state')).toHaveTextContent('authenticated')
  })

  it('maps me 401 to anonymous and non-auth errors to error', async () => {
    getCurrentUser.mockRejectedValueOnce({ ...invalidCredentials, code: 'unauthenticated' })
    renderProvider()
    await waitFor(() => expect(screen.getAllByTestId('state')[0]).toHaveTextContent('anonymous'))

    cleanup()
    getCurrentUser.mockRejectedValue(networkError)
    renderProvider()
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('error'))
  })

  it('cancels the previous login and resets stale loading state on global 401', async () => {
    getCurrentUser.mockResolvedValue(user)
    let resolveLogin: ((value: typeof user) => void) | undefined
    login.mockReturnValue(new Promise(resolve => { resolveLogin = resolve }))
    renderProvider()
    await waitFor(() => expect(screen.getAllByTestId('state').at(-1)).toHaveTextContent('authenticated'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'start-login' }))
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated|login'))

    act(() => notifyUnauthorized())
    expect(screen.getByTestId('state')).toHaveTextContent('anonymous')
    expect(screen.getByTestId('state')).not.toHaveTextContent('login')
    resolveLogin?.(user)
  })

  it('ignores a late result from an older operation', async () => {
    let resolveFirst: ((value: typeof user) => void) | undefined
    getCurrentUser
      .mockReturnValueOnce(new Promise(resolve => { resolveFirst = resolve }))
    login.mockResolvedValue(user)
    const view = renderProvider()
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('checking'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'start-login' }))
    await waitFor(() => expect(view.container.querySelector('[data-testid="state"]')).toHaveTextContent('authenticated'))
    resolveFirst?.(user)
    await act(async () => { await Promise.resolve() })
    expect(view.container.querySelector('[data-testid="state"]')).toHaveTextContent('authenticated')
  })

  it('resets both loading flags when the provider unmounts', async () => {
    getCurrentUser.mockResolvedValue(user)
    login.mockReturnValue(new Promise(() => undefined))
    const view = renderProvider()
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'start-login' }))
    view.unmount()
    expect(document.body.textContent).not.toContain('login')
  })

  it('keeps authenticated state when logout fails', async () => {
    getCurrentUser.mockResolvedValue(user)
    logout.mockRejectedValue(networkError)
    render(<AuthenticationProvider><LogoutHarness /></AuthenticationProvider>)
    await waitFor(() => expect(screen.getAllByTestId('state').at(-1)).toHaveTextContent('authenticated'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'logout' }))
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated|||network'))
    expect(screen.getByTestId('state')).not.toHaveTextContent('anonymous')
  })

  it('keeps authenticated state when logout is forbidden', async () => {
    getCurrentUser.mockResolvedValue(user)
    logout.mockRejectedValue({ kind: 'problemDetails', status: 403, code: 'origin_not_allowed', message: 'forbidden' })
    render(<AuthenticationProvider><LogoutHarness /></AuthenticationProvider>)
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'logout' }))
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated|||problemDetails'))
  })

  it('moves to anonymous after the logout request completes with 204', async () => {
    getCurrentUser.mockResolvedValue(user)
    logout.mockResolvedValue(undefined)
    render(<AuthenticationProvider><LogoutHarness /></AuthenticationProvider>)
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated'))

    await userEvent.setup().click(screen.getByRole('button', { name: 'logout' }))

    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('anonymous'))
    expect(logout).toHaveBeenCalledOnce()
  })

  it('does not clear anonymous state for invalid credentials', async () => {
    getCurrentUser.mockRejectedValue({ kind: 'problemDetails', status: 401, code: 'unauthenticated', message: 'unauthenticated' })
    login.mockRejectedValue(invalidCredentials)
    render(<AuthenticationProvider><LoginHarness /></AuthenticationProvider>)
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('anonymous'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'start-login' }))
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('anonymous|||problemDetails'))
  })

  it('keeps anonymous state and exposes a safe error when login has a network failure', async () => {
    getCurrentUser.mockRejectedValue({ kind: 'problemDetails', status: 401, code: 'unauthenticated', message: 'unauthenticated' })
    login.mockRejectedValue(networkError)
    render(<AuthenticationProvider><LoginHarness /></AuthenticationProvider>)
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('anonymous'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'start-login' }))
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('anonymous|||network'))
  })

  it('retries an initialization error and accepts the recovered session', async () => {
    getCurrentUser.mockRejectedValueOnce(networkError).mockResolvedValueOnce(user)
    render(<AuthenticationProvider><RetryHarness /></AuthenticationProvider>)
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('error'))
    await userEvent.setup().click(screen.getByRole('button', { name: 'retry' }))
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated'))
  })

  it('cancels the first StrictMode initialization and accepts only the latest response', async () => {
    const signals: AbortSignal[] = []
    let resolveLatest: ((value: typeof user) => void) | undefined
    getCurrentUser.mockImplementation((signal: AbortSignal) => {
      signals.push(signal)
      if (signals.length === 2) return new Promise(resolve => { resolveLatest = resolve })
      return new Promise(() => undefined)
    })

    render(<StrictMode><AuthenticationProvider><Harness /></AuthenticationProvider></StrictMode>)

    await waitFor(() => expect(signals).toHaveLength(2))
    expect(signals[0].aborted).toBe(true)
    resolveLatest?.(user)
    await waitFor(() => expect(screen.getByTestId('state')).toHaveTextContent('authenticated'))
  })
})

function LogoutHarness() {
  const authentication = useAuthentication()
  return <><output data-testid="state">{authentication.state.status}|{authentication.isLoggingIn ? 'login' : ''}|{authentication.isLoggingOut ? 'logout' : ''}|{authentication.operationError?.kind ?? ''}</output><button onClick={() => void authentication.logout().catch(() => undefined)}>logout</button></>
}

function LoginHarness() {
  const authentication = useAuthentication()
  return <><output data-testid="state">{authentication.state.status}|{authentication.isLoggingIn ? 'login' : ''}|{authentication.isLoggingOut ? 'logout' : ''}|{authentication.operationError?.kind ?? ''}</output><button onClick={() => void authentication.login('alice', 'secret').catch(() => undefined)}>start-login</button></>
}

function RetryHarness() {
  const authentication = useAuthentication()
  return <><output data-testid="state">{authentication.state.status}</output><button onClick={() => void authentication.retry()}>retry</button></>
}

describe('authentication views', () => {
  afterEach(() => {
    cleanup()
  })
  it('clears password after invalid credentials', async () => {
    const submit = vi.fn().mockRejectedValue(invalidCredentials)
    render(<LoginForm onSubmit={submit} loading={false} error={invalidCredentials} />)
    const actor = userEvent.setup()
    await actor.type(screen.getByLabelText('用户名'), 'alice')
    await actor.type(screen.getByLabelText('密码'), 'secret')
    await actor.click(screen.getByRole('button', { name: /登\s*录/ }))
    await waitFor(() => expect(screen.getByLabelText('密码')).toHaveValue(''))
    expect(screen.getByLabelText('用户名')).toHaveValue('alice')
  })

  it('renders AuthGate outlet only when authenticated and keeps safe returnTo query/hash', () => {
    const value = { state: { status: 'authenticated' as const, user }, login: vi.fn(), logout: vi.fn(), retry: vi.fn(), isLoggingIn: false, isLoggingOut: false }
    render(<AuthenticationContext.Provider value={value}><MemoryRouter initialEntries={['/room?tab=chat#messages']}><Routes><Route element={<AuthGate />}><Route path="*" element={<span>protected outlet</span>} /></Route></Routes></MemoryRouter></AuthenticationContext.Provider>)
    expect(screen.getByText('protected outlet')).toBeInTheDocument()

    renderWithAuthentication(value, ['/login'])
    expect(document.body.querySelectorAll('form')).toHaveLength(0)
  })

  it('navigates once to a safe returnTo containing query and hash', async () => {
    const value = { state: { status: 'authenticated' as const, user }, login: vi.fn(), logout: vi.fn(), retry: vi.fn(), isLoggingIn: false, isLoggingOut: false }
    render(<AuthenticationContext.Provider value={value}><MemoryRouter initialEntries={[{ pathname: '/login', state: { returnTo: { pathname: '/room', search: '?tab=chat', hash: '#messages' } } }]}><Routes><Route path="/login" element={<LoginPage />} /><Route path="/room" element={<span>room page</span>} /></Routes></MemoryRouter></AuthenticationContext.Provider>)
    expect(await screen.findByText('room page')).toBeInTheDocument()
  })

  it('keeps public login page independent from authentication errors', () => {
    const value = { state: { status: 'error' as const, error: networkError }, login: vi.fn(), logout: vi.fn(), retry: vi.fn(), isLoggingIn: false, isLoggingOut: false }
    renderWithAuthentication(value, ['/login'])
    expect(screen.getByText('服务暂时不可访问')).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: '用户名' })).not.toBeInTheDocument()
  })

  it('navigates AuthGate to login after a business 401 notification', async () => {
    getCurrentUser.mockResolvedValue(user)
    render(<AuthenticationProvider><MemoryRouter initialEntries={['/room']}><Routes><Route element={<AuthGate />}><Route path="/room" element={<span>protected</span>} /></Route><Route path="/login" element={<span>login route</span>} /></Routes></MemoryRouter></AuthenticationProvider>)
    await waitFor(() => expect(screen.getByText('protected')).toBeInTheDocument())
    act(() => notifyUnauthorized())
    expect(await screen.findByText('login route')).toBeInTheDocument()
  })
})
