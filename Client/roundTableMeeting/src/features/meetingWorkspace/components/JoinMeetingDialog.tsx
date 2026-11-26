import { Alert, Button, Flex, Input, Modal, Typography } from 'antd'
import { useEffect, useState } from 'react'
import type { JoinPreview } from '../types/meetingWorkspace'

export const meetingCodePattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
type Props = {
  initialCode?: string; open: boolean; loading?: boolean; succeeded?: boolean; previewLoading?: boolean; preview?: JoinPreview
  error?: string; errorCode?: string; retryAfterSeconds?: number; onCancel: () => void; onCodeChange?: () => void
  onPreview: (code: string) => void; onSubmit: (value: { code: string; displayName: string }) => void
}

export function JoinMeetingDialog({ initialCode, open, loading, succeeded, previewLoading, preview, error, errorCode, retryAfterSeconds, onCancel, onCodeChange, onPreview, onSubmit }: Props) {
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [retryBlocked, setRetryBlocked] = useState(false)
  useEffect(() => {
    if (!open || !retryAfterSeconds) { setRetryBlocked(false); return }
    setRetryBlocked(true)
    const timer = window.setTimeout(() => setRetryBlocked(false), retryAfterSeconds * 1000)
    return () => window.clearTimeout(timer)
  }, [open, retryAfterSeconds, error])
  useEffect(() => {
    if (open && initialCode) setCode(initialCode)
  }, [open, initialCode])
  const normalizedCode = code.trim().toLowerCase()
  useEffect(() => { if (!open || succeeded) { setCode(''); setName('') } }, [open, succeeded])
  useEffect(() => { if (preview) setName(preview.existingDisplayName ?? preview.defaultDisplayName) }, [preview])
  function changeCode(value: string) {
    setCode(value)
    const next = value.trim().toLowerCase()
    if (next === normalizedCode) return
    setName('')
    onCodeChange?.()
    if (meetingCodePattern.test(next)) onPreview(next)
  }
  return <Modal width={600} centered className="meeting-form-modal join-form-modal" open={open} title="加入会议" okText={preview?.alreadyJoined ? '打开会议' : '加入会议'} cancelText="取消"
    confirmLoading={loading} okButtonProps={{ disabled: !meetingCodePattern.test(normalizedCode) || !preview || retryBlocked || previewLoading || (!preview.alreadyJoined && !name.trim()) }}
    onCancel={onCancel} onOk={() => onSubmit({ code: normalizedCode, displayName: name })}>
    <Flex vertical gap="small">
      <label htmlFor="join-meeting-code">会议码</label>
      <Input id="join-meeting-code" status={error ? 'error' : undefined} value={code} disabled={loading} placeholder="输入标准 GUID 会议码" onChange={event => changeCode(event.target.value)} />
      {previewLoading && <Typography.Text role="status">正在查找会议…</Typography.Text>}
      {code.trim().length >= 36 && !meetingCodePattern.test(normalizedCode) && <Alert type="error" title="会议码格式有误，请检查后重试" />}
      {error && errorCode === 'meeting_not_found' && <span className="join-code-error" role="alert">没有找到对应会议，请核对会议码。</span>}
      {error && errorCode !== 'meeting_not_found' && <Alert type="error" showIcon title={error} description={retryBlocked && retryAfterSeconds ? retryAfterSeconds + ' 秒后可以重试' : undefined}
        action={<Button size="small" disabled={!meetingCodePattern.test(normalizedCode) || retryBlocked || previewLoading || loading} onClick={() => preview ? onSubmit({ code: normalizedCode, displayName: name }) : onPreview(normalizedCode)}>重试</Button>} />}
      {preview && <>
        <div className="join-meeting-preview">
          <strong>{preview.meetingName}</strong>
          {preview.alreadyJoined ? <p>你已加入这场会议</p> : preview.description && <p>{preview.description}</p>}
        </div>
        <label htmlFor="join-display-name">我的会议昵称</label>
        <Input id="join-display-name" disabled={preview.alreadyJoined || loading} value={name} maxLength={64} onChange={event => setName(event.target.value)} />
      </>}
    </Flex>
  </Modal>
}
