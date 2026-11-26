import { cleanup, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ConfigProvider } from 'antd'
import { CreateUserForm } from './components/CreateUserForm'
import { ResetPasswordDialog } from './components/ResetPasswordDialog'
import { UserListTable } from './components/UserListTable'

const user = { userId: 'user-1', username: 'alice', nickname: 'Alice', isAdmin: true }

afterEach(cleanup)

describe('user management components', () => {
  it('submits identity fields and preserves them when the username conflicts', async () => {
    const onSubmit = vi.fn()
    const actor = userEvent.setup()
    const view = render(<ConfigProvider><CreateUserForm open loading={false} onCancel={vi.fn()} onSubmit={onSubmit} /></ConfigProvider>)
    await actor.type(screen.getByLabelText('登录账号'), 'alice')
    await actor.type(screen.getByLabelText('昵称'), 'Alice')
    expect(screen.queryByLabelText('初始密码')).toBeNull()
    await actor.click(screen.getByRole('button', { name: '添加账号' }))
    await waitFor(() => expect(onSubmit).toHaveBeenCalledWith({ username: 'alice', nickname: 'Alice' }))

    view.rerender(<ConfigProvider><CreateUserForm open loading={false} error={{ kind: 'problemDetails', code: 'username_conflict', message: 'conflict' }} onCancel={vi.fn()} onSubmit={onSubmit} /></ConfigProvider>)

    await waitFor(() => expect(screen.getByText('这个登录账号已被使用，请换一个。')).toBeInTheDocument())
    expect(screen.getByLabelText('登录账号')).toHaveValue('alice')
    expect(screen.getByLabelText('昵称')).toHaveValue('Alice')
  })

  it('explains an invalid instance password configuration in the form', () => {
    const error = { kind: 'problemDetails' as const, code: 'account_initial_password_configuration_invalid', message: 'Service unavailable', fieldErrors: { configuration: ['Invalid configuration'] } }
    render(<ConfigProvider><CreateUserForm open loading={false} error={error} onCancel={vi.fn()} onSubmit={vi.fn()} /></ConfigProvider>)
    expect(screen.getByText('当前实例尚未配置有效的开户初始密码，请联系部署管理员。')).toBeInTheDocument()
  })

  it('applies the frozen field limits', async () => {
    const submit = vi.fn()
    render(<ConfigProvider><CreateUserForm open loading={false} onCancel={vi.fn()} onSubmit={submit} /></ConfigProvider>)

    expect(submit).not.toHaveBeenCalled()
    expect(screen.getByLabelText('登录账号')).toHaveAttribute('maxlength', '128')
    expect(screen.getByLabelText('昵称')).toHaveAttribute('maxlength', '64')
  })

  it('disables only the busy target row commands', () => {
    render(<ConfigProvider><UserListTable onAdmin={vi.fn()} users={[user, { userId: 'user-2', username: 'bob', nickname: 'Bob', isAdmin: true }]} editingUserId="user-1" onEdit={vi.fn()} onReset={vi.fn()} /></ConfigProvider>)

    expect(screen.getByLabelText('修改 alice 的昵称')).toBeDisabled()
    expect(screen.getByLabelText('重置 alice 的密码')).toBeDisabled()
    expect(screen.getByLabelText('修改 bob 的昵称')).toBeEnabled()
    expect(screen.getByLabelText('重置 bob 的密码')).toBeEnabled()
  })

  it('shows fixed reset value and disables cancel while loading', () => {
    render(<ConfigProvider><ResetPasswordDialog user={user} loading error={{ kind: 'network', message: 'offline' }} onCancel={vi.fn()} onConfirm={vi.fn()} /></ConfigProvider>)

    expect(screen.getByText('123456')).toBeInTheDocument()
    expect(screen.getByText('已有登录会话不会立即失效。')).toBeInTheDocument()
    expect(screen.getByText('密码重置失败，请稍后重试。')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /取\s*消/ })).toBeDisabled()
  })
})
