import { useEffect, useImperativeHandle, useRef, useState, type Ref } from 'react'
import { Alert, Button, Drawer, Modal, Spin, Tabs, Tooltip, Typography } from 'antd'
import { AimOutlined, DownloadOutlined, FolderOpenOutlined, PaperClipOutlined } from '@ant-design/icons'
import { createUuid } from '@/shared/utils/createUuid'
import { attachmentUrl, getCommunicationCapabilities, listAttachments } from '../api/messageResources'
import type { MessagePageRequest } from '../api/listMessages'
import type { ChatAttachment } from '../types/realtimeChat'
import { AttachmentViewer } from './ConversationAttachmentViewer'
import AttachmentPreview from './AttachmentPreview'
import AttachmentCard, { attachmentPreviewKind } from './AttachmentCard'
import { decodePreview, isTextAttachment } from './TextAttachmentPreview'


const sizeLabel = (bytes: number) => bytes >= 1_000_000 ? `${(bytes / 1_000_000).toFixed(1)} MB` : bytes >= 1024 ? `${Math.round(bytes / 1024)} KB` : `${bytes} B`

function fileExtension(name: string) {
  const match = /\.([a-zA-Z0-9]{1,5})$/.exec(name.trim())
  return match ? match[1].toLowerCase() : 'file'
}

function fileBadgeTone(ext: string) {
  if (ext === 'pdf') return 'tone-red'
  if (['md', 'doc', 'docx', 'txt'].includes(ext)) return 'tone-blue'
  if (['json', 'ts', 'tsx', 'js', 'py', 'cs', 'yaml', 'yml'].includes(ext)) return 'tone-teal'
  if (['zip', 'rar', '7z', 'tar', 'gz'].includes(ext)) return 'tone-amber'
  return 'tone-slate'
}

function formatShortTime(date: Date) {
  return date.toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })
}

