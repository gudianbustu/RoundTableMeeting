import { useEffect, useState } from 'react'
import { Alert, Button, Modal, Spin } from 'antd'
import { CloseOutlined, FileOutlined } from '@ant-design/icons'
import type { ChatAttachment } from '../types/realtimeChat'
import AttachmentPreview from './AttachmentPreview'
import { attachmentPreviewKind } from './AttachmentCard'


function DraftPreview({ file }: { file: ChatAttachment }) {
  const [url, setUrl] = useState<string>()
  useEffect(() => {
    if (!file.localPreviewFile) return
    const preview = URL.createObjectURL(file.localPreviewFile)
    setUrl(preview)
    return () => URL.revokeObjectURL(preview)
  }, [file.localPreviewFile])
  if (!file.localPreviewFile) return <Alert type="info" title="此旧版附件未保留本地文件，请重新选择。" />
  return url ? <AttachmentPreview file={file} url={url} /> : <Spin />
}
function DraftImage({ file }: { file: ChatAttachment }) {
  const [url, setUrl] = useState<string>()
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    setFailed(false)
    if (!file.localPreviewFile) return
    const preview = URL.createObjectURL(file.localPreviewFile)
    setUrl(preview)
    return () => URL.revokeObjectURL(preview)
  }, [file.localPreviewFile])
  return url && !failed ? <img src={url} alt={file.name} onError={() => setFailed(true)} /> : <span className="draft-image-fallback">图片预览不可用</span>
}

export function DraftAttachments({ attachments, disabled, onChange }: { attachments: ChatAttachment[]; disabled?: boolean; onChange: (files: ChatAttachment[]) => void }) {
  const [previewId, setPreviewId] = useState<string>()
  const preview = attachments.find(file => file.attachmentId === previewId)
  if (!attachments.length) return null
  return <><div className="draft-attachments" role="list" aria-label="待发送附件">
    {attachments.map(file => <div key={file.attachmentId} role="listitem" title={`${file.name} · ${file.sizeBytes.toLocaleString()} 字节`} className={`draft-attachment ${attachmentPreviewKind(file) === 'image' ? 'draft-attachment-image' : 'draft-attachment-file'}`}>
      <button type="button" className="draft-attachment-preview" aria-label={`预览附件 ${file.name}`} onClick={() => setPreviewId(file.attachmentId)}>
      {attachmentPreviewKind(file) === 'image' ? <DraftImage file={file} /> : <FileOutlined className="draft-file-icon" />}
      </button>
      <Button className="draft-attachment-remove" type="text" size="small" icon={<CloseOutlined />} aria-label={`移除附件 ${file.name}`} disabled={disabled} onClick={() => onChange(attachments.filter(item => item.attachmentId !== file.attachmentId))} />
    </div>)}
  </div><Modal open={!!preview} title={preview?.name} footer={null} destroyOnHidden onCancel={() => setPreviewId(undefined)} width="min(900px, 92vw)">
    {preview && <DraftPreview key={preview.attachmentId} file={preview} />}
  </Modal></>
}
