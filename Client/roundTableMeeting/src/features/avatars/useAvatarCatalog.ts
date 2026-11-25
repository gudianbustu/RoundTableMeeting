import { useCallback, useEffect, useState } from 'react'
import type { AvatarChoice } from './avatar'
import { listAvatars } from './avatarApi'

export function useAvatarCatalog(active: boolean) {
  const [items, setItems] = useState<AvatarChoice[]>([])
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading')
  const [revision, setRevision] = useState(0)
  const reload = useCallback(() => setRevision(n => n + 1), [])
  useEffect(() => {
    if (!active) return
    const controller = new AbortController()
    setStatus('loading')
    void listAvatars(controller.signal).then(result => {
      if (!controller.signal.aborted) { setItems(result); setStatus('ready') }
    }).catch(() => { if (!controller.signal.aborted) setStatus('error') })
    return () => controller.abort()
  }, [active, revision])
  return { items, status, reload }
}
