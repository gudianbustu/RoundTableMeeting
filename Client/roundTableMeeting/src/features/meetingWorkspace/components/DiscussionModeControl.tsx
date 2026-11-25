import { useState } from 'react'
import { CheckOutlined } from '@ant-design/icons'
import { Select, message } from 'antd'
import { getApiClient } from '@/api/client/apiClient'
import { meetingError } from '../api/helpers'
import type { MeetingDetails } from '../types/meetingWorkspace'
import './DiscussionModeControl.css'

const modes = [{ value: 'directed', label: '点名讨论' }, { value: 'free', label: '自由讨论' }]

export function DiscussionModeControl({ meeting, onModeChanged }: { meeting: MeetingDetails; onModeChanged: () => void }) {
  const [saving, setSaving] = useState(false)
  const [feedback, feedbackHolder] = message.useMessage()
  const setMode = async (mode: string) => {
    setSaving(true)
    try {
      await getApiClient().api.meetings.byMeetingId(meeting.meetingId as never).discussionMode.put({ mode })
      onModeChanged()
    } catch (error) { void feedback.error(meetingError(error).message) }
    finally { setSaving(false) }
  }
  const current = meeting.freeDiscussion ? 'free' : 'directed'
  const editable = meeting.freeDiscussion !== undefined && meeting.currentParticipant?.isCreator && meeting.membershipState !== 'left'
  return <div className="discussion-mode-control">
    {feedbackHolder}
    <span>讨论模式</span>
    {editable ? <Select aria-label="讨论模式" value={current} options={modes} size="small"
      classNames={{ popup: { root: 'discussion-mode-options' } }} virtual={false}
      loading={saving} disabled={saving} onChange={value => void setMode(value)}
      optionRender={option => <span className="discussion-mode-option">{option.label}{option.value === current && <CheckOutlined aria-hidden />}</span>} />
      : <strong>{meeting.freeDiscussion === undefined ? '未知' : (meeting.freeDiscussion ? '自由讨论' : '点名讨论')}</strong>}
  </div>
}
