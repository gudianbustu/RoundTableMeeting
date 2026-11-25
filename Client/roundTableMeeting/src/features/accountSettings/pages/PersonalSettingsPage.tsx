import { PersonalAvatarDialog } from '@/features/avatars/PersonalAvatarDialog'
import { AvatarImage } from '@/features/avatars/AvatarImage'
import { MentionNotificationSettings } from '../components/MentionNotificationSettings'
import { Alert, Button, Card, Form, Input } from 'antd'
import { TeamOutlined } from '@ant-design/icons'
import { Link, useLocation } from 'react-router-dom'
import { AppearanceSettings } from '../components/AppearanceSettings'
import { useEffect, useRef, useState } from 'react'
import { useAuthentication } from '@/features/authentication'
import { toApiError } from '@/api/errors/toApiError'
import { updateMyNickname } from '../api/updateMyNickname'
import { changeMyPassword } from '../api/changeMyPassword'
import type { ChangeMyPasswordInput, UpdateMyNicknameInput } from '../types/accountSettings'

export function PersonalSettingsPage() {
  const location = useLocation()
  const { state, identityRevision, refreshCurrentUser } = useAuthentication()
  const [changingAvatar, setChangingAvatar] = useState(false)
  const [nicknameForm] = Form.useForm<UpdateMyNicknameInput>()
  const [passwordForm] = Form.useForm<ChangeMyPasswordInput & { confirmPassword: string }>()
  const [nicknameBusy, setNicknameBusy] = useState(false)
  const [passwordBusy, setPasswordBusy] = useState(false)
  const [nicknameMessage, setNicknameMessage] = useState('')
  const [passwordMessage, setPasswordMessage] = useState('')
  const [syncNeeded, setSyncNeeded] = useState(false)
  const nicknameController = useRef<AbortController | undefined>(undefined)
  const passwordController = useRef<AbortController | undefined>(undefined)
  const generation = useRef(0)
  const syncVersion = useRef(0)
  useEffect(() => {
    setNicknameBusy(false); setPasswordBusy(false)
    passwordForm.resetFields()
    return () => {
      generation.current++
      syncVersion.current++
      nicknameController.current?.abort(); passwordController.current?.abort()
    }
  }, [identityRevision, passwordForm])
  const syncProfile = async (version = generation.current) => {
    const sync = ++syncVersion.current
    try {
      const result = await refreshCurrentUser()
      if (version !== generation.current || sync !== syncVersion.current) return
      const synced = result.status === 'refreshed'
      setSyncNeeded(!synced)
      setNicknameMessage(synced ? '昵称已保存' : '昵称已保存，资料同步已取消，请重试同步。')
    } catch {
      if (version !== generation.current || sync !== syncVersion.current) return
      setSyncNeeded(true); setNicknameMessage('昵称已保存，资料同步失败，请重试同步。')
    }
  }
  const saveNickname = async (input: UpdateMyNicknameInput) => {
    nicknameController.current?.abort()
    const controller = new AbortController(); nicknameController.current = controller
    const version = generation.current
    setNicknameBusy(true); setNicknameMessage(''); setSyncNeeded(false)
    try {
      await updateMyNickname(input, controller.signal)
      if (controller.signal.aborted || version !== generation.current) return
      setNicknameMessage('昵称已保存')
      await syncProfile(version)
    } catch (error) {
      if (controller.signal.aborted || version !== generation.current) return
      const apiError = toApiError(error)
      nicknameForm.setFields([{ name: 'nickname', errors: apiError.fieldErrors?.nickname ?? [] }])
      setNicknameMessage('昵称保存失败，请检查输入后重试。')
    } finally {
      if (!controller.signal.aborted && version === generation.current) setNicknameBusy(false)
    }
  }
  const savePassword = async (input: ChangeMyPasswordInput & { confirmPassword: string }) => {
    passwordController.current?.abort()
    const controller = new AbortController(); passwordController.current = controller
    const version = generation.current
    setPasswordBusy(true); setPasswordMessage('')
    try {
      await changeMyPassword({ currentPassword: input.currentPassword, newPassword: input.newPassword }, controller.signal)
      if (!controller.signal.aborted && version === generation.current) setPasswordMessage('密码已修改')
    } catch (error) {
      if (controller.signal.aborted || version !== generation.current) return
      const apiError = toApiError(error)
      setPasswordMessage(apiError.fieldErrors?.currentPassword ? '当前密码不正确，请重新输入。' : '密码修改失败，请重试。')
    } finally {
      if (!controller.signal.aborted && version === generation.current) { passwordForm.resetFields(); setPasswordBusy(false) }
    }
  }
  if (state.status !== 'authenticated') return null
  return <section className="profile-settings">
    <aside className="profile-column">
      <div className="profile-identity">
        <h1>个人设置</h1>
        <div className="profile-person">
          <button type="button" className="profile-avatar profile-avatar-button" aria-label="更换个人头像" title="更换个人头像" onClick={() => setChangingAvatar(true)}><AvatarImage src={state.user.avatarUrl} fallback={Array.from(state.user.nickname)[0]} /></button>
          <div><strong>{state.user.nickname}</strong><small>{state.user.isAdmin ? '管理员' : '普通用户'}</small></div>
        </div>
        <div className="profile-account"><span>用户名</span><strong>{state.user.username}</strong>
          {state.user.isAdmin && <Link className="profile-admin-link" to="/users" state={location.state}><TeamOutlined /> 用户管理</Link>}
        </div>
      </div>
      {changingAvatar && <PersonalAvatarDialog currentUrl={state.user.avatarUrl} onClose={() => setChangingAvatar(false)} onSaved={() => { setChangingAvatar(false); void refreshCurrentUser() }} />}
      <AppearanceSettings />
      <MentionNotificationSettings />
    </aside>
    <div className="profile-column">
      <Card title="账号昵称">
        <p>用于账号展示。会议内昵称可在对应会议成员列表中修改。</p>
        <Form className="nickname-settings" form={nicknameForm} layout="vertical" initialValues={{ nickname: state.user.nickname }} onFinish={saveNickname} disabled={nicknameBusy}>
          <Form.Item name="nickname" label="昵称" rules={[{ required: true, whitespace: true, message: '请输入昵称' }, { max: 64, message: '昵称最多 64 个字符' }]}><Input maxLength={64} autoComplete="nickname" /></Form.Item>
          <Button htmlType="submit" type="primary" loading={nicknameBusy}>保存昵称</Button>
        </Form>
        {nicknameMessage && <Alert style={{ marginTop: 16 }} title={nicknameMessage} type={syncNeeded ? 'warning' : 'info'} />}
        {syncNeeded && <Button onClick={() => void syncProfile()}>重试资料同步</Button>}
      </Card>

      <Card title="修改密码">
        <p>先验证当前密码，再设置并确认新密码。</p>
        <Form className="password-settings" form={passwordForm} layout="vertical" onFinish={savePassword} disabled={passwordBusy}>
          <Form.Item name="currentPassword" label="当前密码" rules={[{ required: true, message: '请输入当前密码' }, { max: 256 }]}><Input.Password autoComplete="current-password" /></Form.Item>
          <Form.Item name="newPassword" label="新密码" rules={[{ required: true, whitespace: true, message: '请输入新密码' }, { max: 256, message: '密码最多 256 个字符' }]}><Input.Password autoComplete="new-password" /></Form.Item>
          <Form.Item name="confirmPassword" label="确认新密码" dependencies={['newPassword']} rules={[{ required: true, message: '请再次输入新密码' }, ({ getFieldValue }) => ({ validator: (_, value) => value === getFieldValue('newPassword') ? Promise.resolve() : Promise.reject(new Error('两次密码不一致')) })]}><Input.Password autoComplete="new-password" /></Form.Item>
          <Button htmlType="submit" type="primary" loading={passwordBusy}>修改密码</Button>
        </Form>
        {passwordMessage && <Alert style={{ marginTop: 16 }} title={passwordMessage} type="info" />}
      </Card>
    </div>
  </section>
}
