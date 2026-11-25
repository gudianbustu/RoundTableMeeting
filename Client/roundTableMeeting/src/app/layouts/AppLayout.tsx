import { Link, Outlet } from 'react-router-dom'

export function AppLayout() {
  return <div className="status-shell">
    <header className="status-brand"><Link to="/"><img src="/roundtable-mark.png" alt="" />圆桌会议</Link></header>
    <main><Outlet /></main>
  </div>
}
