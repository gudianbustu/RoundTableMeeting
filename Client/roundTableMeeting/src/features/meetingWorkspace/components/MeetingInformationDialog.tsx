import { useState } from 'react'
import { Alert, Form, Input, Modal } from 'antd'
import { getApiClient } from '@/api/client/apiClient'
import { meetingError } from '../api/helpers'
import type { MeetingDetails } from '../types/meetingWorkspace'

export function MeetingInformationDialog({ meeting, onClose, onSaved }: {
  meeting: MeetingDetails
  onClose: () => void
  onSaved: () => void
}) {
  const editable = meeting.currentParticipant?.isCreator && meeting.membershipState !== 'left'
  const [name, setName] = useState(meeting.name)
  const [description, setDescription] = useState(meeting.description ?? '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const save = async () => {
    if (!editable || saving || !name.trim()) return
    setSaving(true)
    try {
      await getApiClient().api.meetings.byMeetingId(meeting.meetingId as never).information.put({ name: name.trim(), description: description.trim() || null })
      onSaved()
      onClose()
    } catch (reason) { setError(meetingError(reason).message) }
    finally { setSaving(false) }
  }
  return <Modal open title={editable ? '编辑会议信息' : '会议信息'} onCancel={onClose} onOk={() => void save()}
    okText="保存" cancelText="取消" footer={editable ? undefined : null}
    confirmLoading={saving} okButtonProps={{ disabled: !name.trim() }}>
    {error && <Alert type="error" showIcon title={error} />}
    {editable ? <Form layout="vertical">
      <Form.Item label="会议名称" htmlFor="meeting-information-name" required>
        <Input id="meeting-information-name" value={name} maxLength={128} onChange={event => setName(event.target.value)} />
      </Form.Item>
      <Form.Item label="会议介绍" htmlFor="meeting-information-description">
        <Input.TextArea id="meeting-information-description" value={description} maxLength={2000} rows={4} onChange={event => setDescription(event.target.value)} />
      </Form.Item>
    </Form> : <><strong>{meeting.name}</strong><p style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{meeting.description || '暂无会议介绍'}</p></>}
  </Modal>
}
