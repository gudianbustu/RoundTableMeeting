import { Alert, Button } from 'antd'
import type { ConnectionState, HistoryState } from '../types/realtimeChat'
export function ChatConnectionStatus({ connection, history, error, onRetry }: { connection: ConnectionState; history: HistoryState; error?: string; onRetry: () => void }) {
  if (error) return <Alert type="error" showIcon title="消息暂时不可用" description={error} action={<Button size="small" onClick={onRetry}>重试</Button>} />
  if (connection === 'rejected') return <Alert type="error" showIcon title="会议已不可访问" />
  if (connection === 'reconnecting') return <Alert type="warning" showIcon title="连接正在恢复" />
  if (connection === 'disconnected') return <Alert type="warning" showIcon title="连接已断开" action={<Button size="small" onClick={onRetry}>重新连接</Button>} />
  if (connection === 'connecting' || history === 'loading') return <Alert type="info" showIcon title="正在连接会议" />
  if (history === 'appending') return <Alert type="info" showIcon title="正在同步新消息" />
  return null
}
