import { Card, Switch } from 'antd'
import { useState } from 'react'
import { useAuthentication } from '@/features/authentication'
import { mentionPermission, requestMentionPermission, setMentionPreference, useMentionPermission, useMentionPreference } from '@/features/meetingWorkspace/chat/mentionPermission'

export function MentionNotificationSettings() {
  const { state } = useAuthentication()
  const account = state.status === 'authenticated' ? state.user.userId : undefined
  const permission = useMentionPermission()
  const enabled = useMentionPreference(account)
  const [requesting, setRequesting] = useState(false)
  const [error, setError] = useState(false)

  async function toggle(next: boolean) {
    if (!account) return
    setError(false)
    if (!next || permission === 'granted') {
      setMentionPreference(account, next)
      return
    }
    if (permission !== 'default') return
    setRequesting(true)
    try {
      await requestMentionPermission()
      if (mentionPermission() === 'granted') setMentionPreference(account, true)
    } catch { setError(true) }
    finally { setRequesting(false) }
  }

  return <Card title="提及通知">
    <label style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 16 }}>
      被 @ 时通知我
      <Switch aria-label="被 @ 时通知我" checked={enabled && permission === 'granted'} loading={requesting}
        disabled={!account || permission === 'unsupported' || permission === 'denied'} onChange={toggle} />
    </label>
    {permission === 'denied' && <p>浏览器已阻止通知，可在站点设置中更改。</p>}
    {permission === 'unsupported' && <p>当前浏览器或连接不支持系统通知。</p>}
    {error && <p role="status">浏览器无法请求通知权限，请检查站点设置。</p>}
  </Card>
}
