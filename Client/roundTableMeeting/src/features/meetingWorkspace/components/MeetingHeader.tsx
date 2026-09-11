import { Button, Dropdown, Popover } from 'antd'
import { CopyOutlined, DeleteOutlined, EllipsisOutlined, InfoCircleOutlined, ReloadOutlined, UserAddOutlined } from '@ant-design/icons'
import type { MeetingDetails, MeetingInvitation } from '../types/meetingWorkspace'
export function MeetingHeader({ meeting, invitation, onCopy, onInvite, onRefresh, onDelete }: { meeting: MeetingDetails; invitation?: MeetingInvitation; onCopy: () => void; onInvite: () => void; onRefresh: () => void; onDelete: () => void }) {
  return <section className="meeting-actions" aria-label="会议操作">
    <div className="meeting-action-buttons">
      {meeting.canManageInvitation && <Button type="text" aria-label="邀请入会" title="邀请入会" icon={<UserAddOutlined />} onClick={onInvite} />}
      <Button type="text" aria-label="刷新会议" title="刷新会议" icon={<ReloadOutlined />} onClick={onRefresh} />
      {meeting.canDelete && <Dropdown trigger={['click']} placement="topLeft" menu={{ items: [{ key: 'delete', label: '删除会议', icon: <DeleteOutlined />, danger: true }], onClick: ({ key }) => { if (key === 'delete') onDelete() } }}>
        <Button type="text" aria-label="更多会议操作" title="更多会议操作" icon={<EllipsisOutlined />} />
      </Dropdown>}
      {meeting.description && <Popover trigger="click" title="会议描述" content={<div className="meeting-description">{meeting.description}</div>}><Button type="text" aria-label="会议描述" title="会议描述" icon={<InfoCircleOutlined />} /></Popover>}
    </div>
    {meeting.canManageInvitation && <div className="meeting-code-row">
      <span className="meeting-code-text" title={invitation?.meetingCode}>{invitation?.meetingCode ?? '会议码加载中…'}</span>
      <Button type="text" aria-label="复制邀请码" title="复制邀请码" disabled={!invitation} icon={<CopyOutlined />} onClick={onCopy} />
    </div>}
  </section>
}