function summarizeTextPreview(raw: string) {
  const lines = raw
    .split(/\r?\n/)
    .map(line => line.trim().replace(/^#{1,6}\s+/, '').replace(/^[-*+]\s+/, ''))
    .filter(Boolean)
  return lines.slice(0, 4).join(' · ')
}

function CompactFileSnippet({ url, size }: { url: string; size: number }) {
  const host = useRef<HTMLSpanElement>(null)
  const [visible, setVisible] = useState(false)
  const [snippet, setSnippet] = useState<string>()
  useEffect(() => {
    if (!host.current) return
    if (!('IntersectionObserver' in window)) { setVisible(true); return }
    const observer = new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting) { setVisible(true); observer.disconnect() }
    }, { rootMargin: '120px' })
    observer.observe(host.current)
    return () => observer.disconnect()
  }, [])
  useEffect(() => {
    if (!visible) return
    const controller = new AbortController()
    void (async () => {
      const limit = 4096
      const headers = size === 0 || url.startsWith('data:') ? undefined : { Range: `bytes=0-${limit - 1}` }
      const response = await fetch(url, { signal: controller.signal, credentials: 'same-origin', headers })
      if (!response.ok || response.status === 204 || !response.body) return
      const reader = response.body.getReader(); const bytes = new Uint8Array(limit); let length = 0
      try {
        while (length < limit) {
          const { done, value } = await reader.read(); if (done) break
          const part = value.subarray(0, limit - length); bytes.set(part, length); length += part.length
        }
      } finally { await reader.cancel() }
      if (!controller.signal.aborted) {
        const text = summarizeTextPreview(decodePreview(bytes.subarray(0, length), 'auto', size > limit).text)
        if (text) setSnippet(text)
      }
    })().catch(() => { /* silently omit snippet when unavailable */ })
    return () => controller.abort()
  }, [visible, url, size])
  return <span ref={host} className={snippet ? 'meeting-file-snippet' : 'meeting-file-snippet-probe'}>{snippet}</span>
}
export type AttachmentUploadHandle = { addFiles: (files: File[]) => boolean }
export function AttachmentUpload({ uploadRef, attachments, disabled, onChange, iconOnly = false }: { uploadRef?: Ref<AttachmentUploadHandle>; attachments: ChatAttachment[]; disabled?: boolean; onChange: (files: ChatAttachment[]) => void; iconOnly?: boolean }) {
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
    if (disabled || !files.length) return false
    if (!limit) { setError('附件大小限制尚未就绪，请稍后重新添加。'); return false }
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
    return accepted.length === files.length
  }
  useImperativeHandle(uploadRef, () => ({ addFiles }))
  return <div className={iconOnly ? 'attachment-upload attachment-upload-icon' : 'attachment-upload'}>
    <Tooltip title={limit ? `添加附件 · 单文件上限 ${sizeLabel(limit)} · 发送时上传` : '正在读取附件大小限制'}><Button type="text" aria-label="添加附件" icon={<PaperClipOutlined />} disabled={disabled || !limit} onClick={() => input.current?.click()}>{iconOnly ? null : '附件'}</Button></Tooltip>
    <input ref={input} type="file" multiple hidden aria-label="选择附件" disabled={disabled || !limit} onChange={event => { const files = Array.from(event.target.files ?? []); event.target.value = ''; addFiles(files) }} />
    {error && <Typography.Text type="danger" role="alert" className="attachment-upload-error">{error}</Typography.Text>}
  </div>
}
export function AttachmentLinks({ meetingId, attachments, onOpenViewer, compact = false }: { compact?: boolean; meetingId: string; attachments: ChatAttachment[]; onOpenViewer?: (id: string) => void }) {
  const [preview, setPreview] = useState<ChatAttachment>()
  const renderCard = (file: ChatAttachment) => <AttachmentCard compact={compact} key={file.attachmentId} meetingId={meetingId} file={file} onPreview={() => {
    const kind = attachmentPreviewKind(file)
    if (onOpenViewer && (kind === 'image' || (kind === 'text' && /\.md$/i.test(file.name)))) onOpenViewer(file.attachmentId)
    else setPreview(file)
  }} />
  const images = compact ? attachments.filter(file => attachmentPreviewKind(file) === 'image') : []
  const files = compact ? attachments.filter(file => attachmentPreviewKind(file) !== 'image') : attachments
  return <div className="message-attachments">
    {images.length > 0 && <div className="chat-image-group">{images.map(renderCard)}</div>}
    {files.map(renderCard)}
    <Modal open={!!preview} title={preview?.name} footer={preview ? <Button icon={<DownloadOutlined />} href={attachmentUrl(meetingId, preview.attachmentId)} target="_blank" rel="noopener noreferrer">下载</Button> : null} destroyOnHidden onCancel={() => setPreview(undefined)} width="min(900px, 92vw)">
      {preview && <AttachmentPreview key={preview.attachmentId} file={preview} url={attachmentUrl(meetingId, preview.attachmentId, true)} />}
    </Modal>
  </div>
}
export function MeetingAttachmentPanel({ meetingId, scope, peerParticipantId, onLocate, onClose, kind }: {
  meetingId: string; scope: 'public' | 'private'; peerParticipantId?: string; onLocate: (id: string) => Promise<void>; onClose?: () => void; kind?: 'files' | 'images'
}) {
  const [previewId, setPreviewId] = useState<string>()
  const [modalPreview, setModalPreview] = useState<ChatAttachment>()
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
    setPreviewId(undefined); setModalPreview(undefined); setItems([]); setBefore(undefined)
    void load()
    return () => controller.current?.abort()
  }, [meetingId, scope, peerParticipantId])
  const openFilePreview = (file: ChatAttachment) => {
    const previewKind = attachmentPreviewKind(file)
    if (previewKind === 'image' || (previewKind === 'text' && /\.md$/i.test(file.name))) setPreviewId(file.attachmentId)
    else setModalPreview(file)
  }
  const renderList = (tabKey: 'files' | 'images', label: string) => {
    const filtered = items.filter(item => (attachmentPreviewKind(item.attachment) === 'image') === (tabKey === 'images'))
    if (!busy && !error && !filtered.length) {
      return <Typography.Text type="secondary" className="meeting-attachment-empty">
        {before !== undefined ? `当前已加载内容中暂无${label}，可继续加载更早附件。` : `此交流范围还没有${label}`}
      </Typography.Text>
    }
    if (kind === 'images' && tabKey === 'images') {
      return <div className="meeting-image-grid">
        {filtered.map(item => <div className="meeting-image-tile" key={item.attachment.attachmentId}>
          <button type="button" className="meeting-image-tile-preview" aria-label={`预览 ${item.attachment.name}`} title={`${item.attachment.name} · ${item.author}`}
            onClick={() => setPreviewId(item.attachment.attachmentId)}>
            <img src={attachmentUrl(meetingId, item.attachment.attachmentId, true)} alt={item.attachment.name} loading="lazy" />
          </button>
          <Tooltip placement="topRight" title="跳转原消息">
            <Button type="text" size="small" className="meeting-image-tile-locate" aria-label={`定位 ${item.attachment.name} 所在消息`}
              icon={<AimOutlined />} onClick={() => { void onLocate(item.messageId).then(() => onClose?.()).catch(() => setError('原消息暂时无法定位，请重试。')) }} />
          </Tooltip>
        </div>)}
      </div>
    }
    if (kind === 'files' && tabKey === 'files') {
      return <div className="meeting-file-list">
        {filtered.map(item => {
          const ext = fileExtension(item.attachment.name)
          const hasTextSnippet = attachmentPreviewKind(item.attachment) === 'text' || isTextAttachment(item.attachment.name)
          return <article className="meeting-file-row" key={item.attachment.attachmentId}>
            <button type="button" className="meeting-file-main" onClick={() => openFilePreview(item.attachment)}>
              <span className={`meeting-file-badge ${fileBadgeTone(ext)}`} aria-hidden="true">{ext}</span>
              <span className="meeting-file-info">
                <span className="meeting-file-name" title={item.attachment.name}>{item.attachment.name}</span>
                {hasTextSnippet && <CompactFileSnippet url={attachmentUrl(meetingId, item.attachment.attachmentId, true)} size={item.attachment.sizeBytes} />}
                <span className="meeting-file-sub">{sizeLabel(item.attachment.sizeBytes)} · {item.author} · {formatShortTime(item.createdAt)}</span>
              </span>
            </button>
            <div className="meeting-file-actions">
              <Tooltip placement="topRight" title="下载">
                <Button type="text" size="small" aria-label={`下载 ${item.attachment.name}`} icon={<DownloadOutlined />}
                  href={attachmentUrl(meetingId, item.attachment.attachmentId)} target="_blank" rel="noopener noreferrer" />
              </Tooltip>
              <Tooltip placement="topRight" title="跳转原消息">
                <Button type="text" size="small" aria-label={`定位 ${item.attachment.name} 所在消息`} icon={<AimOutlined />}
                  onClick={() => { void onLocate(item.messageId).then(() => onClose?.()).catch(() => setError('原消息暂时无法定位，请重试。')) }} />
              </Tooltip>
            </div>
          </article>
        })}
      </div>
    }
    return <>{filtered.map(item => <article className="meeting-attachment-row" key={item.attachment.attachmentId}>
      <AttachmentLinks compact meetingId={meetingId} attachments={[item.attachment]} onOpenViewer={setPreviewId} />
      <div className="meeting-attachment-meta">
        <Typography.Text type="secondary">{item.author} · {item.createdAt.toLocaleString()}</Typography.Text>
        <Button type="link" size="small" onClick={() => { void onLocate(item.messageId).then(() => onClose?.()).catch(() => setError('原消息暂时无法定位，请重试。')) }}>跳转原消息</Button>
      </div>
    </article>)}</>
  }
  return <div className="meeting-attachment-panel">
    {error && <Alert type="error" title={error} action={<Button size="small" onClick={() => void load()}>重试</Button>} />}
    {kind ? renderList(kind, kind === 'images' ? '图片' : '文件') : <Tabs size="small" defaultActiveKey="files" destroyOnHidden items={[
      { key: 'files', label: '文件', children: renderList('files', '文件') },
      { key: 'images', label: '图片', children: renderList('images', '图片') },
    ]} />}
    {busy && <div className="meeting-attachment-loading"><Spin size="small" /></div>}
    {before !== undefined && <Button size="small" block loading={busy} onClick={() => void load(true)}>加载更早附件</Button>}
    {previewId && <AttachmentViewer key={previewId} initialId={previewId} meetingId={meetingId} scope={scope} peerParticipantId={peerParticipantId}
      beforeSequence={before} entries={items.map(item => ({ file: item.attachment, author: item.author, sequence: item.sequence }))}
      onAdded={() => onClose?.()} onClose={() => setPreviewId(undefined)} />}
    <Modal open={!!modalPreview} title={modalPreview?.name} footer={modalPreview ? <Button icon={<DownloadOutlined />} href={attachmentUrl(meetingId, modalPreview.attachmentId)} target="_blank" rel="noopener noreferrer">下载</Button> : null} destroyOnHidden onCancel={() => setModalPreview(undefined)} width="min(900px, 92vw)">
      {modalPreview && <AttachmentPreview key={modalPreview.attachmentId} file={modalPreview} url={attachmentUrl(meetingId, modalPreview.attachmentId, true)} />}
    </Modal>
  </div>
}
export function MeetingAttachmentList({ meetingId, scope, peerParticipantId, onLocate, onOpenPanel }: { meetingId: string; scope: 'public' | 'private'; peerParticipantId?: string; onLocate: (id: string) => Promise<void>; onOpenPanel?: () => void }) {
  const [open, setOpen] = useState(false)
  useEffect(() => { setOpen(false) }, [meetingId, scope, peerParticipantId])
  if (onOpenPanel) {
    return <Tooltip title="在右侧栏浏览当前会话的文件与图片"><Button type="text" className="browse-attachments" icon={<FolderOpenOutlined aria-hidden />} onClick={onOpenPanel}>附件列表</Button></Tooltip>
  }
  return <><Tooltip title="浏览当前会话的附件"><Button type="text" className="browse-attachments" icon={<FolderOpenOutlined aria-hidden />} onClick={() => setOpen(true)}>附件列表</Button></Tooltip><Drawer title={scope === 'private' ? '此私聊的附件' : '公开讨论附件'} open={open} onClose={() => setOpen(false)}>
    {open && <MeetingAttachmentPanel meetingId={meetingId} scope={scope} peerParticipantId={peerParticipantId} onLocate={onLocate} onClose={() => setOpen(false)} />}
  </Drawer></>
}
