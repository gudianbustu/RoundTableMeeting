import { Outlet } from 'react-router-dom'
import { CurrentUserBar } from '@/features/authentication'
import { Link } from 'react-router-dom'

/** 需要认证用户的路由布局 */
export function AuthenticatedLayout() {
  return <main className="app-shell authenticated-shell"><header className="app-header"><div><p className="eyebrow">ROUND TABLE MEETING</p><h1>圆桌会议</h1><nav><Link to="/">首页</Link> <Link to="/users">用户管理</Link></nav></div><CurrentUserBar /></header><Outlet /></main>
}
