import { Link, isRouteErrorResponse, useRouteError } from 'react-router-dom'

/** 为路由级故障渲染稳定的恢复页面 */
export function RouteErrorElement() {
  const error = useRouteError()
  const message = isRouteErrorResponse(error) ? '页面暂时无法加载。' : '页面发生了意外错误。'
  return (
    <section className="page-state page-state-error" role="alert">
      <h2>页面不可用</h2>
      <p>{message}</p>
      <Link className="text-link" to="/system/status">返回系统状态</Link>
    </section>
  )
}
