import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
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
  const navigate = useNavigate()
  const location = useLocation()
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
      const result = await listMeetings(controller.signal)
      if (controller.signal.aborted || version !== listVersion.current) return
      setMeetings(result)
      if (autoSelect && !routeId && !suppressAutoSelect.current && result[0]) navigate(`/meetings/${result[0].meetingId}`, { replace: true })
    } catch (value) {
      if (!controller.signal.aborted && version === listVersion.current) setError(value as MeetingWorkspaceApiError)
    } finally {
      if (listController.current === controller) { listController.current = undefined; setLoading(false) }
    }
  }, [navigate, routeId])

  const loadDetails = useCallback(async (id: string) => {
    detailController.current?.abort()
    const controller = new AbortController()
    detailController.current = controller
    const version = ++detailVersion.current
    setDetails(undefined)
    setDetailLoading(true)
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
          void loadList(false)
        }
      }
    } finally {
      if (detailController.current === controller) { detailController.current = undefined; setDetailLoading(false) }
    }
  }, [loadList, navigate])

  useEffect(() => { void loadList(true); return () => { listController.current?.abort(); detailController.current?.abort() } }, [loadList])
  useEffect(() => {
    if (!routeId) { setDetails(undefined); setInvitation(undefined); return }
    const created = createdInvitation.current
    setInvitation(created?.meetingId === routeId ? created : undefined)
    void loadDetails(routeId)
  }, [loadDetails, routeId])
  useEffect(() => {
    const onFocus = () => { if (routeId) void loadDetails(routeId) }
    window.addEventListener('focus', onFocus)
    return () => window.removeEventListener('focus', onFocus)
  }, [loadDetails, routeId])

  const select = (id: string) => { suppressAutoSelect.current = false; createdInvitation.current = undefined; setInvitation(undefined); navigate(`/meetings/${id}`) }
  const refresh = () => { void loadList(false); if (routeId) void loadDetails(routeId) }
  const revealInvitation = async () => { if (!routeId) return; setOperationLoading(true); try { setInvitation(await getMeetingInvitation(routeId)) } catch (value) { setError(value as MeetingWorkspaceApiError) } finally { setOperationLoading(false) } }
  const hideInvitation = () => { createdInvitation.current = undefined; setInvitation(undefined) }
  const create = async (input: CreateMeetingInput) => {
    setCreateSucceeded(false); setOperationLoading(true)
    try {
      const result = await createMeeting(input)
      createdInvitation.current = result.invitation; setInvitation(result.invitation); setDetails(result.meeting)
      await loadList(false); setCreateSucceeded(true); navigate(`/meetings/${result.meeting.meetingId}`)
    } catch (value) { setError(value as MeetingWorkspaceApiError); await loadList(false) } finally { setOperationLoading(false) }
  }
  const previewCode = async (code: string) => { setPreviewLoading(true); try { setPreview(await previewJoinMeeting(code.trim())); setError(undefined) } catch (value) { setPreview(undefined); setError(value as MeetingWorkspaceApiError) } finally { setPreviewLoading(false) } }
  const clearJoin = () => { setPreview(undefined); setJoinSucceeded(false); setError(undefined) }
  const join = async (code: string, displayName: string) => {
    setJoinSucceeded(false); setOperationLoading(true)
    try {
      if (preview?.alreadyJoined) { const meetingId = preview.meetingId; setPreview(undefined); setJoinSucceeded(true); navigate(`/meetings/${meetingId}`); return }
      let result: MeetingDetails
      try { result = await joinMeeting(code.trim(), displayName) } catch (value) {
        const apiError = value as MeetingWorkspaceApiError
        if (apiError.code !== 'meeting_join_conflict') throw value
        result = await joinMeeting(code.trim(), displayName)
      }
      setDetails(result); setPreview(undefined); await loadList(false); setJoinSucceeded(true); navigate(`/meetings/${result.meetingId}`)
    } catch (value) { setError(value as MeetingWorkspaceApiError) } finally { setOperationLoading(false) }
  }
  const remove = async (id: string, name: string) => {
    setOperationLoading(true)
    try { await deleteMeeting(id, name) } catch (value) {
      const apiError = value as MeetingWorkspaceApiError
      if (apiError.code !== 'meeting_not_found' && apiError.status !== 404) { setError(apiError); setOperationLoading(false); return false }
    }
    setMeetings(items => items.filter(item => item.meetingId !== id)); setDetails(undefined); hideInvitation(); suppressAutoSelect.current = true
    navigate('/', { replace: true, state: { suppressMeetingAutoSelect: true } }); setOperationLoading(false); return true
  }

  const handleMeetingUnavailable = useCallback(() => {
    suppressAutoSelect.current = true
    setDetails(undefined)
    setInvitation(undefined)
    setError(undefined)
    navigate('/', { replace: true, state: { suppressMeetingAutoSelect: true } })
    void loadList(false)
  }, [loadList, navigate])

  return { meetings, selectedId: routeId, details, invitation, loading, detailLoading, error, preview, previewLoading, operationLoading, createSucceeded, joinSucceeded, select, refresh, revealInvitation, hideInvitation, create, previewCode, clearJoin, join, remove, handleMeetingUnavailable, clearError: () => setError(undefined) }
}
