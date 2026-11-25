import { useEffect, useImperativeHandle, useRef, useState, type Ref } from 'react'
import { Alert, Button, Drawer, Modal, Spin, Tabs, Tooltip, Typography } from 'antd'
import { DownloadOutlined, FolderOpenOutlined, PaperClipOutlined } from '@ant-design/icons'
import { createUuid } from '@/shared/utils/createUuid'
import { attachmentUrl, getCommunicationCapabilities, listAttachments } from '../api/messageResources'
import type { MessagePageRequest } from '../api/listMessages'
import type { ChatAttachment } from '../types/realtimeChat'
import { AttachmentViewer } from './ConversationAttachmentViewer'
import AttachmentPreview from './AttachmentPreview'
import AttachmentCard, { attachmentPreviewKind } from './AttachmentCard'


const sizeLabel = (bytes: number) => bytes >= 1_000_000 ? `${(bytes / 1_000_000).toFixed(1)} MB` : `${bytes} 字节`
export type AttachmentUploadHandle = { addFiles: (files: File[]) => void }
export function AttachmentUpload({ uploadRef, attachments, disabled, onChange }: { uploadRef?: Ref<AttachmentUploadHandle>; attachments: ChatAttachment[]; disabled?: boolean; onChange: (files: ChatAttachment[]) => void }) {
  const input = useRef<HTMLInputElement>(null)
  const currentAttachments = useRef(attachments)
  currentAttachments.current = attachments
  const [limit, setLimit] = useState<number>()
  const [error, setError] = useState('')
  useEffect(() => {
    const current = new AbortController()
    void getCommunicationCapabilities(current.signal).then(value => { if (!current.signal.aborted) setLimit(value.maxFileBytes) }).catch(() => { if (!current.signal.aborted) setError('无法读取附件大小限制，请重新打开此交流区域后重试。') })
    return () => current.abort()
  }, [])
  const addFiles = (files: File[]) => {
    if (disabled || !files.length) return
    if (!limit) { setError('附件大小限制尚未就绪，请稍后重新添加。'); return }
    const slots = Math.max(0, 20 - currentAttachments.current.length)
    const accepted: ChatAttachment[] = []; const rejected: string[] = []
    for (const file of files) {
      if (file.size > limit) { rejected.push(`${file.name} 超过附件大小限制 ${sizeLabel(limit)}`); continue }
      if (accepted.length >= slots) { rejected.push('每条消息最多附带 20 个文件'); break }
      const id = createUuid()
      const mediaType = ['image/png', 'image/jpeg', 'image/gif', 'image/webp', 'application/pdf'].includes(file.type) ? file.type : 'application/octet-stream'
      accepted.push({ attachmentId: id, uploadRequestId: id, name: file.name, sizeBytes: file.size, mediaType, state: 'local', localPreviewFile: file })
    }
    setError(rejected.join('；'))
    if (accepted.length) { const next = [...currentAttachments.current, ...accepted]; currentAttachments.current = next; onChange(next) }
  }
  useImperativeHandle(uploadRef, () => ({ addFiles }))
  return <div className="attachment-upload">
    <Tooltip title={limit ? `添加附件 · 单文件上限 ${sizeLabel(limit)} · 发送时上传` : '正在读取附件大小限制'}><Button type="text" aria-label="添加附件" icon={<PaperClipOutlined />} disabled={disabled || !limit} onClick={() => input.current?.click()} /></Tooltip>
    <input ref={input} type="file" multiple hidden aria-label="选择附件" disabled={disabled || !limit} onChange={event => { const files = Array.from(event.target.files ?? []); event.target.value = ''; addFiles(files) }} />
    {error && <Typography.Text type="danger" role="alert">{error}</Typography.Text>}
  </div>
}
export function AttachmentLinks({ meetingId, attachments, onOpenViewer }: { meetingId: string; attachments: ChatAttachment[]; onOpenViewer?: (id: string) => void }) {
  const [preview, setPreview] = useState<ChatAttachment>()
  return <div className="message-attachments">
    {attachments.map(file => <AttachmentCard key={file.attachmentId} meetingId={meetingId} file={file} onPreview={() => {
      const kind = attachmentPreviewKind(file)
      if (onOpenViewer && (kind === 'image' || (kind === 'text' && /\.md$/i.test(file.name)))) onOpenViewer(file.attachmentId)
      else setPreview(file)
    }} />)}
    <Modal open={!!preview} title={preview?.name} footer={preview ? <Button icon={<DownloadOutlined />} href={attachmentUrl(meetingId, preview.attachmentId)} target="_blank" rel="noopener noreferrer">下载</Button> : null} destroyOnHidden onCancel={() => setPreview(undefined)} width="min(900px, 92vw)">
      {preview && <AttachmentPreview key={preview.attachmentId} file={preview} url={attachmentUrl(meetingId, preview.attachmentId, true)} />}
    </Modal>
  </div>
}
export function MeetingAttachmentList({ meetingId, scope, peerParticipantId, onLocate }: { meetingId: string; scope: 'public' | 'private'; peerParticipantId?: string; onLocate: (id: string) => Promise<void> }) {
  const [previewId, setPreviewId] = useState<string>()
  const [open, setOpen] = useState(false)
  const [items, setItems] = useState<Awaited<ReturnType<typeof listAttachments>>['items']>([])
  const [before, setBefore] = useState<number>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const controller = useRef<AbortController | undefined>(undefined)
  const load = async (append = false) => {
    controller.current?.abort(); const current = new AbortController(); controller.current = current; setBusy(true); setError('')
    try {
      const request: MessagePageRequest = { scope, peerParticipantId, limit: 50, beforeSequence: append ? before : undefined }
      const page = await listAttachments(meetingId, request, current.signal)
      if (!current.signal.aborted) { setItems(previous => append ? [...previous, ...page.items] : page.items); setBefore(page.nextBeforeSequence) }
    } catch { if (!current.signal.aborted) setError('附件列表加载失败。') }
    finally { if (!current.signal.aborted) setBusy(false) }
  }
  useEffect(() => {
    setPreviewId(undefined); setItems([]); setBefore(undefined); setOpen(false)
    return () => controller.current?.abort()
  }, [meetingId, scope, peerParticipantId])
  return <><Tooltip title="会议附件"><Button type="text" aria-label="会议附件" icon={<FolderOpenOutlined />} onClick={() => { setOpen(true); void load() }} /></Tooltip><Drawer title={scope === 'private' ? '此私聊的附件' : '公开讨论附件'} open={open} onClose={() => setOpen(false)}>
    {error && <Alert type="error" title={error} action={<Button onClick={() => void load()}>重试</Button>} />}
    <Tabs defaultActiveKey="files" destroyOnHidden items={[
      { key: 'files', label: '文件' }, { key: 'images', label: '图片' },
    ].map(tab => {
      const filtered = items.filter(item => (attachmentPreviewKind(item.attachment) === 'image') === (tab.key === 'images'))
      return { ...tab, children: <>
        {filtered.map(item => <article className="meeting-attachment-row" key={item.attachment.attachmentId}><AttachmentLinks meetingId={meetingId} attachments={[item.attachment]} onOpenViewer={setPreviewId} /><Typography.Text type="secondary">{item.author} · {item.createdAt.toLocaleString()}</Typography.Text><Button type="link" onClick={() => { void onLocate(item.messageId).then(() => setOpen(false)).catch(() => setError('原消息暂时无法定位，请重试。')) }}>跳转原消息</Button></article>)}
        {!busy && !error && !filtered.length && <Typography.Text type="secondary">{before !== undefined ? `当前已加载内容中暂无${tab.label}，可继续加载更早附件。` : `此交流范围还没有${tab.label}`}</Typography.Text>}
      </> }
    })} />
    {busy && <Spin />}{before !== undefined && <Button loading={busy} onClick={() => void load(true)}>加载更早附件</Button>}
  </Drawer>
    {previewId && <AttachmentViewer key={previewId} initialId={previewId} meetingId={meetingId} scope={scope} peerParticipantId={peerParticipantId}
      beforeSequence={before} entries={items.map(item => ({ file: item.attachment, author: item.author, sequence: item.sequence }))}
      onClose={() => setPreviewId(undefined)} />}
  </>
}
