import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useAuthentication } from '@/features/authentication'
import { subscribeAccountActivity } from '../api/accountActivityConnection'
import { AccountUnreadStore } from './accountUnreadStore'

const UnreadContext = createContext<{ store: AccountUnreadStore; revision: number } | null | undefined>(undefined)
export const useAccountUnread = () => useContext(UnreadContext)

export function AccountUnreadProvider({ children }: { children: ReactNode }) {
  const { state } = useAuthentication()
  const account = state.status === 'authenticated' ? state.user.userId : undefined
  const [store, setStore] = useState<AccountUnreadStore>()
  const [revision, setRevision] = useState(0)
  useEffect(() => {
    if (!account) { setStore(undefined); return }
    const instance = new AccountUnreadStore(account, () => setRevision(n => n + 1))
    setStore(instance)
    const unsubscribe = subscribeAccountActivity(account, {
      connected: () => instance.refreshAll(),
      meetingActivityChanged: value => { if (typeof value?.meetingId === 'string') instance.refresh(value.meetingId) },
    })
    const storage = (event: StorageEvent) => instance.changedInOtherTab(event.key)
    const refresh = () => instance.refreshAll()
    window.addEventListener('storage', storage)
    window.addEventListener('focus', refresh)
    return () => {
      instance.dispose(); unsubscribe()
      window.removeEventListener('storage', storage); window.removeEventListener('focus', refresh)
    }
  }, [account])
  const value = useMemo(() => store && account && store.prefix === 'rtm-account-unread-v1:' + account + ':' ? { store, revision } : null, [account, store, revision])
  return <UnreadContext.Provider value={value}>{children}</UnreadContext.Provider>
}
