import { Avatar, Empty, Flex, Tag, Typography } from 'antd'
import type { Participant } from '../types/meetingWorkspace'

export function ParticipantList({ participants }: { participants: Participant[] }) {
  return <section className="participant-list"><Typography.Title level={4}>参与者</Typography.Title>{participants.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无参与者" /> : <div className="participant-list-items">{participants.map(participant => <Flex key={participant.participantId} align="center" gap="small" className="participant-list-item"><Avatar>{participant.displayName.slice(0, 1)}</Avatar><div><Typography.Text strong>{participant.displayName}</Typography.Text><div>{participant.isCurrentUser && <Tag>我</Tag>}{participant.isCreator && <Tag color="gold">创建者</Tag>}<Typography.Text type="secondary">{participant.type === 'agent' ? 'AI Agent' : '人类参与者'}</Typography.Text></div></div></Flex>)}</div>}</section>
}
