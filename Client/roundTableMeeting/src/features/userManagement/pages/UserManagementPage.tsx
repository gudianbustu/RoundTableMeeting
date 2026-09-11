import { Alert, Button, Space, Typography, message, Modal } from 'antd'
import { useNavigate } from 'react-router-dom'
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
  const [adminUser, setAdminUser] = useState<UserSummaryViewModel>()
  const [adminLoading, setAdminLoading] = useState(false)
  const [adminError, setAdminError] = useState('')
  const adminController = useRef<AbortController | undefined>(undefined)
  const [forbidden, setForbidden] = useState(false)
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
        navigate('/settings/profile', { replace: true })
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
  return <section className="user-management-page"><Space direction="vertical" size="large" style={{ width: '100%' }}><Space align="center" wrap style={{ justifyContent: 'space-between', width: '100%' }}><Typography.Title level={2} style={{ margin: 0 }}>用户管理</Typography.Title><Button type="primary" icon={<PlusOutlined />} onClick={() => { setCreateError(undefined); setCreateOpen(true) }}>添加账号</Button></Space>{loadError && <Alert type="warning" message="列表刷新失败，当前显示可能已过期。" />}{users.length === 0 ? <Typography.Text type="secondary">暂无账号</Typography.Text> : <UserListTable onAdmin={(user) => { setAdminError(''); setAdminUser(user) }} users={users} editingUserId={editingUserId} resettingUserId={resettingUserId} onEdit={(user) => { setEditError(undefined); setEditUser(user) }} onReset={(user) => { setResetError(undefined); setResetUser(user) }} />}</Space><CreateUserForm open={createOpen} loading={createLoading} error={createError} onCancel={() => setCreateOpen(false)} onSubmit={handleCreate} /><EditNicknameForm user={editUser} loading={Boolean(editingUserId)} error={editError} onCancel={() => setEditUser(undefined)} onSubmit={handleEdit} /><ResetPasswordDialog user={resetUser} loading={Boolean(resettingUserId)} error={resetError} onCancel={() => setResetUser(undefined)} onConfirm={handleReset} /><Modal okText="确认" cancelText="取消" title={adminUser?.isAdmin ? '取消管理员' : '设为管理员'} open={Boolean(adminUser)} onCancel={() => { if (!adminLoading) setAdminUser(undefined) }} onOk={() => void handleAdmin()} confirmLoading={adminLoading} cancelButtonProps={{ disabled: adminLoading }}><Typography.Paragraph>确认修改 {adminUser?.username} 的管理员身份？</Typography.Paragraph>{adminError && <Alert title={adminError} type="error" />}</Modal></section>
}
