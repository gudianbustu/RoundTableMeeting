import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { AuthenticationProvider } from '@/features/authentication/providers/AuthenticationProvider'
import { useAuthentication } from '@/features/authentication'
import { AdminGate } from './AdminGate'
import { AuthGate } from '@/features/authentication/components/AuthGate'
import { useEffect } from 'react'

const getCurrentUser = vi.hoisted(() => vi.fn())
vi.mock('@/features/authentication/api/getCurrentUser', () => ({ getCurrentUser }))
const user = { userId: 'a', username: 'alice', nickname: 'Alice', isAdmin: true }
let auth: ReturnType<typeof useAuthentication>
const mounted = vi.fn(), unmounted = vi.fn()
function Controls() { auth = useAuthentication(); return null }
function Directory() { useEffect(() => { mounted(); return () => { unmounted() } }, []); return <div>private directory</div> }
function setup() {
  return render(<AuthenticationProvider><Controls /><MemoryRouter initialEntries={['/users']}><Routes><Route element={<AuthGate />}><Route element={<AdminGate />}><Route path="/users" element={<Directory />} /></Route></Route><Route path="/login" element={<div>login</div>} /></Routes></MemoryRouter></AuthenticationProvider>)
}
function deferred<T>() { let resolve!: (v: T) => void; const promise = new Promise<T>(r => { resolve = r }); return { promise, resolve } }
beforeEach(() => { getCurrentUser.mockReset(); mounted.mockClear(); unmounted.mockClear() })
afterEach(cleanup)

it('does not mount directory for ordinary user or failed entry refresh', async () => {
  getCurrentUser.mockResolvedValue({ ...user, isAdmin: false }); setup()
  expect(await screen.findByText(/无管理权限/)).toBeInTheDocument(); expect(mounted).not.toHaveBeenCalled()
  cleanup(); getCurrentUser.mockReset().mockResolvedValueOnce(user).mockRejectedValueOnce({ kind: 'network' }); setup()
  expect(await screen.findByText('无法确认管理权限')).toBeInTheDocument(); expect(mounted).not.toHaveBeenCalled()
})
it('keeps mounted directory on nickname refresh and failed ordinary refresh', async () => {
  getCurrentUser.mockResolvedValue(user); setup(); await screen.findByText('private directory')
  const revision = auth.identityRevision
  getCurrentUser.mockResolvedValueOnce({ ...user, nickname: 'Updated' })
  await act(async () => { expect((await auth.refreshCurrentUser()).status).toBe('refreshed') })
  expect(auth.identityRevision).toBe(revision); expect(mounted).toHaveBeenCalledTimes(1); expect(unmounted).not.toHaveBeenCalled()
  getCurrentUser.mockRejectedValueOnce({ kind: 'network' })
  await act(async () => { await auth.refreshCurrentUser().catch(() => undefined) })
  expect(screen.getByText('private directory')).toBeInTheDocument(); expect(unmounted).not.toHaveBeenCalled()
})
it('does not accept entry confirmation superseded by another refresh', async () => {
  const entry = deferred<typeof user>(), newer = deferred<typeof user>()
  getCurrentUser.mockResolvedValueOnce(user).mockReturnValueOnce(entry.promise).mockReturnValueOnce(newer.promise)
  setup(); await waitFor(() => expect(getCurrentUser).toHaveBeenCalledTimes(2))
  let refresh!: ReturnType<typeof auth.refreshCurrentUser>
  act(() => { refresh = auth.refreshCurrentUser() })
  await act(async () => { entry.resolve(user); await Promise.resolve() })
  expect(mounted).not.toHaveBeenCalled()
  await act(async () => { newer.resolve(user); await refresh })
  expect(mounted).not.toHaveBeenCalled(); expect(screen.getByText('无法确认管理权限')).toBeInTheDocument()
})
it('revokes admission on changed role and ignores stale refresh after identity invalidation', async () => {
  getCurrentUser.mockResolvedValue(user); setup(); await screen.findByText('private directory')
  const stale = deferred<typeof user>(); getCurrentUser.mockReturnValueOnce(stale.promise)
  let result!: ReturnType<typeof auth.refreshCurrentUser>
  act(() => { result = auth.refreshCurrentUser() }); act(() => auth.invalidateAdminAccess())
  await act(async () => { stale.resolve(user); expect(await result).toEqual({ status: 'cancelled' }) })
  expect(screen.queryByText('private directory')).not.toBeInTheDocument()
  cleanup(); getCurrentUser.mockResolvedValue(user); setup(); await screen.findByText('private directory')
  getCurrentUser.mockResolvedValueOnce({ ...user, isAdmin: false })
  await act(async () => { await auth.refreshCurrentUser() })
  expect(screen.queryByText('private directory')).not.toBeInTheDocument(); expect(screen.getByText(/无管理权限/)).toBeInTheDocument()
})
