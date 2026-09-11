import { Link } from 'react-router-dom'
import { Alert, Button, Dropdown, Flex } from 'antd'
import { useAuthentication } from '../hooks/useAuthentication'

/** 显示用户昵称，并通过菜单提供个人中心和退出操作 */
export function CurrentUserBar() {
  const { state, logout, isLoggingOut, operationError } = useAuthentication()
  if (state.status !== 'authenticated') return null
  return <Flex vertical gap={4} className="current-user-bar">
    <Dropdown trigger={['hover', 'click']} placement="bottomRight" menu={{ items: [
      { key: 'profile', label: <Link to="/settings/profile">个人中心</Link> },
      { key: 'logout', label: '退出', disabled: isLoggingOut, onClick: () => void logout().catch(() => undefined) },
    ] }}>
      <Button type="text" loading={isLoggingOut} style={{ fontWeight: 600 }}>{state.user.nickname || state.user.username}</Button>
    </Dropdown>
    {operationError && <Alert type="error" showIcon title="退出失败，请重试。" />}
  </Flex>
}
