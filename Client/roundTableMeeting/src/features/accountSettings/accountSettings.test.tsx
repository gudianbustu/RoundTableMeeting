import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { AuthenticationContext } from '@/features/authentication/context/AuthenticationContext'
import { PersonalSettingsPage } from './pages/PersonalSettingsPage'
import type { AuthenticationContextValue } from '@/features/authentication'
const updateMyNickname = vi.hoisted(() => vi.fn())
const changeMyPassword = vi.hoisted(() => vi.fn())
vi.mock('./api/updateMyNickname', () => ({ updateMyNickname }))
vi.mock('./api/changeMyPassword', () => ({ changeMyPassword }))
const user = { userId: 'b', username: 'bob', nickname: 'Bob', isAdmin: false }
const refresh = vi.fn()
function setup() {
 const context: AuthenticationContextValue = { state: { status: 'authenticated', user }, identityRevision: 1, refreshRevision: 0, invalidateAdminAccess: vi.fn(), login: vi.fn(), logout: vi.fn(), retry: vi.fn(), refreshCurrentUser: refresh, isLoggingIn: false, isLoggingOut: false }
 return render(<AuthenticationContext.Provider value={context}><MemoryRouter><PersonalSettingsPage /></MemoryRouter></AuthenticationContext.Provider>)
}
beforeEach(() => { updateMyNickname.mockReset().mockResolvedValue(user); changeMyPassword.mockReset().mockResolvedValue(undefined); refresh.mockReset().mockResolvedValue({ status: 'refreshed', user, identityRevision: 1, refreshRevision: 1 }) })
afterEach(cleanup)
it('rejects mismatched confirmation and sends only original password values, clearing after success', async () => {
 setup(); const actor = userEvent.setup()
 await actor.type(screen.getByLabelText('当前密码'), ' old '); await actor.type(screen.getByLabelText('新密码', { exact: true }), ' new '); await actor.type(screen.getByLabelText('确认新密码'), 'wrong')
 await actor.click(screen.getByRole('button', { name: '修改密码' })); expect(await screen.findByText('两次密码不一致')).toBeInTheDocument(); expect(changeMyPassword).not.toHaveBeenCalled()
 await actor.clear(screen.getByLabelText('确认新密码')); await actor.type(screen.getByLabelText('确认新密码'), ' new ')
 await actor.click(screen.getByRole('button', { name: '修改密码' })); await screen.findByText('密码已修改')
 expect(changeMyPassword).toHaveBeenCalledWith({ currentPassword: ' old ', newPassword: ' new ' }, expect.any(AbortSignal))
 expect(screen.getByLabelText('当前密码')).toHaveValue(''); expect(screen.getByLabelText('昵称')).toHaveValue('Bob')
})
it('clears passwords after current-password error and retains the session', async () => {
 changeMyPassword.mockRejectedValue({ kind: 'problemDetails', code: 'validation_failed', fieldErrors: { currentPassword: ['invalid'] } })
 setup(); const actor = userEvent.setup()
 for (const label of ['当前密码', '新密码', '确认新密码']) await actor.type(screen.getByLabelText(label, { exact: true }), 'secret')
 await actor.click(screen.getByRole('button', { name: '修改密码' })); await screen.findByText('当前密码不正确，请重新输入。')
 expect(screen.getByLabelText('当前密码')).toHaveValue(''); expect(screen.getByText('普通用户')).toBeInTheDocument()
})
it('retries profile sync without saving nickname again, including cancelled refresh', async () => {
 refresh.mockResolvedValueOnce({ status: 'cancelled' }); setup(); const actor = userEvent.setup()
 await actor.click(screen.getByRole('button', { name: '保存昵称' })); await screen.findByText('昵称已保存，资料同步已取消，请重试同步。')
 await actor.click(screen.getByRole('button', { name: '重试资料同步' })); await waitFor(() => expect(refresh).toHaveBeenCalledTimes(2))
 expect(updateMyNickname).toHaveBeenCalledTimes(1)
})
