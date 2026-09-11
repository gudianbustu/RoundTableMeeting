import type { MessageComposerHandle } from '../components/MessageComposer'
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
import { RealtimeChatRegion } from './RealtimeChatRegion'

export function hasUnknownOnlineParticipant(participants: Participant[], presence: Record<string, 'online' | 'offline'>) {
  const participantIds = new Set(participants.map(participant => participant.participantId))
  return Object.entries(presence).some(([participantId, state]) => state === 'online' && !participantIds.has(participantId))
}

export function hasUnknownAgentActivity(participants: Participant[], activities: Record<string, AgentActivity>) {
  const participantIds = new Set(participants.map(participant => participant.participantId))
  return Object.values(activities).some(activity => !participantIds.has(activity.participantId))
}

export function MeetingWorkspacePage() {
  const workspace = useMeetingWorkspace()
  const composerRef = useRef<MessageComposerHandle>(null)
  const [createOpen, setCreateOpen] = useState(false)
  const [joinOpen, setJoinOpen] = useState(false)
  const [inviteOpen, setInviteOpen] = useState(false)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [confirmName, setConfirmName] = useState('')
  const [presence, setPresence] = useState<Record<string, 'online' | 'offline'>>({})
  const [agentActivities, setAgentActivities] = useState<Record<string, AgentActivity>>({})
  const [messageApi, contextHolder] = message.useMessage()
  useEffect(() => { if (workspace.createSucceeded) setCreateOpen(false) }, [workspace.createSucceeded])
  useEffect(() => { if (workspace.joinSucceeded) setJoinOpen(false) }, [workspace.joinSucceeded])
  useEffect(() => {
    setPresence({}); setAgentActivities({})
    setCreateOpen(false); setJoinOpen(false)
    setInviteOpen(false); setDeleteOpen(false); setConfirmName('')
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
      if (!navigator.clipboard?.writeText) throw new Error('Clipboard unavailable')
      await navigator.clipboard.writeText(workspace.invitation.meetingCode)
      messageApi.success('邀请码已复制')
    } catch { messageApi.error('复制失败，请手动选择并复制邀请码') }
  }
  const openInvite = () => setInviteOpen(true)
  const closeDelete = () => { setDeleteOpen(false); setConfirmName('') }
  return <section className="meeting-workspace">
    {contextHolder}
    <MeetingNavigation meetings={workspace.meetings} selectedId={workspace.selectedId} loading={workspace.loading} onSelect={workspace.select} onCreate={() => setCreateOpen(true)} onJoin={() => setJoinOpen(true)} />
    <main className="meeting-main">
      {workspace.error && <Alert closable type="error" showIcon title={workspace.error.code === 'meeting_not_found' ? '会议已不可访问' : '操作未能完成'} description={workspace.error.message} onClose={workspace.clearError} />}
      {workspace.detailLoading ? <Typography.Text>正在加载会议...</Typography.Text> : workspace.details ? <>
        <RealtimeChatRegion composerRef={composerRef} composerTools={<MeetingHeader meeting={workspace.details} invitation={workspace.invitation} onCopy={() => void copyInvitation()} onInvite={openInvite} onRefresh={workspace.refresh} onDelete={() => setDeleteOpen(true)} />} meetingId={workspace.details.meetingId} participants={workspace.details.participants} onMeetingUnavailable={workspace.handleMeetingUnavailable} onPresenceChanged={setPresence} onAgentActivitiesChanged={setAgentActivities} />
      </> : <Flex vertical gap="middle" className="meeting-empty"><Typography.Title level={2}>开始一场圆桌会议</Typography.Title><Typography.Text type="secondary">创建新会议，或使用邀请码加入已有会议。</Typography.Text><Flex gap="small"><Button type="primary" onClick={() => setCreateOpen(true)}>创建会议</Button><Button onClick={() => setJoinOpen(true)}>加入会议</Button></Flex></Flex>}
    </main>
    {workspace.details && <aside className="meeting-side-column"><div className="meeting-sidebar"><ParticipantList onMention={participant => composerRef.current?.mention(participant.displayName)} participants={workspace.details.participants} presence={presence} agentActivities={agentActivities} /></div></aside>}
    <CreateMeetingDialog open={createOpen} loading={workspace.operationLoading} onCancel={() => setCreateOpen(false)} onSubmit={value => void workspace.create(value)} />
    <InviteMeetingDialog open={inviteOpen} invitation={workspace.invitation} loading={!workspace.invitation && workspace.operationLoading} onCancel={() => setInviteOpen(false)} />
    <JoinMeetingDialog open={joinOpen} loading={workspace.operationLoading} succeeded={workspace.joinSucceeded} previewLoading={workspace.previewLoading} preview={workspace.preview} error={workspace.error?.message} retryAfterSeconds={workspace.error?.retryAfterSeconds} onCancel={() => { workspace.clearJoin(); setJoinOpen(false) }} onPreview={workspace.previewCode} onSubmit={value => void workspace.join(value.code, value.displayName)} />
    <Modal open={deleteOpen} title="删除会议" okText="确认删除" cancelText="取消" okButtonProps={{ danger: true, disabled: confirmName.normalize().trim() !== workspace.details?.name }} confirmLoading={workspace.operationLoading} onCancel={closeDelete} onOk={() => { if (workspace.selectedId) void workspace.remove(workspace.selectedId, confirmName).then(success => { if (success) closeDelete() }) }}>
      <Typography.Paragraph>请输入完整会议名称确认删除。参与者记录和会议码将永久失效，此操作不可恢复。</Typography.Paragraph><Input aria-label="确认会议名称" value={confirmName} onChange={event => setConfirmName(event.target.value)} />
    </Modal>
  </section>
}
