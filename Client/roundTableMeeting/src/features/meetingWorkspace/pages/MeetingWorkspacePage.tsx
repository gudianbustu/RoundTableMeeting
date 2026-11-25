import './MeetingWorkspaceShell.css'
import { createPortal } from 'react-dom'
import { CloseOutlined, MenuOutlined } from '@ant-design/icons'
import { useNavigate } from 'react-router-dom'
import { FirstMeetingHome } from '../components/FirstMeetingHome'
import { MeetingDirectory } from '../components/MeetingDirectory'
import type { MessageComposerHandle } from '../components/MessageComposer'
import { Alert, Button, Drawer, Flex, Input, Modal, Typography } from 'antd'
import { useEffect, useRef, useState } from 'react'
import { MeetingNicknameDialog } from '../components/MeetingNicknameDialog'
import { MeetingNavigation } from '../components/MeetingNavigation'
import { DiscussionModeControl } from '../components/DiscussionModeControl'
import { ParticipantList } from '../components/ParticipantList'
import { CreateMeetingDialog } from '../components/CreateMeetingDialog'
import { InviteMeetingDialog } from '../components/InviteMeetingDialog'
import { JoinMeetingDialog } from '../components/JoinMeetingDialog'
import { useMeetingWorkspace } from '../hooks/useMeetingWorkspace'
import type { AgentActivity } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'
import { RealtimeChatRegion, type ChatSessionCache } from './RealtimeChatRegion'

export function hasUnknownOnlineParticipant(participants: Participant[], presence: Record<string, 'online' | 'offline'>) {
  const participantIds = new Set(participants.filter(p => p.membershipState !== 'left').map(p => p.participantId))
  return Object.entries(presence).some(([participantId, state]) => state === 'online' && !participantIds.has(participantId))
}

export function hasUnknownAgentActivity(participants: Participant[], activities: Record<string, AgentActivity>) {
  const participantIds = new Set(participants.map(participant => participant.participantId))
  return Object.values(activities).some(activity => !participantIds.has(activity.participantId))
}

