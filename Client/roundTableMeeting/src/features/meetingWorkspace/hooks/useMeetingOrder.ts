import { useSyncExternalStore } from 'react'
import { arrayMove } from '@dnd-kit/sortable'
import type { MeetingListItem } from '../types/meetingWorkspace'

const changed = 'roundtable:meeting-order'
function subscribe(listener: () => void) {
  window.addEventListener(changed, listener)
  window.addEventListener('storage', listener)
  return () => {
    window.removeEventListener(changed, listener)
    window.removeEventListener('storage', listener)
  }
}

export function useMeetingOrder(meetings: MeetingListItem[], owner?: string) {
  const key = 'roundtable.meetingOrder.' + owner
  const stored = useSyncExternalStore(subscribe, () => {
    try { return owner ? localStorage.getItem(key) : null } catch { return null }
  })
  let ordered = meetings
  if (stored) {
    try {
      const ids: unknown = JSON.parse(stored)
      if (Array.isArray(ids)) {
        const remaining = new Map(meetings.map(meeting => [meeting.meetingId, meeting]))
        ordered = []
        for (const id of ids) {
          const meeting = remaining.get(id)
          if (meeting) { ordered.push(meeting); remaining.delete(id) }
        }
        ordered.push(...remaining.values())
      }
    } catch { /* Ignore malformed preferences. */ }
  }
  function move(from: string, target: string) {
    if (!owner) return
    const ids = ordered.map(meeting => meeting.meetingId)
    const start = ids.indexOf(from), end = ids.indexOf(target)
    if (start < 0 || end < 0 || start === end) return
    try {
      localStorage.setItem(key, JSON.stringify(arrayMove(ids, start, end)))
      window.dispatchEvent(new Event(changed))
    } catch { /* Storage can be disabled by the browser. */ }
  }
  return { ordered, move }
}
