import { useState } from 'react'
import { Button, Select, Typography, message } from 'antd'
import { getApiClient } from '@/api/client/apiClient'
import { meetingError } from '../api/helpers'
import { CopyOutlined, ReloadOutlined } from '@ant-design/icons'
import type { MeetingDetails, MeetingInvitation } from '../types/meetingWorkspace'
export function MeetingHeader({ meeting, invitation, onCopy, onRefresh, onModeChanged }: { meeting: MeetingDetails; invitation?: MeetingInvitation; onCopy: () => void; onRefresh: () => void; onModeChanged?: () => void; }) {
  const [saving, setSaving] = useState(false)
  const [feedback, feedbackHolder] = message.useMessage()
  const setMode = async (mode: string) => {
    setSaving(true)
    try {
      await getApiClient().api.meetings.byMeetingId(meeting.meetingId as never).discussionMode.put({ mode })
      onModeChanged?.()
    } catch (error) { void feedback.error(meetingError(error).message) }
    finally { setSaving(false) }
  }
  return <section className="meeting-actions" aria-label="会议操作">
    {feedbackHolder}
    <div className="meeting-action-buttons">
      {meeting.freeDiscussion !== undefined && meeting.currentParticipant?.isCreator && meeting.membershipState !== 'left'
        ? <Select aria-label="讨论模式" size="small" value={meeting.freeDiscussion ? 'free' : 'directed'} loading={saving} disabled={saving}
            onChange={value => void setMode(value)} options={[{ value: 'directed', label: '点名讨论' }, { value: 'free', label: '自由讨论' }]} />
        : <Typography.Text>{meeting.freeDiscussion ? '自由讨论' : '点名讨论'}</Typography.Text>}
      <Button type="text" aria-label="刷新会议" title="刷新会议" icon={<ReloadOutlined />} onClick={onRefresh} />
    </div>
    {meeting.canManageInvitation && <div className="meeting-code-row">
      <span className="meeting-code-text" title={invitation?.meetingCode}>{invitation?.meetingCode ?? '会议码加载中…'}</span>
      <Button type="text" aria-label="复制邀请码" title="复制邀请码" disabled={!invitation} icon={<CopyOutlined />} onClick={onCopy} />
    </div>}
  </section>
}
