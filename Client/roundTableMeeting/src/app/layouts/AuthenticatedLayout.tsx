import { SourceRepositoryLink } from '@/shared/components/SourceRepositoryLink'
import { AccountUnreadProvider } from '@/features/meetingWorkspace/chat/AccountUnreadProvider'
import { MentionNotificationListener } from '@/features/meetingWorkspace/chat/MentionNotificationListener'
import { Link, Outlet, useLocation } from 'react-router-dom'
import { CurrentUserBar } from '@/features/authentication'

export function AuthenticatedLayout() {
  const { pathname, state } = useLocation()
  const origin = state?.settingsReturnTo
  const returnTo = typeof origin === 'string' && /^\/meetings\/[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\/?$/i.test(origin) ? origin : '/'
  return <main className="app-shell authenticated-shell">
    <header className="app-header top-navigation">
      {pathname.startsWith('/meetings/') && <span id="workspace-navigation-trigger" />}
      <Link className="app-brand" to="/"><img className="brand-mark" src="/roundtable-mark.png" alt="" />圆桌会议</Link>
      {pathname.startsWith('/meetings/') && <div id="workspace-topbar-content" />}
      {pathname === '/' && <SourceRepositoryLink />}
      {pathname.replace(/\/$/, '') === '/admin/avatars' ? <Link to="/settings/profile">返回个人设置 ›</Link> :
        ['/settings/profile', '/users'].includes(pathname.replace(/\/$/, '')) ? <Link to={returnTo}>返回</Link> : <CurrentUserBar />}
    </header>
    <AccountUnreadProvider><MentionNotificationListener /><Outlet /></AccountUnreadProvider>
  </main>
}
