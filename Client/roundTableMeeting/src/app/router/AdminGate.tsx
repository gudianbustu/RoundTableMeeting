import { useEffect, useState } from 'react'
import { Outlet } from 'react-router-dom'
import { useAuthentication } from '@/features/authentication'
import type { RefreshCurrentUserResult } from '@/features/authentication/types/authentication'
import { ErrorState, LoadingState } from '@/shared/components/PageState'

/** 首次准入绑定完整刷新结果；准入后只由身份失效撤销。 */
export function AdminGate() {
  const { state, refreshCurrentUser, identityRevision, refreshRevision } = useAuthentication()
  const [attempt, setAttempt] = useState(0)
  const [candidate, setCandidate] = useState<RefreshCurrentUserResult>()
  const [admitted, setAdmitted] = useState<number>()
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    let active = true
    setCandidate(undefined); setFailed(false); setAdmitted(undefined)
    void refreshCurrentUser().then(result => {
      if (active) { setCandidate(result); setFailed(result.status === 'cancelled') }
    }).catch(() => { if (active) setFailed(true) })
    return () => { active = false }
  }, [attempt, refreshCurrentUser])
  useEffect(() => {
    if (candidate?.status === 'refreshed' && candidate.user.isAdmin
      && candidate.identityRevision === identityRevision && candidate.refreshRevision === refreshRevision) {
      setAdmitted(identityRevision)
    }
  }, [candidate, identityRevision, refreshRevision])
  if (state.status === 'authenticated' && !state.user.isAdmin)
    return <div role="alert">无管理权限：仅管理员可以访问此页面。</div>
  if (admitted === identityRevision) return <Outlet />
  if (failed || candidate || admitted !== undefined)
    return <ErrorState title="无法确认管理权限" description="请重新确认后继续。" onRetry={() => setAttempt(value => value + 1)} />
  return <LoadingState />
}
