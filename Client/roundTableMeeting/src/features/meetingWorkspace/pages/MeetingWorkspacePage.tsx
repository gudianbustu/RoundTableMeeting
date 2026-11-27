import { useAuthentication } from '@/features/authentication'
import { AvatarImage } from '@/features/avatars/AvatarImage'
import { useAccountUnread } from '../chat/AccountUnreadProvider'
import './MeetingWorkspaceShell.css'
import { createPortal } from 'react-dom'
import { ArrowLeftOutlined, CloseOutlined, DownloadOutlined, ExpandOutlined, FolderOpenOutlined, FullscreenExitOutlined, FullscreenOutlined, InfoCircleOutlined, MenuOutlined, PictureOutlined, TeamOutlined } from '@ant-design/icons'
import { useLocation, useNavigate } from 'react-router-dom'
import type { MessageComposerHandle } from '../components/MessageComposer'
import { Alert, Button, Drawer, Flex, Input, Modal, Typography } from 'antd'
import { useEffect, useRef, useState, type CSSProperties, type KeyboardEvent, type PointerEvent } from 'react'
import { getApiClient } from '@/api/client/apiClient'
import { createUuid } from '@/shared/utils/createUuid'
import { meetingError } from '../api/helpers'
import { MeetingInformationPanel } from '../components/MeetingInformationDialog'
import { MeetingNicknameDialog } from '../components/MeetingNicknameDialog'
import { MeetingNavigation } from '../components/MeetingNavigation'
import { DiscussionModeControl } from '../components/DiscussionModeControl'
import { attachmentUrl } from '../api/messageResources'
import { MeetingAttachmentPanel } from '../components/AttachmentControls'
import { MessageBody } from '../components/MessageBody'
import TextAttachmentPreview from '../components/TextAttachmentPreview'
import { ParticipantList } from '../components/ParticipantList'
import { CreateMeetingDialog } from '../components/CreateMeetingDialog'
import { InviteMeetingDialog } from '../components/InviteMeetingDialog'
import { JoinMeetingDialog, meetingCodePattern } from '../components/JoinMeetingDialog'
import { useMeetingWorkspace } from '../hooks/useMeetingWorkspace'
import type { AgentActivity, ChatAttachment, ChatMessage } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'
import { RealtimeChatRegion, type ActiveChatScopeContext, type ChatSessionCache } from './RealtimeChatRegion'

const RIGHT_PANEL_MIN = 280
const RIGHT_PANEL_MAX = 520
const RIGHT_PANEL_DEFAULT = 330
const READER_PANEL_MIN = 360
const READER_PANEL_DEFAULT = 520
const READER_PANEL_HARD_MAX = 960
const READER_MODAL_MIN_WIDTH = 560
const READER_MODAL_AUTO_MAX_WIDTH = 1680
const WORKSPACE_SIDEBAR_WIDTH = 236
const CHAT_COLUMN_MIN = 380
const rightPanelWidthKey = 'rtm-right-panel-width-v1'
const readerPanelWidthKey = 'rtm-reader-panel-width-v1'
const readerModalWidthKey = 'rtm-reader-modal-width-v1'

function computeReaderMaxWidth(viewportWidth: number) {
  const available = viewportWidth - WORKSPACE_SIDEBAR_WIDTH - CHAT_COLUMN_MIN
  return Math.max(READER_PANEL_MIN, Math.min(READER_PANEL_HARD_MAX, Math.round(available)))
}

function computeReaderModalBounds(viewportWidth: number) {
  const margin = viewportWidth <= 640 ? 16 : 48
  const max = Math.max(320, viewportWidth - margin)
  const min = Math.min(READER_MODAL_MIN_WIDTH, max)
  const preferred = Math.round(viewportWidth * 0.8)
  const fallback = Math.min(max, Math.max(min, Math.min(READER_MODAL_AUTO_MAX_WIDTH, preferred)))
  return { min, max, fallback }
}

function readStoredWidth(key: string, min: number, max: number, fallback: number) {
  try {
    const value = Number(localStorage.getItem(key))
    return Number.isSafeInteger(value) && value >= min && value <= max ? value : fallback
  } catch { return fallback }
}

function readOptionalStoredWidth(key: string, min: number) {
  try {
    const raw = localStorage.getItem(key)
    if (!raw) return undefined
    const value = Number(raw)
    return Number.isSafeInteger(value) && value >= min ? value : undefined
  } catch { return undefined }
}

export function hasUnknownOnlineParticipant(participants: Participant[], presence: Record<string, 'online' | 'offline'>) {
  const participantIds = new Set(participants.filter(p => p.membershipState !== 'left').map(p => p.participantId))
  return Object.entries(presence).some(([participantId, state]) => state === 'online' && !participantIds.has(participantId))
}

export function hasUnknownAgentActivity(participants: Participant[], activities: Record<string, AgentActivity>) {
  const participantIds = new Set(participants.map(participant => participant.participantId))
  return Object.values(activities).some(activity => !participantIds.has(activity.participantId))
}

