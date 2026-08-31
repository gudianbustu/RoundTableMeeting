import { Descriptions, Typography } from 'antd'
import { useAuthentication } from '../hooks/useAuthentication'

/** 显示认证用户的基本资料 */
export function AuthenticatedHomePage() {
  const { state } = useAuthentication()
  if (state.status !== 'authenticated') return null
  return <section className="authenticated-home"><Typography.Title level={2}>欢迎回来</Typography.Title><Descriptions bordered column={1}><Descriptions.Item label="UserId">{state.user.userId}</Descriptions.Item><Descriptions.Item label="用户名">{state.user.username}</Descriptions.Item><Descriptions.Item label="昵称">{state.user.nickname}</Descriptions.Item></Descriptions></section>
}
