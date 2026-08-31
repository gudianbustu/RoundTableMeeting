import { Outlet } from 'react-router-dom'
import { CurrentUserBar } from '@/features/authentication'

/** 需要认证用户的路由布局 */
export function AuthenticatedLayout() {
  return <main className="app-shell authenticated-shell"><header className="app-header"><div><p className="eyebrow">ROUND TABLE MEETING</p><h1>圆桌会议</h1></div><CurrentUserBar /></header><Outlet /></main>
}
