import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { listMessages } from '../api/listMessages'
import { MessageNotifications } from '../chat/messageNotifications'
import type { ChatMessage } from '../types/realtimeChat'

export function useMessageNotifications(meetingId: string, participantId: string | undefined, scope: string, enabled: boolean) {
  const tracker = useMemo(() => {
    let storage: Storage | undefined
    try { storage = window.localStorage } catch { /* Optional storage. */ }
    return new MessageNotifications(meetingId, participantId ?? '', storage)
  }, [meetingId, participantId])
  const [, render] = useState(0)
  const [failed, setFailed] = useState(false)
  const activeScope = useRef(scope); activeScope.current = scope
  const runtime = useRef({ controller: new AbortController(), target: 0, running: false })
  useEffect(() => {
    runtime.current = { controller: new AbortController(), target: 0, running: false }
    const current = runtime.current
    return () => current.controller.abort()
  }, [tracker, enabled])
  const visibleScope = useCallback(() => document.visibilityState === 'hidden' ? undefined : activeScope.current, [])
  const refresh = useCallback(() => render(value => value + 1), [])
  useEffect(() => {
    const clear = () => { if (document.visibilityState !== 'hidden') { tracker.clear(activeScope.current); refresh() } }
    clear(); document.addEventListener('visibilitychange', clear)
    return () => document.removeEventListener('visibilitychange', clear)
  }, [tracker, scope, refresh])
  const synchronize = useCallback(async (through: number) => {
    if (!enabled || !participantId) return
    const current = runtime.current
    if (current.controller.signal.aborted) return
    current.target = Math.max(current.target, through)
    if (tracker.state.after === undefined) { tracker.state.after = through; tracker.save(); return }
    if (current.running) return
    current.running = true
    try {
      while (!current.controller.signal.aborted && tracker.state.after < current.target) {
        const target = current.target
        const page = await listMessages(meetingId, { scope: 'all', afterSequence: tracker.state.after, throughSequence: target, limit: 100 }, current.controller.signal)
        if (current.controller.signal.aborted) return
        for (const message of page.items) tracker.receive(message, visibleScope(), activeScope.current)
        const next = page.hasMoreAfter ? page.nextAfterSequence ?? page.lastSequence : target
        if (next === undefined || next <= tracker.state.after) throw new Error('notification_cursor_not_advanced')
        tracker.state.after = next; tracker.save(); refresh()
      }
      if (!current.controller.signal.aborted) setFailed(false)
    } catch { if (!current.controller.signal.aborted) setFailed(true) }
    finally { current.running = false }
  }, [enabled, participantId, tracker, meetingId, visibleScope, refresh])
  const receive = useCallback((message: ChatMessage) => {
    if (!enabled || !participantId || message.meetingId !== meetingId) return
    tracker.receive(message, visibleScope(), activeScope.current); refresh()
    // A live event cannot advance past a missing page; catch up the full visible stream.
    if (tracker.state.after !== undefined) void synchronize(message.sequence)
  }, [enabled, participantId, meetingId, tracker, visibleScope, refresh, synchronize])
  const remove = useCallback(() => { runtime.current.controller.abort(); tracker.remove(); refresh() }, [tracker, refresh])
  const privateUnread = (peer: string) => enabled ? tracker.unreadPrivate.get(peer)?.size ?? 0 : 0
  return { privateUnread, publicUnread: enabled ? tracker.unreadPublic.size : 0, pending: enabled ? tracker.state.pending : {}, failed, receive, synchronize, remove, retry: () => void synchronize(runtime.current.target) }
}
