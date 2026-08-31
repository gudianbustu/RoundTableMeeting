import { Alert, Button, Flex, Input, Modal, Typography } from 'antd'
import { useEffect, useState } from 'react'
import type { JoinPreview } from '../types/meetingWorkspace'

const guidD = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
export function JoinMeetingDialog({ open, loading, succeeded, previewLoading, preview, error, retryAfterSeconds, onCancel, onPreview, onSubmit }: { open: boolean; loading?: boolean; succeeded?: boolean; previewLoading?: boolean; preview?: JoinPreview; error?: string; retryAfterSeconds?: number; onCancel: () => void; onPreview: (code: string) => void; onSubmit: (value: { code: string; displayName: string }) => void }) {
  const [code, setCode] = useState(''); const [name, setName] = useState(''); const normalizedCode = code.trim().toLowerCase(); const codeValid = guidD.test(normalizedCode)
  useEffect(() => { if (!open || succeeded) { setCode(''); setName('') } }, [open, succeeded])
  useEffect(() => { if (preview) setName(preview.existingDisplayName ?? preview.defaultDisplayName) }, [preview])
  return <Modal open={open} title="加入会议" okText={preview?.alreadyJoined ? '打开会议' : '加入'} cancelText="取消" confirmLoading={loading} okButtonProps={{ disabled: !preview || (!preview.alreadyJoined && !name.trim()) }} onCancel={onCancel} onOk={() => onSubmit({ code: normalizedCode, displayName: name })}>
    <Flex vertical gap="small" style={{ width: '100%' }}><Input value={code} placeholder="输入标准 GUID 会议码" onChange={event => setCode(event.target.value)} onBlur={() => { if (codeValid) setCode(normalizedCode) }} onPressEnter={() => { if (codeValid) onPreview(normalizedCode) }} /><Button loading={previewLoading} disabled={!codeValid} onClick={() => onPreview(normalizedCode)}>检查会议码</Button>{code && !codeValid && <Alert type="error" showIcon title="请输入标准 GUID D 格式会议码" />}{error && <Alert type="error" showIcon title={error} description={retryAfterSeconds ? `${retryAfterSeconds} 秒后可以重试` : undefined} />}{preview && <><Typography.Text strong>{preview.meetingName}</Typography.Text>{preview.description && <Typography.Text type="secondary">{preview.description}</Typography.Text>}<Input value={name} maxLength={64} disabled={preview.alreadyJoined} onChange={event => setName(event.target.value)} placeholder="会议内显示名称" /></>}</Flex>
  </Modal>
}
