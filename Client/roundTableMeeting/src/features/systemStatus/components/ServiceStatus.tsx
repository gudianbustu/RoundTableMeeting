import { Badge } from 'antd'
import type { SystemStatusViewModel } from '../types/systemStatus'

/** 渲染服务总体可用性徽标 */
export function ServiceStatus({ status }: { status: SystemStatusViewModel['status'] }) {
  const available = status === 'available'
  return <Badge status={available ? 'success' : 'warning'} text={available ? '可访问' : '降级'} />
}
