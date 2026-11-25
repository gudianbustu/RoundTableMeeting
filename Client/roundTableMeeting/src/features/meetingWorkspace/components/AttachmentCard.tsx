import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { Button } from 'antd'
import { DownloadOutlined, EyeOutlined, FileOutlined } from '@ant-design/icons'
import { attachmentUrl } from '../api/messageResources'
import type { ChatAttachment } from '../types/realtimeChat'
import { decodePreview, isTextAttachment } from './TextAttachmentPreview'

const PdfThumbnail = lazy(() => import('./PdfAttachmentThumbnail'))
export function attachmentPreviewKind(file: ChatAttachment) {
  if (['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(file.mediaType)) return 'image'
  if (file.mediaType === 'application/pdf') return 'pdf'
  if (file.mediaType === 'application/octet-stream') {
    const extension = file.name.split('.').pop()?.toLowerCase() ?? ''
    if (['mp3', 'wav', 'ogg', 'oga', 'm4a', 'aac', 'flac'].includes(extension)) return 'audio'
    if (['mp4', 'm4v', 'webm', 'ogv', 'mov'].includes(extension)) return 'video'
  }
  if (['audio/mpeg', 'audio/wav', 'audio/ogg', 'audio/mp4', 'audio/aac', 'audio/flac'].includes(file.mediaType)) return 'audio'
  if (['video/mp4', 'video/webm', 'video/ogg', 'video/quicktime'].includes(file.mediaType)) return 'video'
  if (file.mediaType === 'application/octet-stream' && isTextAttachment(file.name)) return 'text'
  return undefined
}

function ImageThumbnail({ url, name }: { url: string; name: string }) {
  const [state, setState] = useState('loading')
  return <>
    {state !== 'ready' && <span className="attachment-thumbnail-status">{state === 'failed' ? '图片加载失败，点击展开后重试' : '正在加载图片…'}</span>}
    <img src={url} alt={name} decoding="async" onLoad={() => setState('ready')} onError={() => setState('failed')} style={{ visibility: state === 'ready' ? 'visible' : 'hidden' }} />
  </>
}
function TextThumbnail({ url, size }: { url: string; size: number }) {
  const [text, setText] = useState<string>()
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    void (async () => {
      const limit = 8192
      const response = await fetch(url, { signal: controller.signal, credentials: 'same-origin', headers: size === 0 ? undefined : { Range: `bytes=0-${limit - 1}` } })
      if (!response.ok || response.status === 204 || !response.body) throw new Error('text_unavailable')
      const reader = response.body.getReader(); const bytes = new Uint8Array(limit); let length = 0
      try {
        while (length < limit) {
          const { done, value } = await reader.read(); if (done) break
          const part = value.subarray(0, limit - length); bytes.set(part, length); length += part.length
        }
      } finally { await reader.cancel() }
      if (!controller.signal.aborted) setText(decodePreview(bytes.subarray(0, length), 'auto', size > limit).text.split(/\r?\n/).slice(0, 6).join('\n'))
    })().catch(() => { if (!controller.signal.aborted) setFailed(true) })
    return () => controller.abort()
  }, [url, size])
  return text !== undefined ? <pre>{text || '（空文件）'}</pre> : <span className="attachment-thumbnail-status">{failed ? '缩略预览暂不可用' : '正在加载预览…'}</span>
}

export default function AttachmentCard({ meetingId, file, onPreview, compact = false }: { compact?: boolean; meetingId: string; file: ChatAttachment; onPreview: () => void }) {
  const host = useRef<HTMLDivElement>(null)
  const [visible, setVisible] = useState(false)
  const kind = attachmentPreviewKind(file)
  const url = attachmentUrl(meetingId, file.attachmentId, true)
  useEffect(() => {
    if (!kind || !host.current) return
    if (!('IntersectionObserver' in window)) { setVisible(true); return }
    const observer = new IntersectionObserver(([entry]) => {
      setVisible(entry.isIntersecting)
      if (kind === 'image' && entry.isIntersecting) observer.disconnect()
    }, { rootMargin: '100px' })
    observer.observe(host.current)
    return () => observer.disconnect()
  }, [kind])
  const thumbnail = visible && (kind === 'pdf' || kind === 'text') ? kind === 'pdf' ? <Suspense fallback={<span>正在加载预览…</span>}><PdfThumbnail url={url} name={file.name} /></Suspense> : <TextThumbnail url={url} size={file.sizeBytes} /> : <div className="attachment-file-symbol"><FileOutlined /><span>{kind === 'audio' ? '音频预览' : kind === 'video' ? '视频预览' : kind ? '文件预览' : file.name.split('.').pop()?.toUpperCase() || '文件'}</span></div>
  if (kind === 'image') return <div ref={host} className="message-image">
    <button type="button" className="message-image-button" aria-label={`预览 ${file.name}`} onClick={onPreview}>
      {visible ? <ImageThumbnail key={url} url={url} name={file.name} /> : <span className="attachment-thumbnail-status">图片</span>}
      {compact && <span className="chat-file-name">{file.name}</span>}
    </button>
  </div>
  return <div ref={host} className="attachment-card" aria-label={`附件 ${file.name}`}>
    {kind ? <button className="attachment-thumbnail" type="button" aria-label={`预览 ${file.name}`} onClick={onPreview}>{thumbnail}</button> : <div className="attachment-thumbnail">{thumbnail}</div>}
    <div className="attachment-card-info"><div className="attachment-card-name" title={file.name}>{file.name}</div><span className="attachment-card-size">{file.sizeBytes >= 1_000_000 ? `${(file.sizeBytes / 1_000_000).toFixed(1)} MB` : `${file.sizeBytes} 字节`}</span></div>
    <div className="attachment-card-actions"><Button type="text" size="small" icon={<DownloadOutlined />} href={attachmentUrl(meetingId, file.attachmentId)} target="_blank" rel="noopener noreferrer">下载</Button>{kind && <Button type="text" size="small" icon={<EyeOutlined />} onClick={onPreview}>预览</Button>}</div>
  </div>
}
