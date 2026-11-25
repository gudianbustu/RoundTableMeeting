import { useEffect, useEffectEvent, useLayoutEffect } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { subscribeAccountActivity } from '../api/accountActivityConnection'
import { useAuthentication } from '@/features/authentication'
import { mentionPreference, useMentionPreference, useMentionPermission } from './mentionPermission'

const guid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i
const foregroundLock = (account: string, meeting: string) => 'rtm-mention-view:' + account + ':' + meeting

// A foreground tab holds a shared lock for its visible meeting. Closing or crashing
// releases the browser-owned lock without leaving stale presence in local storage.
function trackForeground(account: string, meeting: string) {
  const stop = new AbortController()
  let presence: AbortController | undefined
  const release = () => { presence?.abort(); presence = undefined }
  function update() {
    if (document.visibilityState !== 'visible' || !document.hasFocus()) { release(); return }
    if (presence) return
    const owner = new AbortController()
    presence = owner
    void navigator.locks.request(foregroundLock(account, meeting), { mode: 'shared', signal: owner.signal },
      () => new Promise<void>(resolve => {
        if (owner.signal.aborted) resolve()
        else owner.signal.addEventListener('abort', () => resolve(), { once: true })
      })).catch(() => {})
  }
  for (const event of ['focus', 'blur', 'pageshow']) window.addEventListener(event, update, { signal: stop.signal })
  window.addEventListener('pagehide', release, { signal: stop.signal })
  document.addEventListener('visibilitychange', update, { signal: stop.signal })
  update()
  return () => { stop.abort(); release() }
}

export function MentionNotificationListener() {
  const { state } = useAuthentication()
  const account = state.status === 'authenticated' ? state.user.userId : undefined
  const { pathname } = useLocation()
  const meeting = pathname.match(/^\/meetings\/([^/]+)\/?$/i)?.[1]?.toLowerCase()
  const navigate = useNavigate()
  const openMeeting = useEffectEvent((id: string) => { window.focus(); void navigate('/meetings/' + id) })
  const permission = useMentionPermission()
  const enabled = useMentionPreference(account)

  useLayoutEffect(() => {
    if (navigator.locks && account && meeting && guid.test(meeting)) return trackForeground(account, meeting)
  }, [account, meeting])

  useEffect(() => {
    if (!account || !enabled || permission !== 'granted') return
    let active = true
    const shown = new Set<Notification>()
    const recentKey = 'rtm-mention-seen:' + account
    async function receive(value: { meetingId?: string; messageId?: string } | null) {
      const room = value?.meetingId, message = value?.messageId
      if (!active || !room || !message || !guid.test(room) || !guid.test(message)) return
      const id = room.toLowerCase() + '/' + message.toLowerCase()
      await navigator.locks.request(recentKey, async () => {
        if (!active || Notification.permission !== 'granted') return
        let recent: string[] = []
        try {
          const stored: unknown = JSON.parse(localStorage.getItem(recentKey) ?? '[]')
          if (Array.isArray(stored)) recent = stored.filter(item => typeof item === 'string')
        } catch { /* Use the browser notification tag if storage is unavailable. */ }
        if (recent.includes(id)) return
        await navigator.locks.request(foregroundLock(account!, room.toLowerCase()), { ifAvailable: true }, lock => {
          if (!active || !mentionPreference(account) || Notification.permission !== 'granted') return
          if (lock) {
            const notice = new Notification('圆桌会议', { body: '有人在会议中提到了你。', tag: recentKey + '/' + id })
            shown.add(notice)
            notice.onclose = () => shown.delete(notice)
            notice.onclick = () => { if (active) openMeeting(room); notice.close() }
          }
          try { localStorage.setItem(recentKey, JSON.stringify([...recent.slice(-255), id])) } catch { /* Storage is optional. */ }
        })
      })
    }
    const unsubscribe = subscribeAccountActivity(account, { humanMentioned: value => { void receive(value).catch(() => {}) } })
    return () => {
      active = false
      unsubscribe()
      for (const notice of shown) notice.close()
    }
  }, [account, permission, enabled])
  return null
}
