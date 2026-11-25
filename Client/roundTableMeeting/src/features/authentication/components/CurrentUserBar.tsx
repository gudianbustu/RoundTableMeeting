import { AvatarImage } from '@/features/avatars/AvatarImage'
import { Link, useLocation } from 'react-router-dom'
import { DownOutlined } from '@ant-design/icons'
import { Alert, Button, Dropdown, Flex } from 'antd'
import { useAuthentication } from '../hooks/useAuthentication'

/** 显示用户昵称，并通过菜单提供个人设置和退出操作 */
export function CurrentUserBar() {
  const { pathname } = useLocation()
  const settingsOrigin = { settingsReturnTo: pathname }
  const { state, logout, isLoggingOut, operationError } = useAuthentication()
  if (state.status !== 'authenticated') return null
  const name = state.user.nickname || state.user.username
  return <Flex vertical gap={4} className="current-user-bar">
    <Dropdown trigger={['hover', 'click']} placement="bottomRight" menu={{ items: [
      { type: 'group', label: <div className="account-menu-identity"><strong>{name}</strong><small>{state.user.isAdmin ? '管理员' : '普通用户'}</small></div> },
      { type: 'divider' },
      { key: 'profile', label: <Link to="/settings/profile" state={settingsOrigin}>个人设置</Link> },
      ...(state.user.isAdmin ? [
        { key: 'avatars', label: <Link to="/admin/avatars" state={settingsOrigin}>头像管理</Link> },
        { key: 'users', label: <Link to="/users" state={settingsOrigin}>用户管理</Link> },
      ] : []),
      { key: 'logout', label: '退出', disabled: isLoggingOut, onClick: () => void logout().catch(() => undefined) },
    ] }}>
      <Button type="text" className="account-trigger" aria-label={name} loading={isLoggingOut}><span aria-hidden="true" className="account-avatar"><AvatarImage src={state.user.avatarUrl} fallback={Array.from(name)[0]} /></span><strong>{name}</strong><DownOutlined /></Button>
    </Dropdown>
    {operationError && <Alert type="error" showIcon title="退出失败，请重试。" />}
  </Flex>
}
