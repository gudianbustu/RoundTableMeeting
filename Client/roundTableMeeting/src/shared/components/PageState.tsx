import { Button, Result, Spin } from 'antd'

/** 路由或请求初始化期间共用的加载状态 */
export function LoadingState() {
  return <div className="page-state" aria-label="正在加载"><Spin size="large" /><p>正在加载…</p></div>
}

/** 带可选追踪标识的共用可重试错误状态 */
export function ErrorState({ title, description, traceId, onRetry }: { title: string; description: string; traceId?: string; onRetry: () => void }) {
  return <Result status="error" title={title} subTitle={<>{description}{traceId && <><br />关联标识：{traceId}</>}</>} extra={<Button type="primary" onClick={onRetry}>重试</Button>} />
}
