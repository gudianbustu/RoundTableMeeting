import { Avatar, Empty, Flex, Tag, Typography } from 'antd'
import type { AgentActivity } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'

function agentStatus(activity: AgentActivity | undefined) {
  if (!activity || activity.state === 'unknown') return { online: false, label: '实时状态未知', color: 'default' as const }
  if (activity.reason === 'activating') return { online: false, label: '连接建立中', color: 'default' as const }
  if (activity.state === 'disconnected') return { online: false, label: '连接中断', color: 'default' as const }
  if (activity.reason === 'initial_context_unconfirmed' || activity.reason === 'awaiting_context_confirmation') return { online: true, label: '等待接收确认', color: 'orange' as const }
  if (activity.reason === 'retry_pending') return { online: true, label: '重试等待', color: 'orange' as const }
  if (activity.reason === 'retry_exhausted') return { online: true, label: '任务失败，等待重试', color: 'red' as const }
  if (activity.state === 'waiting') return { online: true, label: '等待中', color: 'green' as const }
  if (activity.state === 'processing') return { online: true, label: '处理中', color: 'blue' as const }
  if (activity.state === 'blocked') return { online: true, label: '已阻塞', color: 'orange' as const }
  return { online: false, label: undefined, color: 'default' as const }
}

export function ParticipantList({ participants, presence = {}, agentActivities = {} }: { participants: Participant[]; presence?: Record<string, 'online' | 'offline'>; agentActivities?: Record<string, AgentActivity> }) {
  return <section className="participant-list">
    <Typography.Title level={4}>参与者</Typography.Title>
    {participants.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无参与者" /> :
      <div className="participant-list-items">{participants.map(participant => {
        const agent = participant.type === 'agent'
        const activity = agentActivities[participant.participantId]
        const status = agentStatus(activity)
        const humanOnline = presence[participant.participantId] === 'online'
        return <Flex key={participant.participantId} align="center" gap="small" className="participant-list-item">
          <Avatar>{participant.displayName.slice(0, 1)}</Avatar>
          <div className="participant-list-item-content"><Typography.Text strong>{participant.displayName}</Typography.Text><div>
            {participant.isCurrentUser && <Tag>我</Tag>}{participant.isCreator && <Tag color="gold">创建者</Tag>}
            <Tag color={agent ? status.online ? 'green' : 'default' : humanOnline ? 'green' : 'default'}>
              {agent ? status.online ? '在线' : status.label ?? '离线' : humanOnline ? '在线' : '离线'}
            </Tag>
            {agent && status.online && status.label && <Tag color={status.color}>{status.label}</Tag>}
            {agent && activity?.state !== 'unknown' && activity?.connectionDisposition === 'taken_over' && <Tag color="blue">已由新连接接管</Tag>}
            <Typography.Text type="secondary">{agent ? 'AI Agent' : '人类参与者'}</Typography.Text>
          </div></div>
        </Flex>
      })}</div>}
  </section>
}
