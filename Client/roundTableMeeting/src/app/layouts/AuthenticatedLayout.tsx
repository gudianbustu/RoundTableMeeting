import { Link, Outlet, useLocation } from 'react-router-dom'
import { CurrentUserBar } from '@/features/authentication'

/** 需要认证用户的路由布局 */
export function AuthenticatedLayout() {
  const { pathname } = useLocation()
  const inMeetings = pathname === '/' || pathname.startsWith('/meetings/')
  return <main className="app-shell authenticated-shell">
    <header className="app-header top-navigation">
      <div className="top-navigation-links">
        <Link className="app-brand" to="/">圆桌会议</Link>
        <nav aria-label="主导航">
          <Link to="/" aria-current={inMeetings ? 'page' : undefined}>首页</Link>
          <Link to="/users" aria-current={pathname === '/users' ? 'page' : undefined}>用户管理</Link>
        </nav>
      </div>
      <CurrentUserBar />
    </header>
    <Outlet />
  </main>
}
