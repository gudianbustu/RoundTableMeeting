import { Button } from 'antd'
import { CopyOutlined, ReloadOutlined } from '@ant-design/icons'
import type { MeetingDetails, MeetingInvitation } from '../types/meetingWorkspace'
export function MeetingHeader({ meeting, invitation, onCopy, onRefresh }: { meeting: MeetingDetails; invitation?: MeetingInvitation; onCopy: () => void; onRefresh: () => void; }) {
  return <section className="meeting-actions" aria-label="会议操作">
    <div className="meeting-action-buttons">
      <Button type="text" aria-label="刷新会议" title="刷新会议" icon={<ReloadOutlined />} onClick={onRefresh} />
    </div>
    {meeting.canManageInvitation && <div className="meeting-code-row">
      <span className="meeting-code-text" title={invitation?.meetingCode}>{invitation?.meetingCode ?? '会议码加载中…'}</span>
      <Button type="text" aria-label="复制邀请码" title="复制邀请码" disabled={!invitation} icon={<CopyOutlined />} onClick={onCopy} />
    </div>}
  </section>
}