export function MeetingWorkspacePage() {
  const { state: identity } = useAuthentication()
  const workspace = useMeetingWorkspace()
  const unread = useAccountUnread()
  const unreadStore = unread?.store
  useEffect(() => { if (workspace.listReady) unreadStore?.list(workspace.lists.active.map(item => item.meetingId)) }, [unreadStore, workspace.listReady, workspace.lists.active])
  const unreadCounts: Record<string, number> = {}
  for (const [id, meeting] of unreadStore?.meetings ?? []) {
    unreadCounts[id] = Object.values(meeting.scopes).reduce((sum, scope) => sum + scope.count, 0)
  }
  const navigate = useNavigate()
  const location = useLocation()
  const invitedCode = new URLSearchParams(location.hash.slice(1)).get('join') ?? ''
  const previewCode = workspace.previewCode
  useEffect(() => {
    if (meetingCodePattern.test(invitedCode)) void previewCode(invitedCode)
  }, [invitedCode, previewCode])
  const chatCache = useRef<ChatSessionCache>({ states: new Map(), selection: {} })
  const composerRef = useRef<MessageComposerHandle>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [joinOpen, setJoinOpen] = useState(false)
  const meetingName = workspace.details?.name
  useEffect(() => {
    if (!meetingName) return
    const previousTitle = document.title
    document.title = meetingName + ' - 圆桌会议'
    return () => { document.title = previousTitle }
  }, [meetingName])
  const [inviteOpen, setInviteOpen] = useState(false)
  const [nicknameOpen, setNicknameOpen] = useState(false)
  const [removal, setRemoval] = useState<{ member: Participant; meetingId: string; operationId: string }>()
  const [removing, setRemoving] = useState(false)
  const [removalError, setRemovalError] = useState('')
  const removeMember = async () => {
    if (!removal || removing) return
    setRemoving(true)
    try {
      await getApiClient().api.meetings.byMeetingId(removal.meetingId as never).participants
        .byParticipantId(removal.member.participantId as never).remove.post({ operationId: removal.operationId as never })
      setRemoval(undefined)
      workspace.membershipChanged()
    } catch (reason) { setRemovalError(meetingError(reason).message) }
    finally { setRemoving(false) }
  }
  const [wideDesktop, setWideDesktop] = useState(() => typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(min-width: 1200px)').matches : false)
  const [rightPanel, setRightPanel] = useState<'info' | 'members' | 'files' | 'images' | 'reader' | null>(() =>
    typeof window !== 'undefined' && typeof window.matchMedia === 'function' && window.matchMedia('(min-width: 1200px)').matches ? 'members' : null)
  const [renderedPanel, setRenderedPanel] = useState<'info' | 'members' | 'files' | 'images' | 'reader'>('members')
  const [readerTarget, setReaderTarget] = useState<
    | { kind: 'message'; message: ChatMessage; returnTab: 'info' | 'members' | 'files' | 'images' }
    | { kind: 'markdownFile'; meetingId: string; file: ChatAttachment; author?: string; createdAt?: Date; returnTab: 'info' | 'members' | 'files' | 'images' }
  >()
  const [readerExpanded, setReaderExpanded] = useState(false)
  const [readerModalFullscreen, setReaderModalFullscreen] = useState(false)
  useEffect(() => { if (rightPanel) setRenderedPanel(rightPanel) }, [rightPanel])
  const [viewportWidth, setViewportWidth] = useState(() => typeof window !== 'undefined' ? window.innerWidth : 1280)
  useEffect(() => {
    if (typeof window === 'undefined') return
    const sync = () => setViewportWidth(window.innerWidth)
    window.addEventListener('resize', sync)
    return () => window.removeEventListener('resize', sync)
  }, [])
  const readerMaxWidth = computeReaderMaxWidth(viewportWidth)
  const readerModalBounds = computeReaderModalBounds(viewportWidth)
  const [readerModalCustomWidth, setReaderModalCustomWidth] = useState<number | undefined>(() => readOptionalStoredWidth(readerModalWidthKey, READER_MODAL_MIN_WIDTH))
  const [resizingReaderModal, setResizingReaderModal] = useState(false)
  const modalDrag = useRef<{ pointer: number; edge: 'left' | 'right'; startX: number; startWidth: number; lastWidth?: number } | undefined>(undefined)
  const clampReaderModalWidth = (value: number) => Math.max(readerModalBounds.min, Math.min(readerModalBounds.max, Math.round(value)))
  const activeReaderModalWidth = readerModalCustomWidth !== undefined ? clampReaderModalWidth(readerModalCustomWidth) : readerModalBounds.fallback
  const saveReaderModalWidth = (value: number) => {
    const next = clampReaderModalWidth(value)
    setReaderModalCustomWidth(next)
    try { localStorage.setItem(readerModalWidthKey, String(next)) } catch { /* ignore storage failure */ }
  }
  const resetReaderModalWidth = () => {
    setReaderModalCustomWidth(undefined)
    try { localStorage.removeItem(readerModalWidthKey) } catch { /* ignore storage failure */ }
  }
  const startModalResize = (edge: 'left' | 'right') => (event: PointerEvent<HTMLDivElement>) => {
    if (!event.isPrimary || event.button !== 0 || readerModalFullscreen) return
    event.currentTarget.setPointerCapture(event.pointerId)
    modalDrag.current = { pointer: event.pointerId, edge, startX: event.clientX, startWidth: activeReaderModalWidth }
    setResizingReaderModal(true)
  }
  const moveModalResize = (event: PointerEvent<HTMLDivElement>) => {
    const active = modalDrag.current
    if (active?.pointer !== event.pointerId) return
    const delta = active.edge === 'right' ? (event.clientX - active.startX) * 2 : (active.startX - event.clientX) * 2
    active.lastWidth = clampReaderModalWidth(active.startWidth + delta)
    setReaderModalCustomWidth(active.lastWidth)
  }
  const finishModalResize = () => {
    if (modalDrag.current?.lastWidth !== undefined) saveReaderModalWidth(modalDrag.current.lastWidth)
    modalDrag.current = undefined
    setResizingReaderModal(false)
  }
  const keydownModalResize = (event: KeyboardEvent<HTMLDivElement>) => {
    const map: Record<string, number> = {
      ArrowLeft: activeReaderModalWidth - 32,
      ArrowRight: activeReaderModalWidth + 32,
      Home: readerModalBounds.min,
      End: readerModalBounds.max,
    }
    if (!Object.hasOwn(map, event.key)) return
    event.preventDefault()
    saveReaderModalWidth(map[event.key])
  }
  const closeReaderExpanded = () => { setReaderExpanded(false); setReaderModalFullscreen(false) }
  const [rightPanelWidth, setRightPanelWidth] = useState(() => readStoredWidth(rightPanelWidthKey, RIGHT_PANEL_MIN, RIGHT_PANEL_MAX, RIGHT_PANEL_DEFAULT))
  const [readerPanelWidth, setReaderPanelWidth] = useState(() => readStoredWidth(readerPanelWidthKey, READER_PANEL_MIN, READER_PANEL_HARD_MAX, READER_PANEL_DEFAULT))
  const [resizingRightPanel, setResizingRightPanel] = useState(false)
  const rightDrag = useRef<{ pointer: number; startX: number; startWidth: number; isReader: boolean; lastWidth?: number } | undefined>(undefined)
  const isReaderPanel = (rightPanel ?? renderedPanel) === 'reader'
  const activePanelMin = isReaderPanel ? READER_PANEL_MIN : RIGHT_PANEL_MIN
  const activePanelMax = isReaderPanel ? readerMaxWidth : RIGHT_PANEL_MAX
  const clampPanelWidth = (value: number, forReader: boolean) => {
    const min = forReader ? READER_PANEL_MIN : RIGHT_PANEL_MIN
    const max = forReader ? readerMaxWidth : RIGHT_PANEL_MAX
    return Math.max(min, Math.min(max, Math.round(value)))
  }
  const activePanelWidth = isReaderPanel ? clampPanelWidth(readerPanelWidth, true) : rightPanelWidth
  const saveActivePanelWidth = (value: number, forReader = isReaderPanel) => {
    const next = clampPanelWidth(value, forReader)
    if (forReader) setReaderPanelWidth(next)
    else setRightPanelWidth(next)
    try { localStorage.setItem(forReader ? readerPanelWidthKey : rightPanelWidthKey, String(next)) } catch { /* ignore storage failure */ }
  }
  const startRightResize = (event: PointerEvent<HTMLDivElement>) => {
    if (!event.isPrimary || event.button !== 0) return
    event.currentTarget.setPointerCapture(event.pointerId)
    rightDrag.current = { pointer: event.pointerId, startX: event.clientX, startWidth: activePanelWidth, isReader: isReaderPanel }
    setResizingRightPanel(true)
  }
  const moveRightResize = (event: PointerEvent<HTMLDivElement>) => {
    const active = rightDrag.current
    if (active?.pointer !== event.pointerId) return
    active.lastWidth = clampPanelWidth(active.startWidth + (active.startX - event.clientX), active.isReader)
    if (active.isReader) setReaderPanelWidth(active.lastWidth)
    else setRightPanelWidth(active.lastWidth)
  }
  const finishRightResize = () => {
    if (rightDrag.current?.lastWidth !== undefined) saveActivePanelWidth(rightDrag.current.lastWidth, rightDrag.current.isReader)
    rightDrag.current = undefined
    setResizingRightPanel(false)
  }
  const keydownRightResize = (event: KeyboardEvent<HTMLDivElement>) => {
    const map: Record<string, number> = {
      ArrowLeft: activePanelWidth + 16,
      ArrowRight: activePanelWidth - 16,
      Home: activePanelMin,
      End: activePanelMax,
    }
    if (!Object.hasOwn(map, event.key)) return
    event.preventDefault()
    saveActivePanelWidth(map[event.key], isReaderPanel)
  }
  const [activeScope, setActiveScope] = useState<ActiveChatScopeContext>()
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return
    const query = window.matchMedia('(min-width: 1200px)')
    const sync = () => setWideDesktop(query.matches)
    sync()
    query.addEventListener?.('change', sync)
    return () => query.removeEventListener?.('change', sync)
  }, [])
  const [navigationOpen, setNavigationOpen] = useState(false)
  const [navigationTrigger, setNavigationTrigger] = useState<HTMLElement | null>(null)
  useEffect(() => { setNavigationTrigger(document.getElementById('workspace-navigation-trigger')) }, [workspace.selectedId])
  const [leaveOpen, setLeaveOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [hideId, setHideId] = useState<string>()
  const [confirmName, setConfirmName] = useState('')
  const [presence, setPresence] = useState<Record<string, 'online' | 'offline'>>({})
  const [agentActivities, setAgentActivities] = useState<Record<string, AgentActivity>>({})
  useEffect(() => { if (workspace.createSucceeded) setCreateOpen(false) }, [workspace.createSucceeded])
  useEffect(() => { if (workspace.joinSucceeded) setJoinOpen(false) }, [workspace.joinSucceeded])
  useEffect(() => {
    setRemoval(undefined); setNicknameOpen(false); setNavigationOpen(false)
    setRightPanel(wideDesktop ? 'members' : null); setActiveScope(undefined); setReaderTarget(undefined); closeReaderExpanded()
    setHideId(undefined); setPresence({}); setAgentActivities({})
    setCreateOpen(false); setJoinOpen(false)
    setInviteOpen(false); setDeleteOpen(false); setLeaveOpen(false); setConfirmName('')
  }, [workspace.selectedId])
  useEffect(() => {
    if (workspace.details && hasUnknownOnlineParticipant(workspace.details.participants, presence)) workspace.refreshDetails()
  }, [presence, workspace.details?.participants, workspace.refreshDetails])
  useEffect(() => {
    if (workspace.details && hasUnknownAgentActivity(workspace.details.participants, agentActivities)) workspace.refreshDetails()
  }, [agentActivities, workspace.details?.participants, workspace.refreshDetails])
  const openInvite = () => setInviteOpen(true)
  const closeDelete = () => { setDeleteOpen(false); setConfirmName('') }
  const historical = workspace.details ? workspace.details.membershipState === 'left'
    : workspace.lists.history.some(meeting => meeting.meetingId === workspace.selectedId) || workspace.view === 'history'
  const listError = workspace.listError && <Alert type="error" showIcon title={workspace.listError.message}
    action={<Button size="small" loading={workspace.loading} onClick={workspace.refresh}>重试加载会议列表</Button>} />
  const navigation = (
    <MeetingNavigation orderOwner={identity.status === 'authenticated' ? identity.user.userId : undefined} unreadCounts={unreadCounts} details={workspace.details} detailLoading={workspace.detailLoading} operationLoading={workspace.operationLoading} onRejoin={id => void workspace.updateHistory(id, 'rejoin')} onHide={id => { setNavigationOpen(false); setHideId(id) }} onLeave={() => { setNavigationOpen(false); setLeaveOpen(true) }} onDelete={() => { setNavigationOpen(false); setDeleteOpen(true) }} meetings={workspace.lists.active} historyMeetings={workspace.lists.history} selectedId={workspace.selectedId} loading={workspace.loading} onSelect={id => { setNavigationOpen(false); workspace.select(id) }} onCreate={() => { setNavigationOpen(false); setCreateOpen(true) }} onJoin={() => { setNavigationOpen(false); setJoinOpen(true) }} />
  )
  const members = workspace.details?.participants.filter(person => person.membershipState !== 'left') ?? []
  const onlineMembers = members.filter(person => presence[person.participantId] === 'online')
  const readOnly = historical || workspace.details?.canSend === false
  const discussionMode = workspace.details && <DiscussionModeControl meeting={workspace.details} onModeChanged={workspace.refreshDetails} />
  const topbarSlot = navigationTrigger?.parentElement?.querySelector('#workspace-topbar-content')
  const topicHeader = workspace.details && !historical && <div className="workspace-topic-header">
    <h1 aria-label={workspace.details.name} className="workspace-topbar-title" title={workspace.details.name}><button type="button"
      className="workspace-topic-trigger" aria-label={workspace.details.currentParticipant?.isCreator ? '编辑会议信息' : '查看会议信息'}
      aria-expanded={rightPanel === 'info'}
      onClick={() => setRightPanel(current => current === 'info' ? null : 'info')}>{workspace.details.name}</button></h1>
    <div className="workspace-member-tools">
      <button type="button" className="workspace-members-toggle" aria-expanded={rightPanel === 'members'} onClick={() => setRightPanel(current => current === 'members' ? null : 'members')} aria-label={`查看 ${members.length} 位成员，${onlineMembers.length} 位在线`}>
        {onlineMembers.length > 0 && <span className="workspace-member-faces" aria-hidden="true">{onlineMembers.slice(0, 3).map(person =>
          <i key={person.participantId}><AvatarImage src={person.avatarUrl} colorSlot={person.avatarColorSlot} fallback={Array.from(person.displayName)[0]} /></i>)}</span>}{onlineMembers.length}/{members.length} 在线
      </button>
      {workspace.details.canManageInvitation && <Button type="primary" onClick={openInvite}>邀请入会</Button>}
    </div>
  </div>
  const closePanelOnMobile = () => { if (!wideDesktop) setRightPanel(null) }
  const memberList = workspace.details && <ParticipantList compact onRemove={!historical && workspace.details.currentParticipant?.isCreator ? member => {
    setRemovalError('')
    setRemoval({ member, operationId: createUuid(), meetingId: workspace.details!.meetingId })
  } : undefined} participants={workspace.details.participants} presence={presence} agentActivities={agentActivities}
    onEditNickname={historical ? undefined : () => { closePanelOnMobile(); setNicknameOpen(true) }}
    onPrivateChat={participant => { closePanelOnMobile(); composerRef.current?.privateChat?.(participant.participantId) }}
    onMention={historical ? undefined : participant => { closePanelOnMobile(); composerRef.current?.mention(participant.displayName) }} />
  const currentBaseTab = (): 'info' | 'members' | 'files' | 'images' =>
    rightPanel && rightPanel !== 'reader' ? rightPanel : 'members'
  const openMessageInReader = (message: ChatMessage, expanded = false) => {
    setReaderTarget({ kind: 'message', message, returnTab: currentBaseTab() })
    setReaderExpanded(expanded)
    if (!expanded) setRightPanel('reader')
  }
  const openMarkdownFileInReader = (entry: { file: ChatAttachment; author?: string; createdAt?: Date }, returnTab = currentBaseTab()) => {
    if (!workspace.details) return
    setReaderTarget({ kind: 'markdownFile', meetingId: workspace.details.meetingId, ...entry, returnTab })
    setRightPanel('reader')
  }
  const activePanelTab = rightPanel ?? renderedPanel
  const readerKey = readerTarget?.kind === 'message' ? `reader:msg:${readerTarget.message.messageId}` : readerTarget?.kind === 'markdownFile' ? `reader:file:${readerTarget.file.attachmentId}` : activePanelTab
  const readerDocumentBody = readerTarget
    ? readerTarget.kind === 'message'
      ? <MessageBody diagrams content={readerTarget.message.content} mentions={readerTarget.message.mentions} />
      : <TextAttachmentPreview key={readerTarget.file.attachmentId} markdown url={attachmentUrl(readerTarget.meetingId, readerTarget.file.attachmentId, true)} sizeBytes={readerTarget.file.sizeBytes} />
    : null
  const rightPanelBody = workspace.details && <div key={activePanelTab === 'reader' ? readerKey : activePanelTab} className="workspace-right-panel-view">
    {activePanelTab === 'reader' && readerTarget
      ? readerExpanded
        ? <div className="workspace-reader-expanded-hint">文档已在宽屏弹窗中打开</div>
        : readerTarget.kind === 'message'
          ? <article className="workspace-reader-doc">
              <div className="workspace-reader-meta">
                <strong>{readerTarget.message.author.displayName}</strong>
                <time dateTime={readerTarget.message.createdAt.toISOString()}>{readerTarget.message.createdAt.toLocaleString('zh-CN')}</time>
              </div>
              {readerDocumentBody}
            </article>
          : <article className="workspace-reader-doc">
              <div className="workspace-reader-meta">
                <strong title={readerTarget.file.name}>{readerTarget.file.name}</strong>
                {readerTarget.createdAt && <time dateTime={readerTarget.createdAt.toISOString()}>{readerTarget.author ? `${readerTarget.author} · ` : ''}{readerTarget.createdAt.toLocaleString('zh-CN')}</time>}
              </div>
              {readerDocumentBody}
            </article>
      : activePanelTab === 'info'
        ? <MeetingInformationPanel meeting={workspace.details} onSaved={workspace.refreshDetails} />
        : activePanelTab === 'files' || activePanelTab === 'images'
          ? <MeetingAttachmentPanel kind={activePanelTab} meetingId={workspace.details.meetingId} scope={activeScope?.scope ?? 'public'} peerParticipantId={activeScope?.peerParticipantId}
              onLocate={id => activeScope ? activeScope.locate(id) : Promise.resolve()} onClose={closePanelOnMobile}
              onOpenMarkdownFile={entry => openMarkdownFileInReader(entry, 'files')} />
          : memberList}
  </div>
  const rightPanelHeader = activePanelTab === 'reader' ? <div className="workspace-right-panel-header">
    <div className="workspace-reader-head">
      <Button type="text" size="small" aria-label="返回侧边栏" icon={<ArrowLeftOutlined />} onClick={() => { closeReaderExpanded(); setRightPanel(readerTarget?.returnTab ?? 'members') }} />
      <span>文档阅读</span>
    </div>
    <div className="workspace-reader-actions">
      <Button type="text" size="small" aria-label="放大阅读" title="放大阅读" icon={<ExpandOutlined />} onClick={() => setReaderExpanded(true)} />
      {readerTarget?.kind === 'markdownFile' && <Button type="text" size="small" aria-label={`下载 ${readerTarget.file.name}`} icon={<DownloadOutlined />}
        href={attachmentUrl(readerTarget.meetingId, readerTarget.file.attachmentId)} target="_blank" rel="noopener noreferrer" />}
      <Button type="text" size="small" aria-label="关闭右侧面板" icon={<CloseOutlined />} onClick={() => { closeReaderExpanded(); setRightPanel(null) }} />
    </div>
  </div> : <div className="workspace-right-panel-header">
    <div className="workspace-right-panel-tabs" role="tablist" aria-label="右侧信息面板">
      <button type="button" role="tab" aria-selected={rightPanel === 'info'} className={rightPanel === 'info' ? 'is-active' : undefined}
        onClick={() => setRightPanel('info')}><InfoCircleOutlined aria-hidden="true" /><span>信息</span></button>
      <button type="button" role="tab" aria-selected={rightPanel === 'members'} className={rightPanel === 'members' ? 'is-active' : undefined}
        onClick={() => setRightPanel('members')}><TeamOutlined aria-hidden="true" /><span>成员</span><small>{onlineMembers.length}/{members.length}</small></button>
      <button type="button" role="tab" aria-selected={rightPanel === 'files'} className={rightPanel === 'files' ? 'is-active' : undefined}
        onClick={() => setRightPanel('files')}><FolderOpenOutlined aria-hidden="true" /><span>文件</span></button>
      <button type="button" role="tab" aria-selected={rightPanel === 'images'} className={rightPanel === 'images' ? 'is-active' : undefined}
        onClick={() => setRightPanel('images')}><PictureOutlined aria-hidden="true" /><span>图片</span></button>
    </div>
    <Button type="text" size="small" aria-label="关闭右侧面板" icon={<CloseOutlined />} onClick={() => setRightPanel(null)} />
  </div>
  const canShowRightPanel = Boolean(workspace.selectedId && workspace.details)
  const showRightPanel = canShowRightPanel && Boolean(rightPanel)
  return <section className={(workspace.selectedId ? 'meeting-workspace' : 'meeting-workspace workspace-home') + (wideDesktop && canShowRightPanel ? ' workspace-with-panel' : '') + (wideDesktop && showRightPanel ? ' workspace-has-rail' : '') + (resizingRightPanel ? ' workspace-right-resizing' : '')}
    style={{ '--workspace-right-panel-width': activePanelWidth + 'px' } as CSSProperties}>
    {navigation}
    {!workspace.selectedId ? <>
      {(listError || workspace.error) && <div className="workspace-home-errors">{listError}
        {workspace.error && <Alert closable showIcon onClose={workspace.clearError} type="error" title={workspace.error.code === 'meeting_not_found' ? '会议已不可访问' : workspace.error.message} />}
      </div>}
      <div className="workspace-home-prompt">选择一个会议，开始讨论</div>
    </> : <>
    {navigationTrigger && createPortal(<Button type="text" icon={<MenuOutlined />} aria-label="打开会议导航"
      onClick={() => setNavigationOpen(true)} />, navigationTrigger)}
    <Drawer title="会议导航" placement="left" open={navigationOpen} onClose={() => setNavigationOpen(false)} size={280}>
      {navigationOpen && navigation}
    </Drawer>
    <main className="meeting-main">
      {listError}
      {workspace.error && <Alert closable type="error" showIcon title={workspace.error.code === 'meeting_not_found' ? '会议已不可访问' : '操作未能完成'} description={workspace.error.message} onClose={workspace.clearError} />}
      {workspace.detailLoading ? <Typography.Text>正在加载会议...</Typography.Text> : workspace.details ? <>
        {historical ? <header className="workspace-history-header">
          <Button type="link" onClick={() => navigate('/')}>‹ 我的会议</Button>
          <div className="workspace-history-heading"><h1>{workspace.details.name}</h1><span>已退出</span>
            <div className="workspace-history-actions">
              <Button onClick={() => setHideId(workspace.selectedId)}>从我的历史移除</Button>
              <Button type="primary" loading={workspace.operationLoading} onClick={() => void workspace.updateHistory(workspace.details!.meetingId, 'rejoin')}>重新入会</Button>
            </div>
          </div>
        </header> : topbarSlot ? createPortal(topicHeader, topbarSlot) : topicHeader}
        <RealtimeChatRegion sessionCache={chatCache.current} readOnly={readOnly} onMeetingDetailsChanged={workspace.refreshDetails} onMembershipChanged={workspace.membershipChanged} composerRef={composerRef} meetingId={workspace.details.meetingId} participants={workspace.details.participants} onMeetingUnavailable={workspace.handleMeetingUnavailable} onPresenceChanged={setPresence} onAgentActivitiesChanged={setAgentActivities} onActiveScopeChanged={setActiveScope} onOpenAttachments={() => setRightPanel(current => current === 'files' || current === 'images' ? null : 'files')} onReadMessageInPanel={openMessageInReader} onReadMessageExpanded={message => openMessageInReader(message, true)} onReadMarkdownFile={entry => openMarkdownFileInReader(entry)} />
        {readOnly && discussionMode}
      </> : <Flex vertical gap="middle" className="meeting-empty"><Typography.Title level={2}>开始一场圆桌会议</Typography.Title><Typography.Text type="secondary">创建新会议，或使用邀请码加入已有会议。</Typography.Text><Flex gap="small"><Button type="primary" onClick={() => setCreateOpen(true)}>创建会议</Button><Button onClick={() => setJoinOpen(true)}>加入会议</Button></Flex></Flex>}
    </main>
    {wideDesktop && canShowRightPanel && <aside className={showRightPanel ? 'workspace-right-panel is-open' : 'workspace-right-panel'}
      aria-label={activePanelTab === 'reader' ? '文档阅读' : activePanelTab === 'info' ? '会议信息' : activePanelTab === 'files' ? '会话文件' : activePanelTab === 'images' ? '会话图片' : '会议成员'}
      aria-hidden={!showRightPanel} inert={!showRightPanel || undefined}>
      {showRightPanel && <div className="workspace-right-resizer" role="separator" aria-orientation="vertical" aria-label="调整右侧栏宽度"
        aria-valuemin={activePanelMin} aria-valuemax={activePanelMax} aria-valuenow={activePanelWidth} tabIndex={0}
        onPointerDown={startRightResize} onPointerMove={moveRightResize} onPointerUp={finishRightResize}
        onPointerCancel={finishRightResize} onLostPointerCapture={finishRightResize} onKeyDown={keydownRightResize} />}
      <div className="workspace-right-panel-inner">
        {rightPanelHeader}
        <div className={activePanelTab === 'reader' ? 'workspace-right-panel-body is-reader' : 'workspace-right-panel-body'}>{rightPanelBody}</div>
      </div>
    </aside>}
    </>}
    {!wideDesktop && workspace.details && <Drawer title={rightPanelHeader} closable={false} placement="right" size="min(360px, calc(100vw - 18px))"
      open={Boolean(rightPanel)} onClose={() => { closeReaderExpanded(); setRightPanel(null) }} rootClassName={activePanelTab === 'reader' ? 'workspace-right-drawer is-reader' : 'workspace-right-drawer'}>
      {rightPanel && rightPanelBody}
    </Drawer>}
    <Modal open={Boolean(readerExpanded && readerTarget)} onCancel={closeReaderExpanded} footer={null} centered closable={false} destroyOnHidden
      width={readerModalFullscreen ? '100vw' : activeReaderModalWidth}
      className={'workspace-reader-modal' + (readerModalFullscreen ? ' is-fullscreen' : '') + (resizingReaderModal ? ' is-resizing' : '')}
      styles={{ mask: { backgroundColor: 'rgb(20 23 19 / 45%)' } }}>
      {readerTarget && <>
        {!readerModalFullscreen && viewportWidth > 640 && <>
          <div className="workspace-reader-modal-resizer is-left" role="separator" aria-orientation="vertical" aria-label="调整阅读弹窗宽度"
            aria-valuemin={readerModalBounds.min} aria-valuemax={readerModalBounds.max} aria-valuenow={activeReaderModalWidth} tabIndex={0}
            title="拖动调整弹窗宽度，双击恢复默认"
            onPointerDown={startModalResize('left')} onPointerMove={moveModalResize} onPointerUp={finishModalResize}
            onPointerCancel={finishModalResize} onLostPointerCapture={finishModalResize} onKeyDown={keydownModalResize}
            onDoubleClick={resetReaderModalWidth} />
          <div className="workspace-reader-modal-resizer is-right" aria-hidden="true"
            title="拖动调整弹窗宽度，双击恢复默认"
            onPointerDown={startModalResize('right')} onPointerMove={moveModalResize} onPointerUp={finishModalResize}
            onPointerCancel={finishModalResize} onLostPointerCapture={finishModalResize}
            onDoubleClick={resetReaderModalWidth} />
        </>}
        <div className="workspace-reader-modal-header">
          <div className="workspace-reader-modal-title">
            <strong title={readerTarget.kind === 'message' ? readerTarget.message.author.displayName : readerTarget.file.name}>
              {readerTarget.kind === 'message' ? readerTarget.message.author.displayName : readerTarget.file.name}
            </strong>
            {readerTarget.kind === 'message'
              ? <time dateTime={readerTarget.message.createdAt.toISOString()}>{readerTarget.message.createdAt.toLocaleString('zh-CN')}</time>
              : readerTarget.createdAt && <time dateTime={readerTarget.createdAt.toISOString()}>{readerTarget.author ? `${readerTarget.author} · ` : ''}{readerTarget.createdAt.toLocaleString('zh-CN')}</time>}
          </div>
          <div className="workspace-reader-actions">
            {readerTarget.kind === 'markdownFile' && <Button type="text" size="small" aria-label={`下载 ${readerTarget.file.name}`} icon={<DownloadOutlined />}
              href={attachmentUrl(readerTarget.meetingId, readerTarget.file.attachmentId)} target="_blank" rel="noopener noreferrer" />}
            <Button type="text" size="small" aria-label={readerModalFullscreen ? '退出全屏' : '全屏阅读'} title={readerModalFullscreen ? '退出全屏' : '全屏阅读'}
              icon={readerModalFullscreen ? <FullscreenExitOutlined /> : <FullscreenOutlined />} onClick={() => setReaderModalFullscreen(value => !value)} />
            <Button type="text" size="small" aria-label="退出放大阅读" title="退出放大阅读" icon={<CloseOutlined />} onClick={closeReaderExpanded} />
          </div>
        </div>
        <div className="workspace-reader-modal-body">{readerDocumentBody}</div>
      </>}
    </Modal>
    <Modal open={!!removal} title="移除会议成员" okText="移除" cancelText="取消" okButtonProps={{ danger: true }}
      confirmLoading={removing} onOk={() => void removeMember()} onCancel={() => { if (!removing) setRemoval(undefined) }}>
      <p>确定将「{removal?.member.displayName}」移出本场会议？对方可以重新加入。</p>
      {removalError && <Alert type="error" showIcon title={removalError} />}
    </Modal>
    {nicknameOpen && workspace.details?.currentParticipant && <MeetingNicknameDialog meetingId={workspace.details.meetingId}
      meetingName={workspace.details.name} name={workspace.details.currentParticipant.displayName} onSaved={workspace.refresh} onClose={() => setNicknameOpen(false)} />}
    <CreateMeetingDialog open={createOpen} loading={workspace.operationLoading} onCancel={() => setCreateOpen(false)} onSubmit={value => void workspace.create(value)} />
    <InviteMeetingDialog meetingName={workspace.details?.name} open={inviteOpen} invitation={workspace.invitation} loading={!workspace.invitation && workspace.operationLoading} onCancel={() => setInviteOpen(false)} />
    <JoinMeetingDialog initialCode={invitedCode} open={joinOpen || Boolean(invitedCode)} loading={workspace.operationLoading} succeeded={workspace.joinSucceeded} previewLoading={workspace.previewLoading} preview={workspace.preview} error={workspace.joinError?.message} errorCode={workspace.joinError?.code} retryAfterSeconds={workspace.joinError?.retryAfterSeconds} onCancel={() => { workspace.clearJoin(); setJoinOpen(false); if (invitedCode) navigate(location.pathname + location.search, { replace: true }) }} onCodeChange={workspace.clearJoin} onPreview={workspace.previewCode} onSubmit={value => void workspace.join(value.code, value.displayName)} />
    <Modal open={leaveOpen} title="退出会议" okText="确认退出" cancelText="取消" confirmLoading={workspace.operationLoading} onCancel={() => setLeaveOpen(false)} onOk={() => void workspace.leave().then(success => { if (success) setLeaveOpen(false) })}>
      <Typography.Paragraph>退出后不再接收新消息，可以在历史会议中查看退出前有权访问的消息。之后可在历史会议中重新入会，也可通过邀请码加入。</Typography.Paragraph>
    </Modal>
    <Modal width={540} centered className="history-remove-confirm" styles={{ mask: { backgroundColor: 'rgb(20 23 19 / 40%)' } }} open={!!hideId} title="从我的历史移除" okText="确认移除" cancelText="取消" okButtonProps={{ danger: true }} confirmLoading={workspace.operationLoading} onCancel={() => setHideId(undefined)} onOk={() => { if (hideId) void workspace.updateHistory(hideId, 'hide').then(ok => { if (ok) setHideId(undefined) }) }}>
      <Typography.Paragraph>确定从你的历史列表中移除“{workspace.lists.history.find(item => item.meetingId === hideId)?.name}”吗？</Typography.Paragraph>
      <Typography.Paragraph type="secondary">会议、消息和其他人的记录都会保留。之后仍可使用邀请码加入。</Typography.Paragraph>
    </Modal>
    <Modal width={540} centered className="history-remove-confirm" styles={{ mask: { backgroundColor: 'rgb(20 23 19 / 40%)' } }} open={deleteOpen} title="永久删除会议" okText="永久删除" cancelText="取消" okButtonProps={{ danger: true, disabled: confirmName.normalize().trim() !== workspace.details?.name }} confirmLoading={workspace.operationLoading} onCancel={closeDelete} onOk={() => { if (workspace.selectedId) void workspace.remove(workspace.selectedId, confirmName).then(success => { if (success) { const id = workspace.selectedId!; for (const key of chatCache.current.states.keys()) if (key.startsWith(id + ':')) chatCache.current.states.delete(key); delete chatCache.current.selection[id]; closeDelete() } }) }}>
      <Typography.Paragraph>请输入完整会议名称“<strong>{workspace.details?.name}</strong>”确认删除。公开消息、私聊、全部附件和参与者记录将永久删除，会议码失效，此操作不可恢复。</Typography.Paragraph><Input aria-label="确认会议名称" value={confirmName} onChange={event => setConfirmName(event.target.value)} />
    </Modal>
  </section>
}
