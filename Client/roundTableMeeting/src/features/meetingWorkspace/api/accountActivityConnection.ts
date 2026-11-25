import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

type Listener = { connected?: () => void; humanMentioned?: (value: any) => void; meetingActivityChanged?: (value: any) => void }
function connect() {
  const listeners = new Set<Listener>()
  const hub = new HubConnectionBuilder().withUrl(new URL('/hubs/mention-notifications', window.location.href).href, { withCredentials: true })
    .withAutomaticReconnect().configureLogging(LogLevel.Warning).build()
  let stopped = false, retry: ReturnType<typeof setTimeout> | undefined
  const ready = async () => {
    if (stopped) return
    try {
      if ([...listeners].some(listener => listener.meetingActivityChanged)) await hub.invoke('SubscribeMeetingActivity')
      if (!stopped) listeners.forEach(listener => listener.connected?.())
    } catch {
      if (!stopped) await hub.stop()
    }
  }
  const schedule = () => {
    if (!stopped && !retry) retry = setTimeout(() => { retry = undefined; start() }, 5000)
  }
  const start = () => { if (!stopped) void hub.start().then(ready, schedule) }
  hub.on('humanMentioned', value => listeners.forEach(listener => listener.humanMentioned?.(value)))
  hub.on('meetingActivityChanged', value => listeners.forEach(listener => listener.meetingActivityChanged?.(value)))
  hub.onreconnected(ready)
  hub.onclose(schedule)
  start()
  return { listeners, stop() { stopped = true; clearTimeout(retry); void hub.stop().catch(() => {}) } }
}
const connections = new Map<string, ReturnType<typeof connect>>()
export function subscribeAccountActivity(account: string, listener: Listener) {
  const connection = connections.get(account) ?? connect()
  connections.set(account, connection)
  connection.listeners.add(listener)
  return () => {
    connection.listeners.delete(listener)
    if (!connection.listeners.size) { connections.delete(account); connection.stop() }
  }
}
