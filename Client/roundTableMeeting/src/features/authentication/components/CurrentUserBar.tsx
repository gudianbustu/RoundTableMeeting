import { Alert, Button, Space, Typography } from 'antd'
import { useAuthentication } from '../hooks/useAuthentication'

export function CurrentUserBar() {
  const { state, logout, isLoggingOut, operationError } = useAuthentication()
  if (state.status !== 'authenticated') return null
  return <Space direction="vertical" size={4} className="current-user-bar">
    <Space wrap><Typography.Text strong>{state.user.nickname || state.user.username}</Typography.Text><Typography.Text type="secondary">{state.user.username}</Typography.Text><Button onClick={() => void logout().catch(() => undefined)} loading={isLoggingOut} disabled={isLoggingOut}>退出</Button></Space>
    {operationError && <Alert type="error" showIcon message="退出失败，请重试。" />}
  </Space>
}
