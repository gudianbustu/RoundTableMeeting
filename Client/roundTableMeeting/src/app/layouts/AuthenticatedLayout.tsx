import { Link, Outlet, useLocation } from 'react-router-dom'
import { CurrentUserBar } from '@/features/authentication'

export function AuthenticatedLayout() {
  const { pathname } = useLocation()
  return <main className="app-shell authenticated-shell">
    <header className="app-header top-navigation">
      {pathname.startsWith('/meetings/') && <span id="workspace-navigation-trigger" />}
      <Link className="app-brand" to="/"><img className="brand-mark" src="/roundtable-mark.png" alt="" />圆桌会议</Link>
      {['/settings/profile', '/users'].includes(pathname.replace(/\/$/, '')) ? <Link to="/">返回工作台 ›</Link> : <CurrentUserBar />}
    </header>
    <Outlet />
  </main>
}
