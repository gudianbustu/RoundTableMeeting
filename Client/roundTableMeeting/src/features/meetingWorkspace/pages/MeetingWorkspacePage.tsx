import type { MessageComposerHandle } from '../components/MessageComposer'
import { copyText } from '@/shared/utils/copyText'
import { Alert, Button, Flex, Input, Modal, Typography, message } from 'antd'
import { useEffect, useRef, useState } from 'react'
import { MeetingNavigation } from '../components/MeetingNavigation'
import { MeetingHeader } from '../components/MeetingHeader'
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
  const chatCache = useRef<ChatSessionCache>({ states: new Map(), selection: {} })
  const composerRef = useRef<MessageComposerHandle>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [joinOpen, setJoinOpen] = useState(false)
  const [inviteOpen, setInviteOpen] = useState(false)
  const [leaveOpen, setLeaveOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [hideId, setHideId] = useState<string>()
  const [confirmName, setConfirmName] = useState('')
  const [presence, setPresence] = useState<Record<string, 'online' | 'offline'>>({})
  const [agentActivities, setAgentActivities] = useState<Record<string, AgentActivity>>({})
  const [messageApi, contextHolder] = message.useMessage()
  useEffect(() => { if (workspace.createSucceeded) setCreateOpen(false) }, [workspace.createSucceeded])
  useEffect(() => { if (workspace.joinSucceeded) setJoinOpen(false) }, [workspace.joinSucceeded])
  useEffect(() => {
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
  const copyInvitation = async () => {
    if (!workspace.invitation) return
    try {
      await copyText(workspace.invitation.meetingCode)
      messageApi.success('邀请码已复制')
    } catch { messageApi.error('复制失败，请手动选择并复制邀请码') }
  }
  const openInvite = () => setInviteOpen(true)
  const closeDelete = () => { setDeleteOpen(false); setConfirmName('') }
  return <section className="meeting-workspace">
    {contextHolder}
    <MeetingNavigation details={workspace.details} detailLoading={workspace.detailLoading} operationLoading={workspace.operationLoading} onRejoin={id => void workspace.updateHistory(id, 'rejoin')} onHide={setHideId} onLeave={() => setLeaveOpen(true)} onDelete={() => setDeleteOpen(true)} view={workspace.view} onViewChange={workspace.setView} meetings={workspace.meetings} selectedId={workspace.selectedId} loading={workspace.loading} onSelect={workspace.select} onCreate={() => setCreateOpen(true)} onJoin={() => setJoinOpen(true)} />
    <main className="meeting-main">
      {workspace.error && <Alert closable type="error" showIcon title={workspace.error.code === 'meeting_not_found' ? '会议已不可访问' : '操作未能完成'} description={workspace.error.message} onClose={workspace.clearError} />}
      {workspace.detailLoading ? <Typography.Text>正在加载会议...</Typography.Text> : workspace.details ? <>
        <RealtimeChatRegion sessionCache={chatCache.current} readOnly={workspace.details.membershipState === 'left' || workspace.details.canSend === false} onMeetingDetailsChanged={workspace.refreshDetails} onMembershipChanged={workspace.membershipChanged} composerRef={composerRef} composerTools={<MeetingHeader meeting={workspace.details} invitation={workspace.invitation} onCopy={() => void copyInvitation()} onModeChanged={workspace.refreshDetails} onRefresh={workspace.refresh} />} meetingId={workspace.details.meetingId} participants={workspace.details.participants} onMeetingUnavailable={workspace.handleMeetingUnavailable} onPresenceChanged={setPresence} onAgentActivitiesChanged={setAgentActivities} />
      </> : <Flex vertical gap="middle" className="meeting-empty"><Typography.Title level={2}>开始一场圆桌会议</Typography.Title><Typography.Text type="secondary">创建新会议，或使用邀请码加入已有会议。</Typography.Text><Flex gap="small"><Button type="primary" onClick={() => setCreateOpen(true)}>创建会议</Button><Button onClick={() => setJoinOpen(true)}>加入会议</Button></Flex></Flex>}
    </main>
    {workspace.details && <aside className="meeting-side-column"><div className="meeting-sidebar"><ParticipantList onInvite={workspace.details.canManageInvitation ? openInvite : undefined} onPrivateChat={participant => composerRef.current?.privateChat?.(participant.participantId)} onMention={workspace.details.membershipState === 'left' ? undefined : participant => composerRef.current?.mention(participant.displayName)} participants={workspace.details.participants} presence={presence} agentActivities={agentActivities} /></div></aside>}
    <CreateMeetingDialog open={createOpen} loading={workspace.operationLoading} onCancel={() => setCreateOpen(false)} onSubmit={value => void workspace.create(value)} />
    <InviteMeetingDialog meetingName={workspace.details?.name} open={inviteOpen} invitation={workspace.invitation} loading={!workspace.invitation && workspace.operationLoading} onCancel={() => setInviteOpen(false)} />
    <JoinMeetingDialog open={joinOpen} loading={workspace.operationLoading} succeeded={workspace.joinSucceeded} previewLoading={workspace.previewLoading} preview={workspace.preview} error={workspace.joinError?.message} errorCode={workspace.joinError?.code} retryAfterSeconds={workspace.joinError?.retryAfterSeconds} onCancel={() => { workspace.clearJoin(); setJoinOpen(false) }} onCodeChange={workspace.clearJoin} onPreview={workspace.previewCode} onSubmit={value => void workspace.join(value.code, value.displayName)} />
    <Modal open={leaveOpen} title="退出会议" okText="确认退出" cancelText="取消" confirmLoading={workspace.operationLoading} onCancel={() => setLeaveOpen(false)} onOk={() => void workspace.leave().then(success => { if (success) setLeaveOpen(false) })}>
      <Typography.Paragraph>退出后不再接收新消息，可以在历史会议中查看退出前有权访问的消息。之后可在历史会议中重新入会，也可通过邀请码加入。</Typography.Paragraph>
    </Modal>
    <Modal open={!!hideId && hideId === workspace.selectedId} title="从历史会议中删除" okText="确认删除" cancelText="取消" okButtonProps={{ danger: true }} confirmLoading={workspace.operationLoading} onCancel={() => setHideId(undefined)} onOk={() => { if (hideId) void workspace.updateHistory(hideId, 'hide').then(ok => { if (ok) setHideId(undefined) }) }}>
      <Typography.Paragraph>从你的历史列表中移除“{workspace.details?.name}”。会议、消息和其他人的记录都会保留。之后仍可使用邀请码加入。</Typography.Paragraph>
    </Modal>
    <Modal open={deleteOpen} title="删除会议" okText="确认删除" cancelText="取消" okButtonProps={{ danger: true, disabled: confirmName.normalize().trim() !== workspace.details?.name }} confirmLoading={workspace.operationLoading} onCancel={closeDelete} onOk={() => { if (workspace.selectedId) void workspace.remove(workspace.selectedId, confirmName).then(success => { if (success) { const id = workspace.selectedId!; for (const key of chatCache.current.states.keys()) if (key.startsWith(id + ':')) chatCache.current.states.delete(key); delete chatCache.current.selection[id]; closeDelete() } }) }}>
      <Typography.Paragraph>请输入完整会议名称“<strong>{workspace.details?.name}</strong>”确认删除。公开消息、私聊、全部附件和参与者记录将永久删除，会议码失效，此操作不可恢复。</Typography.Paragraph><Input aria-label="确认会议名称" value={confirmName} onChange={event => setConfirmName(event.target.value)} />
    </Modal>
  </section>
}
