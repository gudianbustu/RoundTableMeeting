import { useCallback, useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
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
  const [view, setView] = useState<'active' | 'history'>('active')
  const leaveOperations = useRef(new Map<string, string>())
  const [lists, setLists] = useState<{ active: MeetingListItem[]; history: MeetingListItem[] }>({ active: [], history: [] })
  const [listReady, setListReady] = useState(false)
  const [listError, setListError] = useState<MeetingWorkspaceApiError>()
  const meetings = lists[view]
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
  const loadList = useCallback(async () => {
    listController.current?.abort()
    const controller = new AbortController()
    listController.current = controller
    const version = ++listVersion.current
    setLoading(true)
    try {
      const [active, history] = await Promise.all([listMeetings(controller.signal, 'active'), listMeetings(controller.signal, 'history')])
      if (controller.signal.aborted || version !== listVersion.current) return
      setLists({ active, history })
      setListError(undefined)
    } catch (value) {
      if (!controller.signal.aborted && version === listVersion.current) setListError(value as MeetingWorkspaceApiError)
    } finally {
      if (listController.current === controller && !controller.signal.aborted) { listController.current = undefined; setLoading(false); setListReady(true) }
    }
  }, [])

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
          navigate('/', { replace: true })
          void loadListRef.current()
        }
      }
    } finally {
      if (detailController.current === controller) { detailController.current = undefined; setDetailLoading(false) }
    }
  }, [navigate])

  useEffect(() => { void loadList(); return () => { listController.current?.abort() } }, [loadList])
  useEffect(() => {
    if (!routeId) { setDetails(undefined); setInvitation(undefined); return }
    const created = createdInvitation.current
    setInvitation(created?.meetingId === routeId ? created : undefined)
    void loadDetails(routeId)
    return () => detailController.current?.abort()
  }, [loadDetails, routeId, view])
  const select = (id: string) => { createdInvitation.current = undefined; setInvitation(undefined); navigate(`/meetings/${id}`) }
  const refresh = () => { void loadListRef.current(); if (routeId) void loadDetails(routeId) }
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
      setView('active'); await loadList(); setCreateSucceeded(true); navigate(`/meetings/${result.meeting.meetingId}`)
    } catch (value) { setError(value as MeetingWorkspaceApiError); await loadList() } finally { setOperationLoading(false) }
  }
  const previewRequest = useRef<AbortController | undefined>(undefined)
  const [joinError, setJoinError] = useState<MeetingWorkspaceApiError>()
  const clearJoin = useCallback(() => {
    previewRequest.current?.abort()
    setPreviewLoading(false)
    setPreview(undefined)
    setJoinSucceeded(false)
    setJoinError(undefined)
  }, [])
  useEffect(() => {
    clearJoin()
    return () => previewRequest.current?.abort()
  }, [routeId, clearJoin])
  const previewCode = async (code: string) => {
    clearJoin()
    const controller = new AbortController()
    previewRequest.current = controller
    setPreviewLoading(true)
    try {
      const next = await previewJoinMeeting(code.trim(), controller.signal)
      if (!controller.signal.aborted) setPreview(next)
    } catch (value) {
      if (!controller.signal.aborted) setJoinError(value as MeetingWorkspaceApiError)
    } finally {
      if (!controller.signal.aborted) setPreviewLoading(false)
    }
  }
  const join = async (code: string, displayName: string) => {
    setJoinError(undefined); setJoinSucceeded(false); setOperationLoading(true)
    try {
      if (preview?.alreadyJoined) { const meetingId = preview.meetingId; setPreview(undefined); setView('active'); setJoinSucceeded(true); if (meetingId === routeId) await loadDetails(meetingId); else navigate(`/meetings/${meetingId}`); return }
      let result: MeetingDetails
      try { result = await joinMeeting(code.trim(), displayName) } catch (value) {
        const apiError = value as MeetingWorkspaceApiError
        if (apiError.code !== 'meeting_join_conflict') throw value
        result = await joinMeeting(code.trim(), displayName)
      }
      setDetails(result); setPreview(undefined); setView('active'); await loadList(); setJoinSucceeded(true); if (result.meetingId === routeId) await loadDetails(result.meetingId); else navigate(`/meetings/${result.meetingId}`)
    } catch (value) { setJoinError(value as MeetingWorkspaceApiError) } finally { setOperationLoading(false) }
  }
  const remove = async (id: string, name: string) => {
    setOperationLoading(true)
    try { await deleteMeeting(id, name) } catch (value) {
      const apiError = value as MeetingWorkspaceApiError
      if (apiError.code !== 'meeting_not_found' && apiError.status !== 404) { setError(apiError); setOperationLoading(false); return false }
    }
    setLists(items => ({ active: items.active.filter(item => item.meetingId !== id), history: items.history.filter(item => item.meetingId !== id) })); setDetails(undefined); setInvitation(undefined); createdInvitation.current = undefined
    navigate('/', { replace: true }); setOperationLoading(false); return true
  }

  const membershipChanged = useCallback(() => { void loadListRef.current(); if (routeId) void loadDetails(routeId, true) }, [loadDetails, routeId])
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
      await loadDetails(id, true); setView('history'); await loadList()
      return true
    } catch (value) { setError(value as MeetingWorkspaceApiError); return false }
    finally { setOperationLoading(false) }
  }

  const updateHistory = async (id: string, action: 'rejoin' | 'hide') => {
    setOperationLoading(true)
    try {
      await updateMeetingHistory(id, action)
      if (activeRoute.current !== id) {
        await loadListRef.current()
        if (!activeRoute.current && action === 'rejoin') { setView('active'); navigate('/meetings/' + id) }
        return true
      }
      setError(undefined)
      if (action === 'rejoin') {
        setView('active')
        await loadList()
        await loadDetails(id, true)
      } else {
        setLists(items => ({ active: items.active.filter(item => item.meetingId !== id), history: items.history.filter(item => item.meetingId !== id) }))
        navigate('/', { replace: true })
      }
      return true
    } catch (value) { setError(value as MeetingWorkspaceApiError); return false }
    finally { setOperationLoading(false) }
  }

  const handleMeetingUnavailable = useCallback(() => {
    setDetails(undefined)
    setInvitation(undefined)
    setError(undefined)
    navigate('/', { replace: true })
    void loadListRef.current()
  }, [navigate])

  return { lists, listReady, listError, view, setView, updateHistory, leave, membershipChanged, meetings, selectedId: routeId, details, invitation, loading, detailLoading, error, joinError, preview, previewLoading, operationLoading, createSucceeded, joinSucceeded, select, refresh, refreshDetails, create, previewCode, clearJoin, join, remove, handleMeetingUnavailable, clearError: () => setError(undefined) }
}
