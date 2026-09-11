import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { AuthenticationContext } from '@/features/authentication/context/AuthenticationContext'
import { UserManagementPage } from './pages/UserManagementPage'
const listUsers = vi.hoisted(() => vi.fn())
const updateAdmin = vi.hoisted(() => vi.fn())
const createUser = vi.hoisted(() => vi.fn())
vi.mock('./api/listUsers', () => ({ listUsers }))
vi.mock('./api/updateAdmin', () => ({ updateAdmin }))
vi.mock('./api/createUser', () => ({ createUser }))
const user = { userId: 'a', username: 'alice', nickname: 'Alice', isAdmin: true }
const refreshCurrentUser = vi.fn(), invalidateAdminAccess = vi.fn()
function setup() { return render(<AuthenticationContext.Provider value={{ state: { status: 'authenticated', user }, login: vi.fn(), logout: vi.fn(), retry: vi.fn(), refreshCurrentUser, invalidateAdminAccess, identityRevision: 1, refreshRevision: 1, isLoggingIn: false, isLoggingOut: false }}><MemoryRouter><UserManagementPage /></MemoryRouter></AuthenticationContext.Provider>) }
beforeEach(() => { listUsers.mockReset().mockResolvedValue([user]); updateAdmin.mockReset(); refreshCurrentUser.mockReset().mockRejectedValue({ kind: 'network' }); invalidateAdminAccess.mockReset() })
afterEach(cleanup)
it('keeps last administrator visible on conflict', async () => {
 updateAdmin.mockRejectedValue({ kind: 'problemDetails', code: 'last_admin_required' }); setup(); const actor = userEvent.setup()
 await actor.click(await screen.findByRole('button', { name: '取消管理员' })); await actor.click(screen.getByRole('button', { name: /确.*认/ }))
 expect(await screen.findByText('至少需要保留一名管理员。')).toBeInTheDocument(); expect(invalidateAdminAccess).not.toHaveBeenCalled()
})
it('clears directory and dialogs on forbidden even when profile refresh fails', async () => {
 updateAdmin.mockRejectedValue({ kind: 'problemDetails', code: 'forbidden' }); setup(); const actor = userEvent.setup()
 await actor.click(await screen.findByRole('button', { name: '取消管理员' })); await actor.click(screen.getByRole('button', { name: /确.*认/ }))
 expect(await screen.findByText(/无管理权限/)).toBeInTheDocument(); expect(screen.queryByRole('table')).not.toBeInTheDocument(); expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
 expect(invalidateAdminAccess).toHaveBeenCalledOnce(); await waitFor(() => expect(refreshCurrentUser).toHaveBeenCalledOnce())
})
it('does not interpret Origin rejection as role revocation', async () => {
 updateAdmin.mockRejectedValue({ kind: 'problemDetails', code: 'origin_not_allowed' }); setup(); const actor = userEvent.setup()
 await actor.click(await screen.findByRole('button', { name: '取消管理员' })); await actor.click(screen.getByRole('button', { name: /确.*认/ }))
 await screen.findByText('身份更新失败，请重试。'); expect(invalidateAdminAccess).not.toHaveBeenCalled(); expect(screen.getByRole('table')).toBeInTheDocument()
})
it('ignores a list result arriving after unmount', async () => {
 let resolve!: (value: typeof user[]) => void; listUsers.mockReturnValue(new Promise(r => { resolve = r }))
 const view = setup(); view.unmount(); await act(async () => { resolve([user]) })
 expect(screen.queryByRole('table')).not.toBeInTheDocument()
})
