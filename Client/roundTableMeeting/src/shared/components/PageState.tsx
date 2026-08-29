import { Button, Result, Spin } from 'antd'

export function LoadingState() {
  return <div className="page-state" aria-label="正在加载"><Spin size="large" /><p>正在检查系统状态…</p></div>
}

export function ErrorState({ title, description, traceId, onRetry }: { title: string; description: string; traceId?: string; onRetry: () => void }) {
  return <Result status="error" title={title} subTitle={<>{description}{traceId && <><br />关联标识：{traceId}</>}</>} extra={<Button type="primary" onClick={onRetry}>重试</Button>} />
}
