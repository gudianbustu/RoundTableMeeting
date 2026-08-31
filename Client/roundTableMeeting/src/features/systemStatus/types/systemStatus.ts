/** 系统状态功能渲染的状态值 */
export type SystemStatusViewModel = {
  status: 'available' | 'degraded'
  databaseStatus: 'available' | 'unavailable'
  environment: string
}
