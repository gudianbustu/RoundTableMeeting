import { useState } from 'react'
import { Alert, Button, Input, Modal } from 'antd'
import { renameMeetingSelf } from '../api/renameMeetingSelf'
import type { MeetingWorkspaceApiError } from '../types/meetingWorkspace'

export function MeetingNicknameDialog({ meetingId, meetingName, name, onClose, onSaved }: {
  meetingId: string; meetingName: string; name: string; onClose: () => void; onSaved: () => void
}) {
  const [value, setValue] = useState(name)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  async function save() {
    if (busy || !value.trim()) return
    setBusy(true)
    try {
      await renameMeetingSelf(meetingId, value.trim())
      onSaved()
      onClose()
    } catch (failure) {
      const issue = failure as MeetingWorkspaceApiError
      setError(issue.code === 'display_name_taken' ? '本场会议已有参与者使用这个昵称。' : issue.message)
      setBusy(false)
    }
  }
  return <Modal width={535} centered open className="meeting-form-modal" title="修改会议昵称" onCancel={busy ? undefined : onClose}
    footer={<><Button disabled={busy} onClick={onClose}>取消</Button><Button type="primary" loading={busy} disabled={!value.trim()} onClick={() => void save()}>保存昵称</Button></>}>
    <p>{meetingName}</p>
    <label htmlFor="meeting-self-name">我的会议昵称</label>
    <Input id="meeting-self-name" autoFocus maxLength={64} value={value} disabled={busy}
      onChange={event => { setValue(event.target.value); setError('') }} onPressEnter={() => void save()} />
    <p>本场历史消息也将显示新昵称。其他会议各自保留原昵称。</p>
    {error && <Alert type="error" showIcon title={error} />}
  </Modal>
}
