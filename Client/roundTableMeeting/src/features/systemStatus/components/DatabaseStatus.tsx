import { Badge } from 'antd'

/** 渲染数据库可用性徽标 */
export function DatabaseStatus({ status }: { status: 'available' | 'unavailable' }) {
  const available = status === 'available'
  return <Badge status={available ? 'success' : 'error'} text={available ? '可访问' : '不可用'} />
}
