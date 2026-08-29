export type SystemStatusViewModel = {
  status: 'available' | 'degraded'
  databaseStatus: 'available' | 'unavailable'
  environment: string
}
