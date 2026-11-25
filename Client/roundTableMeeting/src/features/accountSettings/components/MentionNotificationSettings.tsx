import { Button, Card } from 'antd'
import { useState } from 'react'
import { requestMentionPermission, useMentionPermission } from '@/features/meetingWorkspace/chat/mentionPermission'

export function MentionNotificationSettings() {
  const permission = useMentionPermission()
  const [error, setError] = useState(false)
  return <Card title="提及通知">
    {permission === 'default' ? <Button onClick={() => {
      setError(false)
      void requestMentionPermission().catch(() => setError(true))
    }}>启用浏览器通知</Button> : <span>{
      permission === 'granted' ? '已允许浏览器通知' : permission === 'denied' ? '浏览器已阻止通知，可在站点设置中更改。' : '当前浏览器或连接不支持系统通知。'
    }</span>}
    {error && <p role="status">浏览器无法请求通知权限，请检查站点设置。</p>}
  </Card>
}
