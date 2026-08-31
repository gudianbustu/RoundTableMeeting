import { Outlet } from 'react-router-dom'

/** 公共页面与系统状态页面共用的布局 */
export function AppLayout() {
  return (
    <main className="app-shell">
      <header className="app-header">
        <div>
          <p className="eyebrow">ROUND TABLE MEETING</p>
          <h1>系统状态</h1>
        </div>
        <span className="framework-badge">框架验收</span>
      </header>
      <Outlet />
    </main>
  )
}
