import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { createUuid } from '@/shared/utils/createUuid'
import { updateMeetingHistory } from '../api/updateMeetingHistory'
import { leaveMeeting } from '../api/leaveMeeting'
import { createMeeting } from '../api/createMeeting'
import { deleteMeeting } from '../api/deleteMeeting'
import { getMeetingDetails } from '../api/getMeetingDetails'
import { getMeetingInvitation } from '../api/getMeetingInvitation'
import { joinMeeting } from '../api/joinMeeting'
import { listMeetings } from '../api/listMeetings'
import { previewJoinMeeting } from '../api/previewJoinMeeting'
import type { CreateMeetingInput, JoinPreview, MeetingDetails, MeetingInvitation, MeetingListItem, MeetingWorkspaceApiError } from '../types/meetingWorkspace'

export function useMeetingWorkspace() {
  const { meetingId: routeId } = useParams()
  const activeRoute = useRef(routeId)
  activeRoute.current = routeId
  const navigate = useNavigate()
  const location = useLocation()
  const [view, setView] = useState<'active' | 'history'>('active')
  const leaveOperations = useRef(new Map<string, string>())
  const [meetings, setMeetings] = useState<MeetingListItem[]>([])
  const [details, setDetails] = useState<MeetingDetails>()
  const [invitation, setInvitation] = useState<MeetingInvitation>()
  const [loading, setLoading] = useState(true)
  const [detailLoading, setDetailLoading] = useState(false)
  const [error, setError] = useState<MeetingWorkspaceApiError>()
  const [preview, setPreview] = useState<JoinPreview>()
  const [previewLoading, setPreviewLoading] = useState(false)
  const [operationLoading, setOperationLoading] = useState(false)
  const [createSucceeded, setCreateSucceeded] = useState(false)
  const [joinSucceeded, setJoinSucceeded] = useState(false)
  const listVersion = useRef(0)
  const detailVersion = useRef(0)
  const listController = useRef<AbortController | undefined>(undefined)
  const detailController = useRef<AbortController | undefined>(undefined)
  const createdInvitation = useRef<MeetingInvitation | undefined>(undefined)
  const suppressAutoSelect = useRef(Boolean(location.state?.suppressMeetingAutoSelect))

  useEffect(() => {
    if (location.state?.suppressMeetingAutoSelect) navigate(location.pathname, { replace: true, state: null })
  }, [location.pathname, location.state, navigate])

  const loadList = useCallback(async (autoSelect = false) => {
    listController.current?.abort()
    const controller = new AbortController()
    listController.current = controller
    const version = ++listVersion.current
    setLoading(true)
    try {
      const result = await listMeetings(controller.signal, view)
      if (controller.signal.aborted || version !== listVersion.current) return
      setMeetings(result)
      if (autoSelect && !routeId && !suppressAutoSelect.current && result[0]) navigate(`/meetings/${result[0].meetingId}`, { replace: true })
    } catch (value) {
      if (!controller.signal.aborted && version === listVersion.current) setError(value as MeetingWorkspaceApiError)
    } finally {
      if (listController.current === controller) { listController.current = undefined; setLoading(false) }
    }
  }, [navigate, routeId, view])

  const loadListRef = useRef(loadList)
  loadListRef.current = loadList

  const loadDetails = useCallback(async (id: string, preserveCurrent = false) => {
    detailController.current?.abort()
    const controller = new AbortController()
    detailController.current = controller
    const version = ++detailVersion.current
    if (!preserveCurrent) { setDetails(undefined); setDetailLoading(true) }
    try {
      const result = await getMeetingDetails(id, controller.signal)
      if (!controller.signal.aborted && version === detailVersion.current) { setDetails(result); setError(undefined) }
    } catch (value) {
      if (!controller.signal.aborted && version === detailVersion.current) {
        const apiError = value as MeetingWorkspaceApiError
        setError(apiError)
        if (apiError.code === 'meeting_not_found' || apiError.status === 404) {
          suppressAutoSelect.current = true
          navigate('/', { replace: true, state: { suppressMeetingAutoSelect: true } })
          void loadListRef.current(false)
        }
      }
    } finally {
      if (detailController.current === controller) { detailController.current = undefined; setDetailLoading(false) }
    }
  }, [navigate])

  useEffect(() => { void loadList(true); return () => { listController.current?.abort() } }, [loadList])
  useEffect(() => {
    if (!routeId) { setDetails(undefined); setInvitation(undefined); return }
    const created = createdInvitation.current
    setInvitation(created?.meetingId === routeId ? created : undefined)
    void loadDetails(routeId)
    return () => detailController.current?.abort()
  }, [loadDetails, routeId, view])
  const select = (id: string) => { suppressAutoSelect.current = false; createdInvitation.current = undefined; setInvitation(undefined); navigate(`/meetings/${id}`) }
  const refresh = () => { void loadListRef.current(false); if (routeId) void loadDetails(routeId) }
  const refreshDetails = useCallback(() => {
    if (routeId && activeRoute.current === routeId) void loadDetails(routeId, true)
  }, [loadDetails, routeId])
  useEffect(() => {
    if (!routeId || details?.meetingId !== routeId || !details.canManageInvitation || invitation?.meetingId === routeId) return
    const controller = new AbortController()
    void getMeetingInvitation(routeId, controller.signal).then(value => {
      if (!controller.signal.aborted) setInvitation(value)
    }).catch(value => {
      if (!controller.signal.aborted) setError(value as MeetingWorkspaceApiError)
    })
    return () => controller.abort()
  }, [routeId, details, invitation])
  const create = async (input: CreateMeetingInput) => {
    setCreateSucceeded(false); setOperationLoading(true)
    try {
      const result = await createMeeting(input)
      createdInvitation.current = result.invitation; setInvitation(result.invitation); setDetails(result.meeting)
      setView('active'); await loadList(false); setCreateSucceeded(true); navigate(`/meetings/${result.meeting.meetingId}`)
    } catch (value) { setError(value as MeetingWorkspaceApiError); await loadList(false) } finally { setOperationLoading(false) }
  }
  const previewCode = async (code: string) => { setPreviewLoading(true); try { setPreview(await previewJoinMeeting(code.trim())); setError(undefined) } catch (value) { setPreview(undefined); setError(value as MeetingWorkspaceApiError) } finally { setPreviewLoading(false) } }
  const clearJoin = () => { setPreview(undefined); setJoinSucceeded(false); setError(undefined) }
  const join = async (code: string, displayName: string) => {
    setJoinSucceeded(false); setOperationLoading(true)
    try {
      if (preview?.alreadyJoined) { const meetingId = preview.meetingId; setPreview(undefined); setView('active'); setJoinSucceeded(true); if (meetingId === routeId) await loadDetails(meetingId); else navigate(`/meetings/${meetingId}`); return }
      let result: MeetingDetails
      try { result = await joinMeeting(code.trim(), displayName) } catch (value) {
        const apiError = value as MeetingWorkspaceApiError
        if (apiError.code !== 'meeting_join_conflict') throw value
        result = await joinMeeting(code.trim(), displayName)
      }
      setDetails(result); setPreview(undefined); setView('active'); await loadList(false); setJoinSucceeded(true); if (result.meetingId === routeId) await loadDetails(result.meetingId); else navigate(`/meetings/${result.meetingId}`)
    } catch (value) { setError(value as MeetingWorkspaceApiError) } finally { setOperationLoading(false) }
  }
  const remove = async (id: string, name: string) => {
    setOperationLoading(true)
    try { await deleteMeeting(id, name) } catch (value) {
      const apiError = value as MeetingWorkspaceApiError
      if (apiError.code !== 'meeting_not_found' && apiError.status !== 404) { setError(apiError); setOperationLoading(false); return false }
    }
    setMeetings(items => items.filter(item => item.meetingId !== id)); setDetails(undefined); setInvitation(undefined); createdInvitation.current = undefined; suppressAutoSelect.current = true
    navigate('/', { replace: true, state: { suppressMeetingAutoSelect: true } }); setOperationLoading(false); return true
  }

  const membershipChanged = useCallback(() => { void loadListRef.current(false); if (routeId) void loadDetails(routeId, true) }, [loadDetails, routeId])
  const leave = async () => {
    if (!routeId) return false
    const id = routeId
    const operationId = leaveOperations.current.get(id) ?? createUuid()
    leaveOperations.current.set(id, operationId)
    setOperationLoading(true)
    try {
      await leaveMeeting(id, operationId)
      leaveOperations.current.delete(id)
      setInvitation(undefined); createdInvitation.current = undefined
      await loadDetails(id, true); setView('history'); await loadList(false)
      return true
    } catch (value) { setError(value as MeetingWorkspaceApiError); return false }
    finally { setOperationLoading(false) }
  }

  const updateHistory = async (id: string, action: 'rejoin' | 'hide') => {
    setOperationLoading(true)
    try {
      await updateMeetingHistory(id, action)
      if (activeRoute.current !== id) { void loadListRef.current(false); return true }
      setError(undefined)
      if (action === 'rejoin') {
        setView('active')
      } else {
        setMeetings(items => items.filter(item => item.meetingId !== id))
        suppressAutoSelect.current = false
        navigate('/', { replace: true })
      }
      return true
    } catch (value) { setError(value as MeetingWorkspaceApiError); return false }
    finally { setOperationLoading(false) }
  }

  const handleMeetingUnavailable = useCallback(() => {
    suppressAutoSelect.current = true
    setDetails(undefined)
    setInvitation(undefined)
    setError(undefined)
    navigate('/', { replace: true, state: { suppressMeetingAutoSelect: true } })
    void loadListRef.current(false)
  }, [navigate])

  return { view, setView, updateHistory, leave, membershipChanged, meetings, selectedId: routeId, details, invitation, loading, detailLoading, error, preview, previewLoading, operationLoading, createSucceeded, joinSucceeded, select, refresh, refreshDetails, create, previewCode, clearJoin, join, remove, handleMeetingUnavailable, clearError: () => setError(undefined) }
}
