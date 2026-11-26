import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { ErrorState, LoadingState } from '@/shared/components/PageState'
import { useAuthentication } from '../hooks/useAuthentication'

/** 确保登录后导航保持同源并移除不安全目标 */
function safeReturnTo(location: ReturnType<typeof useLocation>) {
  const pathname = location.pathname
  if (!pathname.startsWith('/') || pathname.startsWith('//') || pathname.includes('\\') || /^[a-z][a-z\d+.-]*:/i.test(pathname)) return undefined
  return { pathname, search: location.search, hash: location.hash }
}

/** 保护认证路由并为登录保留安全的返回地址 */
export function AuthGate() {
  const { state, retry } = useAuthentication()
  const location = useLocation()
  if (state.status === 'checking') return <LoadingState />
  if (state.status === 'authenticated') return <Outlet />
  if (state.status === 'error') return <ErrorState title="服务暂时不可访问" description="请检查服务端是否已启动，然后重试。" traceId={state.error.traceId} onRetry={() => void retry()} />
  if (location.pathname === '/' && !location.hash && !location.search) return <Navigate to="/welcome" replace />
  return <Navigate to="/login" replace state={{ returnTo: safeReturnTo(location) }} />
}
