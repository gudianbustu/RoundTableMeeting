import { Alert, Button, Flex, Typography } from 'antd'
import { useAuthentication } from '../hooks/useAuthentication'

/** 显示已登录用户并提供退出操作 */
export function CurrentUserBar() {
  const { state, logout, isLoggingOut, operationError } = useAuthentication()
  if (state.status !== 'authenticated') return null
  return <Flex vertical gap={4} className="current-user-bar">
    <Flex wrap gap="small" align="center"><Typography.Text strong>{state.user.nickname || state.user.username}</Typography.Text><Typography.Text type="secondary">{state.user.username}</Typography.Text><Button onClick={() => void logout().catch(() => undefined)} loading={isLoggingOut} disabled={isLoggingOut}>退出</Button></Flex>
    {operationError && <Alert type="error" showIcon title="退出失败，请重试。" />}
  </Flex>
}
