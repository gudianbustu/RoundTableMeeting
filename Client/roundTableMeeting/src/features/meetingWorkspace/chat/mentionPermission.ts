import { useSyncExternalStore } from 'react'

export type MentionPermission = NotificationPermission | 'unsupported'
export function mentionPermission(): MentionPermission {
  return window.isSecureContext && 'Notification' in window && navigator.locks
    ? Notification.permission : 'unsupported'
}

const changed = 'rt-mention-notification-permission'
export async function requestMentionPermission() {
  if (mentionPermission() !== 'default') return
  try { await Notification.requestPermission() } finally { window.dispatchEvent(new Event(changed)) }
}

function subscribe(update: () => void) {
  const stop = new AbortController()
  window.addEventListener(changed, update, { signal: stop.signal })
  window.addEventListener('focus', update, { signal: stop.signal })
  document.addEventListener('visibilitychange', update, { signal: stop.signal })
  void navigator.permissions?.query({ name: 'notifications' }).then(status => {
    if (!stop.signal.aborted) {
      status.addEventListener('change', update, { signal: stop.signal })
      update()
    }
  }).catch(() => {})
  return () => stop.abort()
}

export function useMentionPermission() {
  return useSyncExternalStore(subscribe, mentionPermission)
}
