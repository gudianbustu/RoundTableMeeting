import { useEffect, useState } from 'react'
import { EditOutlined } from '@ant-design/icons'
import { Alert, Button, Form, Input, Modal, Typography } from 'antd'
import { getApiClient } from '@/api/client/apiClient'
import { meetingError } from '../api/helpers'
import type { MeetingDetails } from '../types/meetingWorkspace'
import { DiscussionModeControl } from './DiscussionModeControl'

export function MeetingInformationPanel({ meeting, onSaved }: {
  meeting: MeetingDetails
  onSaved: () => void
}) {
  const editable = meeting.currentParticipant?.isCreator && meeting.membershipState !== 'left'
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(meeting.name)
  const [description, setDescription] = useState(meeting.description ?? '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    setEditing(false)
    setName(meeting.name)
    setDescription(meeting.description ?? '')
    setError('')
  }, [meeting.meetingId, meeting.name, meeting.description])
  const save = async () => {
    if (!editable || saving || !name.trim()) return
    setSaving(true)
    setError('')
    try {
      await getApiClient().api.meetings.byMeetingId(meeting.meetingId as never).information.put({ name: name.trim(), description: description.trim() || null })
      setEditing(false)
      onSaved()
    } catch (reason) { setError(meetingError(reason).message) }
    finally { setSaving(false) }
  }
  const cancel = () => {
    setName(meeting.name)
    setDescription(meeting.description ?? '')
    setError('')
    setEditing(false)
  }
  return <div className="meeting-info-panel">
    <section className="meeting-info-card">
      <div className="meeting-info-card-head">
        <Typography.Text type="secondary" className="meeting-info-label">基本信息</Typography.Text>
        {editable && !editing && <Button type="text" size="small" icon={<EditOutlined />} onClick={() => setEditing(true)}>编辑</Button>}
      </div>
      {error && <Alert type="error" showIcon title={error} className="meeting-info-error" />}
      {editing ? <Form layout="vertical" className="meeting-info-form">
        <Form.Item label="会议名称" htmlFor="meeting-information-name" required>
          <Input id="meeting-information-name" size="small" value={name} maxLength={128} onChange={event => setName(event.target.value)} />
        </Form.Item>
        <Form.Item label="会议介绍" htmlFor="meeting-information-description">
          <Input.TextArea id="meeting-information-description" value={description} maxLength={2000} autoSize={{ minRows: 3, maxRows: 8 }} onChange={event => setDescription(event.target.value)} />
        </Form.Item>
        <div className="meeting-info-form-actions">
          <Button size="small" disabled={saving} onClick={cancel}>取消</Button>
          <Button size="small" type="primary" loading={saving} disabled={!name.trim()} onClick={() => void save()}>保存</Button>
        </div>
      </Form> : <div className="meeting-info-readonly">
        <strong className="meeting-info-name">{meeting.name}</strong>
        <p className="meeting-info-desc">{meeting.description || '暂无会议介绍'}</p>
      </div>}
    </section>
    <section className="meeting-info-card">
      <DiscussionModeControl meeting={meeting} onModeChanged={onSaved} />
    </section>
  </div>
}

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