export function MeetingWorkspacePage() {
  const workspace = useMeetingWorkspace()
  const navigate = useNavigate()
  const chatCache = useRef<ChatSessionCache>({ states: new Map(), selection: {} })
  const composerRef = useRef<MessageComposerHandle>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [joinOpen, setJoinOpen] = useState(false)
  const [inviteOpen, setInviteOpen] = useState(false)
  const [mentionPeerId, setMentionPeerId] = useState<string>()
  const [nicknameOpen, setNicknameOpen] = useState(false)
  const [membersOpen, setMembersOpen] = useState(false)
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
    setMembersOpen(false); setNicknameOpen(false); setNavigationOpen(false)
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
    <MeetingNavigation onAll={() => navigate('/')} details={workspace.details} detailLoading={workspace.detailLoading} operationLoading={workspace.operationLoading} onRejoin={id => void workspace.updateHistory(id, 'rejoin')} onHide={id => { setNavigationOpen(false); setHideId(id) }} onLeave={() => { setNavigationOpen(false); setLeaveOpen(true) }} onDelete={() => { setNavigationOpen(false); setDeleteOpen(true) }} view={historical ? 'history' : 'active'} meetings={historical ? workspace.lists.history : workspace.lists.active} selectedId={workspace.selectedId} loading={workspace.loading} onSelect={id => { setNavigationOpen(false); workspace.select(id) }} onCreate={() => setCreateOpen(true)} onJoin={() => setJoinOpen(true)} />
  )
  const members = workspace.details?.participants.filter(person => person.membershipState !== 'left') ?? []
  const readOnly = historical || workspace.details?.canSend === false
  const discussionMode = workspace.details && <DiscussionModeControl meeting={workspace.details} onModeChanged={workspace.refreshDetails} />
  const creator = workspace.details?.participants.find(person => person.isCreator)
  return <section className={workspace.selectedId ? "meeting-workspace" : "meeting-home"}>
    {!workspace.selectedId ? <>
      {listError}
      {workspace.error && <Alert closable onClose={workspace.clearError} type="error" title={workspace.error.message} />}
      {!workspace.listReady ? <Typography.Text>正在加载会议列表…</Typography.Text> :
        workspace.lists.active.length || workspace.lists.history.length ? <MeetingDirectory active={workspace.lists.active} history={workspace.lists.history}
          view={workspace.view} onView={workspace.setView} onSelect={workspace.select} onHide={setHideId} busy={workspace.operationLoading}
          onRejoin={id => void workspace.updateHistory(id, 'rejoin')} onCreate={() => setCreateOpen(true)} onJoin={() => setJoinOpen(true)} /> :
        <FirstMeetingHome onCreate={() => setCreateOpen(true)} onJoin={() => setJoinOpen(true)} />}
    </> : <>
    {navigation}
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
        </header> : <header className="workspace-topic-header">
          <div className="workspace-topic-name"><div className="workspace-title-row"><h1>{workspace.details.name}</h1></div>
            {creator && <p>创建者　{creator.displayName}</p>}
          </div>
          <div className="workspace-member-tools">
              <button type="button" className="workspace-members-toggle" aria-expanded={membersOpen} onClick={() => setMembersOpen(true)} aria-label={`查看 ${members.length} 位成员`}>
                <span className="workspace-member-faces" aria-hidden="true">{members.slice(0, 3).map(person =>
                  <i key={person.participantId}>{Array.from(person.displayName)[0]}</i>)}</span>{members.length} 位成员
              </button>
            {workspace.details.canManageInvitation && <Button type="primary" onClick={openInvite}>邀请入会</Button>}
          </div>
        </header>}
        <RealtimeChatRegion onPeerChanged={setMentionPeerId} sessionCache={chatCache.current} readOnly={readOnly} composerTools={discussionMode} onMeetingDetailsChanged={workspace.refreshDetails} onMembershipChanged={workspace.membershipChanged} composerRef={composerRef} meetingId={workspace.details.meetingId} participants={workspace.details.participants} onMeetingUnavailable={workspace.handleMeetingUnavailable} onPresenceChanged={setPresence} onAgentActivitiesChanged={setAgentActivities} />
        {readOnly && discussionMode}
      </> : <Flex vertical gap="middle" className="meeting-empty"><Typography.Title level={2}>开始一场圆桌会议</Typography.Title><Typography.Text type="secondary">创建新会议，或使用邀请码加入已有会议。</Typography.Text><Flex gap="small"><Button type="primary" onClick={() => setCreateOpen(true)}>创建会议</Button><Button onClick={() => setJoinOpen(true)}>加入会议</Button></Flex></Flex>}
    </main>
    </>}
    {workspace.details && <Drawer title={<span>会议成员 <small className="drawer-member-count">{members.length} 人</small></span>} closable={false}
      extra={<Button type="text" aria-label="关闭成员列表" icon={<CloseOutlined />} onClick={() => setMembersOpen(false)} />} placement="right" size="min(450px, calc(100vw - 18px))"
      open={membersOpen} onClose={() => setMembersOpen(false)} rootClassName="workspace-member-drawer">
      <ParticipantList participants={workspace.details.participants} presence={presence} agentActivities={agentActivities}
        mentionPeerId={mentionPeerId} onEditNickname={historical ? undefined : () => { setMembersOpen(false); setNicknameOpen(true) }}
        onPrivateChat={participant => { setMembersOpen(false); composerRef.current?.privateChat?.(participant.participantId) }}
        onMention={historical ? undefined : participant => { setMembersOpen(false); composerRef.current?.mention(participant.displayName) }} />
    </Drawer>}
    {nicknameOpen && workspace.details?.currentParticipant && <MeetingNicknameDialog meetingId={workspace.details.meetingId}
      meetingName={workspace.details.name} name={workspace.details.currentParticipant.displayName} onSaved={workspace.refresh} onClose={() => setNicknameOpen(false)} />}
    <CreateMeetingDialog open={createOpen} loading={workspace.operationLoading} onCancel={() => setCreateOpen(false)} onSubmit={value => void workspace.create(value)} />
    <InviteMeetingDialog meetingName={workspace.details?.name} open={inviteOpen} invitation={workspace.invitation} loading={!workspace.invitation && workspace.operationLoading} onCancel={() => setInviteOpen(false)} />
    <JoinMeetingDialog open={joinOpen} loading={workspace.operationLoading} succeeded={workspace.joinSucceeded} previewLoading={workspace.previewLoading} preview={workspace.preview} error={workspace.joinError?.message} errorCode={workspace.joinError?.code} retryAfterSeconds={workspace.joinError?.retryAfterSeconds} onCancel={() => { workspace.clearJoin(); setJoinOpen(false) }} onCodeChange={workspace.clearJoin} onPreview={workspace.previewCode} onSubmit={value => void workspace.join(value.code, value.displayName)} />
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
