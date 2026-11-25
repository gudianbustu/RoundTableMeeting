import { Alert, Button, Input, Tabs, Typography, message, Modal } from 'antd'
import './UserManagementPage.css'
import { useLocation, useNavigate } from 'react-router-dom'
import { updateAdmin } from '../api/updateAdmin'
import { PlusOutlined } from '@ant-design/icons'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useAuthentication } from '@/features/authentication'
import { ErrorState, LoadingState } from '@/shared/components/PageState'
import { toApiError } from '@/api/errors/toApiError'
import type { ApiError } from '@/api/errors/ApiError'
import { listUsers } from '../api/listUsers'
import { createUser } from '../api/createUser'
import { updateNickname } from '../api/updateNickname'
import { resetPassword } from '../api/resetPassword'
import { CreateUserForm } from '../components/CreateUserForm'
import { EditNicknameForm } from '../components/EditNicknameForm'
import { ResetPasswordDialog } from '../components/ResetPasswordDialog'
import { UserListTable } from '../components/UserListTable'
import type { CreateUserInput, UpdateNicknameInput, UserSummaryViewModel } from '../types/userManagement'

export function UserManagementPage() {
  const { state, refreshCurrentUser, invalidateAdminAccess } = useAuthentication()
  const navigate = useNavigate()
  const location = useLocation()
  const [adminUser, setAdminUser] = useState<UserSummaryViewModel>()
  const [adminLoading, setAdminLoading] = useState(false)
  const [adminError, setAdminError] = useState('')
  const adminController = useRef<AbortController | undefined>(undefined)
  const [forbidden, setForbidden] = useState(false)
  const [role, setRole] = useState('member')
  const [query, setQuery] = useState('')
  const [users, setUsers] = useState<UserSummaryViewModel[]>([])
  const [loadState, setLoadState] = useState<'loading' | 'ready' | 'error'>('loading')
  const [loadError, setLoadError] = useState<ApiError>()
  const [createOpen, setCreateOpen] = useState(false)
  const [createError, setCreateError] = useState<ApiError>()
  const [createLoading, setCreateLoading] = useState(false)
  const [editUser, setEditUser] = useState<UserSummaryViewModel>()
  const [editError, setEditError] = useState<ApiError>()
  const [editingUserId, setEditingUserId] = useState<string>()
  const [resetUser, setResetUser] = useState<UserSummaryViewModel>()
  const [resetError, setResetError] = useState<ApiError>()
  const [resettingUserId, setResettingUserId] = useState<string>()
  const listController = useRef<AbortController | undefined>(undefined)
  const createController = useRef<AbortController | undefined>(undefined)
  const editController = useRef<AbortController | undefined>(undefined)
  const resetController = useRef<AbortController | undefined>(undefined)
  const listVersion = useRef(0)
  const createVersion = useRef(0)
  const editVersion = useRef(0)
  const resetVersion = useRef(0)

  const denyAccess = useCallback((error: unknown) => {
    if (toApiError(error).code !== 'forbidden') return false
    listController.current?.abort(); createController.current?.abort(); editController.current?.abort(); resetController.current?.abort(); adminController.current?.abort()
    listVersion.current++; createVersion.current++; editVersion.current++; resetVersion.current++
    setUsers([]); setCreateOpen(false); setEditUser(undefined); setResetUser(undefined); setAdminUser(undefined); setForbidden(true)
    invalidateAdminAccess()
    void refreshCurrentUser().catch(() => undefined)
    return true
  }, [invalidateAdminAccess, refreshCurrentUser])

  const load = useCallback(async () => {
    listController.current?.abort()
    const controller = new AbortController()
    listController.current = controller
    const version = ++listVersion.current
    setLoadState('loading')
    try {
      const result = await listUsers(controller.signal)
      if (controller.signal.aborted || version !== listVersion.current) return
      setUsers(result)
      setLoadError(undefined)
      setLoadState('ready')
    } catch (error) {
      if (controller.signal.aborted || (error instanceof Error && error.name === 'AbortError') || version !== listVersion.current) return
      if (denyAccess(error)) return
      setLoadError(toApiError(error))
      setLoadState('error')
    } finally {
      if (listController.current === controller) listController.current = undefined
    }
  }, [denyAccess])

  useEffect(() => {
    void load()
    return () => {
      listController.current?.abort()
      createController.current?.abort()
      editController.current?.abort()
      resetController.current?.abort()
      adminController.current?.abort()
      listVersion.current += 1
      createVersion.current += 1
      editVersion.current += 1
      resetVersion.current += 1
    }
  }, [load])

  const handleCreate = async (input: CreateUserInput) => {
    createController.current?.abort()
    const controller = new AbortController()
    createController.current = controller
    const version = ++createVersion.current
    setCreateLoading(true)
    setCreateError(undefined)
    try {
      await createUser(input, controller.signal)
      if (controller.signal.aborted || version !== createVersion.current) return
      setCreateOpen(false)
      setRole('member')
      setQuery('')
      message.success('账号已创建')
      void load()
    } catch (error) {
      if (controller.signal.aborted || (error instanceof Error && error.name === 'AbortError') || version !== createVersion.current) return
      if (denyAccess(error)) return
      setCreateError(toApiError(error))
    } finally {
      if (createController.current === controller) {
        createController.current = undefined
        setCreateLoading(false)
      }
    }
  }

  const handleEdit = async (input: UpdateNicknameInput) => {
    if (!editUser) return
    editController.current?.abort()
    const target = editUser
    const controller = new AbortController()
    editController.current = controller
    const version = ++editVersion.current
    setEditingUserId(target.userId)
    setEditError(undefined)
    try {
      await updateNickname(target.userId, input, controller.signal)
      if (controller.signal.aborted || version !== editVersion.current) return
      setEditUser(undefined)
      message.success('昵称已更新')
      const refreshes: Promise<unknown>[] = [load()]
      if (state.status === 'authenticated' && state.user.userId === target.userId) refreshes.push(refreshCurrentUser())
      const results = await Promise.allSettled(refreshes)
      if (!controller.signal.aborted && results.some(result => result.status === 'rejected' || result.status === 'fulfilled' && (result.value as { status?: string })?.status === 'cancelled')) message.warning('昵称已更新，但部分资料刷新失败。')
    } catch (error) {
      if (controller.signal.aborted || (error instanceof Error && error.name === 'AbortError') || version !== editVersion.current) return
      if (denyAccess(error)) return
      const apiError = toApiError(error)
      if (apiError.code === 'user_not_found') {
        setEditUser(undefined)
        message.error('账号已不存在')
        void load()
      } else {
        setEditError(apiError)
        message.error('昵称修改失败')
      }
    } finally {
      if (editController.current === controller) {
        editController.current = undefined
        setEditingUserId(undefined)
      }
    }
  }

  const handleReset = async () => {
    if (!resetUser) return
    resetController.current?.abort()
    const target = resetUser
    const controller = new AbortController()
    resetController.current = controller
    const version = ++resetVersion.current
    setResetError(undefined)
    setResettingUserId(target.userId)
    try {
      await resetPassword(target.userId, controller.signal)
      if (controller.signal.aborted || version !== resetVersion.current) return
      setResetUser(undefined)
      message.success('密码已重置为 123456')
      void load()
    } catch (error) {
      if (controller.signal.aborted || (error instanceof Error && error.name === 'AbortError') || version !== resetVersion.current) return
      if (denyAccess(error)) return
      const apiError = toApiError(error)
      if (apiError.code === 'user_not_found') {
        setResetUser(undefined)
        message.error('账号已不存在')
        void load()
      } else {
        setResetError(apiError)
        message.error('密码重置失败')
      }
    } finally {
      if (resetController.current === controller) {
        resetController.current = undefined
        setResettingUserId(undefined)
      }
    }
  }

  const handleAdmin = async () => {
    if (!adminUser) return
    const target = adminUser
    adminController.current?.abort()
    const controller = new AbortController(); adminController.current = controller
    setAdminLoading(true); setAdminError('')
    try {
      await updateAdmin(target.userId, { isAdmin: !target.isAdmin }, controller.signal)
      if (controller.signal.aborted) return
      setAdminUser(undefined)
      if (state.status === 'authenticated' && state.user.userId === target.userId && target.isAdmin) {
        invalidateAdminAccess()
        navigate('/settings/profile', { replace: true, state: location.state })
        void refreshCurrentUser().catch(() => undefined)
      } else { message.success('管理员身份已更新'); void load() }
    } catch (error) {
      if (controller.signal.aborted || denyAccess(error)) return
      setAdminError(toApiError(error).code === 'last_admin_required' ? '至少需要保留一名管理员。' : '身份更新失败，请重试。')
    } finally { if (!controller.signal.aborted) setAdminLoading(false) }
  }

  if (forbidden) return <div role="alert">无管理权限：管理权限已失效。</div>
  if (loadState === 'loading') return <LoadingState />
  if (loadState === 'error') return <section className="user-management-page"><ErrorState title="用户列表加载失败" description="请稍后重试。" traceId={loadError?.traceId} onRetry={() => void load()} /></section>
  const keyword = query.trim().toLowerCase()
  const roleTabs = [{ key: 'member', label: '普通用户' }, { key: 'admin', label: '管理员' }]
  return <section className="user-directory">
    <div className="user-directory-heading">
      <h1>用户管理</h1>
      <Button type="primary" icon={<PlusOutlined />} onClick={() => { setCreateError(undefined); setCreateOpen(true) }}>创建账号</Button>
    </div>
    <div className="user-directory-card">
      <Tabs activeKey={role} onChange={key => { setRole(key); setQuery('') }} destroyOnHidden items={roleTabs.map(tab => ({
        ...tab,
        children: <>
          <Input className="user-directory-search" value={query} onChange={event => setQuery(event.target.value)}
            allowClear placeholder="按昵称或登录账号查找" aria-label="按昵称或登录账号查找" />
          <UserListTable users={users.filter(user => user.isAdmin === (tab.key === 'admin')
            && [user.nickname, user.username].some(value => value.toLowerCase().includes(keyword)))}
            emptyText={keyword ? `没有找到匹配的${tab.label}` : `暂无${tab.label}`}
            editingUserId={editingUserId} resettingUserId={resettingUserId}
            onEdit={user => { setEditError(undefined); setEditUser(user) }}
            onAdmin={user => { setAdminError(''); setAdminUser(user) }}
            onReset={user => { setResetError(undefined); setResetUser(user) }} />
        </>,
      }))} />
    </div>
    <CreateUserForm open={createOpen} loading={createLoading} error={createError} onCancel={() => setCreateOpen(false)} onSubmit={handleCreate} />
    <EditNicknameForm user={editUser} loading={Boolean(editingUserId)} error={editError} onCancel={() => setEditUser(undefined)} onSubmit={handleEdit} />
    <ResetPasswordDialog user={resetUser} loading={Boolean(resettingUserId)} error={resetError} onCancel={() => setResetUser(undefined)} onConfirm={handleReset} />
    <Modal centered className="user-directory-dialog" okText="确认" cancelText="取消" title={adminUser?.isAdmin ? '取消管理员' : '设为管理员'}
      open={Boolean(adminUser)} onCancel={() => { if (!adminLoading) setAdminUser(undefined) }} onOk={() => void handleAdmin()}
      confirmLoading={adminLoading} cancelButtonProps={{ disabled: adminLoading }}>
      {adminUser && <>
        <dl className="user-directory-target"><dt>登录账号</dt><dd>{adminUser.username}</dd><dt>昵称</dt><dd>{adminUser.nickname}</dd></dl>
        <Typography.Paragraph>{adminUser.isAdmin ? '确认后，此用户将变为普通用户。' : '确认后，此用户将获得管理员权限。'}</Typography.Paragraph>
      </>}
      {adminError && <Alert title={adminError} type="error" />}
    </Modal>
  </section>
}
