import { AvatarImage } from '@/features/avatars/AvatarImage'
import { Avatar, Button, Collapse, Empty, Tag, Tooltip, Typography } from 'antd'
import { EditOutlined, UserDeleteOutlined, MessageOutlined, UserAddOutlined } from '@ant-design/icons'
import './ParticipantList.css'
import type { AgentActivity } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'

export function ParticipantList({ compact = false, onRemove, onEditNickname, onInvite, onMention, onPrivateChat, participants, presence = {} }: { compact?: boolean; onRemove?: (participant: Participant) => void; onEditNickname?: () => void; onInvite?: () => void; onMention?: (participant: Participant) => void; onPrivateChat?: (participant: Participant) => void; participants: Participant[]; presence?: Record<string, 'online' | 'offline'>; agentActivities?: Record<string, AgentActivity> }) {
  const members = participants.filter(p => p.membershipState !== 'left')
  const online = members.filter(p => presence[p.participantId] === 'online')
  const offline = members.filter(p => presence[p.participantId] !== 'online')
  const rows = (items: Participant[]) => <div className="participant-list-items">{items.map(participant => <div key={participant.participantId} className="participant-list-item">
    {participant.avatarUrl && participant.avatarColorSlot !== undefined
      ? <span className="rt-member-avatar"><AvatarImage src={participant.avatarUrl} colorSlot={participant.avatarColorSlot} fallback={Array.from(participant.displayName)[0]} /></span>
      : <Avatar size={compact ? 28 : 32} src={participant.avatarUrl}>{Array.from(participant.displayName)[0]}</Avatar>}
    <div className="participant-list-item-content">
      <div className="participant-list-item-name">
        <Typography.Text strong ellipsis={{ tooltip: participant.displayName }}>{participant.displayName}</Typography.Text>
        {participant.type === 'agent' && <Tag className="participant-role-tag participant-role-agent">AI</Tag>}
        {participant.isCurrentUser && <Tag className="participant-role-tag">我</Tag>}
        {participant.isCreator && <Tag className="participant-role-tag" color="gold">创建者</Tag>}
      </div>
      {!compact && <div>
        <Typography.Text type="secondary">{participant.type === 'agent' ? 'AI Agent' : '人类参与者'}</Typography.Text>
      </div>}
    </div>
    <div className="participant-list-item-actions">
      {onRemove && !participant.isCurrentUser && !participant.isCreator && <Tooltip placement="topRight" title={`移除 ${participant.displayName}`}><Button type="text" danger size="small" aria-label={`移除 ${participant.displayName}`}
        icon={<UserDeleteOutlined />} onClick={() => onRemove(participant)}>{compact ? null : '移除'}</Button></Tooltip>}
      {participant.isCurrentUser && onEditNickname && <Tooltip placement="topRight" title="修改会议昵称"><Button type="text" aria-label="修改会议昵称" size="small" icon={<EditOutlined />} onClick={onEditNickname}>{compact ? null : '改昵称'}</Button></Tooltip>}
      {onMention && <Tooltip placement="topRight" title={`提及 ${participant.displayName}`}><Button type="text" size="small" aria-label={`提及 ${participant.displayName}`} icon={<span className="participant-mention-icon" aria-hidden="true">@</span>} onClick={() => onMention(participant)}>{compact ? null : '提及'}</Button></Tooltip>}
      {!participant.isCurrentUser && onPrivateChat && <Tooltip placement="topRight" title={`私聊 ${participant.displayName}`}><Button type="text" size="small" aria-label={`私聊 ${participant.displayName}`} icon={<MessageOutlined />} onClick={() => onPrivateChat(participant)}>{compact ? null : '私聊'}</Button></Tooltip>}
    </div>
  </div>)}</div>
  return <section className={compact ? 'participant-list participant-list-compact' : 'participant-list'}><div className="participant-list-heading"><Typography.Title level={4}>参与者</Typography.Title>{onInvite && <Button type="text" aria-label="邀请入会" title="邀请入会" icon={<UserAddOutlined />} onClick={onInvite} />}</div>
    {members.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无参与者" /> : <>
      <Typography.Text type="secondary" className="participant-group-label">在线 · {online.length}</Typography.Text>{rows(online)}
      {offline.length > 0 && <Collapse ghost size="small" items={[{ key: 'offline', label: `离线 · ${offline.length}`, children: rows(offline) }]} />}
    </>}
  </section>
}
