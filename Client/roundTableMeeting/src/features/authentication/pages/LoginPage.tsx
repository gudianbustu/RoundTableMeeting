import { Typography } from 'antd'
import { Navigate, useLocation } from 'react-router-dom'
import { LoadingState, ErrorState } from '@/shared/components/PageState'
import { LoginForm } from '../components/LoginForm'
import { useAuthentication } from '../hooks/useAuthentication'
import type { LoginInput } from '../types/authentication'

/** 在登录后跳转前校验路由提供的返回目标 */
function getReturnTo(value: unknown) {
  if (!value || typeof value !== 'object') return '/'
  const item = value as { pathname?: unknown; search?: unknown; hash?: unknown }
  if (typeof item.pathname !== 'string' || !item.pathname.startsWith('/') || item.pathname.startsWith('//') || item.pathname.includes('\\') || /^[a-z][a-z\d+.-]*:/i.test(item.pathname)) return '/'
  if (typeof item.search !== 'string' || (item.search && !item.search.startsWith('?')) || typeof item.hash !== 'string' || (item.hash && !item.hash.startsWith('#'))) return '/'
  return item.pathname + item.search + item.hash
}

/** 协调认证状态与登录页面表单 */
export function LoginPage() {
  const { state, login, isLoggingIn, operationError, retry } = useAuthentication()
  const location = useLocation()
  if (state.status === 'checking') return <LoadingState />
  if (state.status === 'authenticated') return <Navigate to={getReturnTo((location.state as { returnTo?: unknown } | null)?.returnTo)} replace />
  if (state.status === 'error') return <ErrorState title="服务暂时不可访问" description="请检查服务端是否已启动，然后重试。" traceId={state.error.traceId} onRetry={() => void retry()} />
  const submit = async (values: LoginInput) => { await login(values.username, values.password) }
  return <main className="login-page"><section className="login-panel"><Typography.Title level={1}><img className="brand-mark" src="/roundtable-mark.png" alt="" />圆桌会议</Typography.Title><Typography.Paragraph>登录以继续</Typography.Paragraph><LoginForm onSubmit={submit} loading={isLoggingIn} error={operationError} /></section></main>
}
