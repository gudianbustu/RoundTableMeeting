import { Avatar, Button, Collapse, Empty, Tag, Tooltip, Typography } from 'antd'
import { MessageOutlined, UserAddOutlined } from '@ant-design/icons'
import type { AgentActivity } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'

export function ParticipantList({ onInvite, mentionPeerId, onMention, onPrivateChat, participants, presence = {} }: { onInvite?: () => void; mentionPeerId?: string; onMention?: (participant: Participant) => void; onPrivateChat?: (participant: Participant) => void; participants: Participant[]; presence?: Record<string, 'online' | 'offline'>; agentActivities?: Record<string, AgentActivity> }) {
  const members = participants.filter(p => p.membershipState !== 'left')
  const online = members.filter(p => presence[p.participantId] === 'online')
  const offline = members.filter(p => presence[p.participantId] !== 'online')
  const rows = (items: Participant[]) => <div className="participant-list-items">{items.map(participant => <div key={participant.participantId} className="participant-list-item">
    <Avatar>{participant.displayName.slice(0, 1)}</Avatar>
    <div className="participant-list-item-content"><Typography.Text strong>{participant.displayName}</Typography.Text><div>
      {participant.isCurrentUser && <Tag>我</Tag>}{participant.isCreator && <Tag color="gold">创建者</Tag>}
      <Typography.Text type="secondary">{participant.type === 'agent' ? 'AI Agent' : '人类参与者'}</Typography.Text>
    </div></div>
    <div className="participant-list-item-actions">
      {onMention && (!mentionPeerId || participant.isCurrentUser || participant.participantId === mentionPeerId) && <Tooltip title={`提及 ${participant.displayName}`}><Button type="text" size="small" aria-label={`提及 ${participant.displayName}`} icon={<span className="participant-mention-icon" aria-hidden="true">@</span>} onClick={() => onMention(participant)} /></Tooltip>}
      {!participant.isCurrentUser && onPrivateChat && <Tooltip title={`私聊 ${participant.displayName}`}><Button type="text" size="small" aria-label={`私聊 ${participant.displayName}`} icon={<MessageOutlined />} onClick={() => onPrivateChat(participant)} /></Tooltip>}
    </div>
  </div>)}</div>
  return <section className="participant-list"><div className="participant-list-heading"><Typography.Title level={4}>参与者</Typography.Title>{onInvite && <Button type="text" aria-label="邀请入会" title="邀请入会" icon={<UserAddOutlined />} onClick={onInvite} />}</div>
    {members.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无参与者" /> : <>
      <Typography.Text type="secondary">在线 · {online.length}</Typography.Text>{rows(online)}
      {offline.length > 0 && <Collapse ghost items={[{ key: 'offline', label: `离线 · ${offline.length}`, children: rows(offline) }]} />}
    </>}
  </section>
}
