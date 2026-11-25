import { useEffect, useRef, useState } from 'react'
import { Modal } from 'antd'
import { attachmentUrl, listAttachments } from '../api/messageResources'
import type { ChatAttachment, ChatMessage } from '../types/realtimeChat'
import AttachmentPreview from './AttachmentPreview'
import { attachmentPreviewKind } from './AttachmentCard'

function sameConversation(a: ChatMessage, b: ChatMessage) {
  if (a.meetingId !== b.meetingId || (a.visibility ?? 'public') !== (b.visibility ?? 'public')) return false
  if (a.visibility !== 'private') return true
  if (!a.author.participantId || !a.recipient || !b.author.participantId || !b.recipient) return a.messageId === b.messageId
  return (a.author.participantId === b.author.participantId && a.recipient?.participantId === b.recipient?.participantId)
    || (a.author.participantId === b.recipient?.participantId && a.recipient?.participantId === b.author.participantId)
}

type ViewerEntry = { file: ChatAttachment; author: string; sequence: number }
type ViewerProps = {
  entries: ViewerEntry[]; initialId: string; meetingId: string; scope: 'public' | 'private'
  peerParticipantId?: string; beforeSequence?: number; onClose: () => void
}

export function ConversationAttachmentViewer({ messages, selectedId, onSelect, currentParticipantId, hasEarlierMessages }: {
  messages: ChatMessage[]; selectedId: string; onSelect: (id?: string) => void
  currentParticipantId?: string; hasEarlierMessages?: boolean
}) {
  const source = messages.find(message => message.attachments?.some(file => file.attachmentId === selectedId))
  useEffect(() => { if (!source) onSelect(undefined) }, [source, onSelect])
  if (!source) return null
  const scope = source.visibility ?? 'public'
  const peer = source.author.participantId === currentParticipantId ? source.recipient?.participantId : source.author.participantId
  const visible = messages.filter(message => sameConversation(source, message))
  const entries = visible.flatMap(message => (message.attachments ?? []).map(file => ({ file, author: message.author.displayName, sequence: message.sequence })))
  return <AttachmentViewer key={selectedId} initialId={selectedId} entries={entries} meetingId={source.meetingId}
    scope={scope} peerParticipantId={scope === 'private' ? peer : undefined}
    beforeSequence={hasEarlierMessages && (scope === 'public' || currentParticipantId && peer) ? Math.min(...visible.map(item => item.sequence)) : undefined}
    onClose={() => onSelect(undefined)} />
}

export function AttachmentViewer({ initialId, entries, scope, meetingId, peerParticipantId, beforeSequence, onClose }: ViewerProps) {
  const [open, setOpen] = useState(true)
  const [selectedId, setSelectedId] = useState(initialId)
  const [older, setOlder] = useState<ViewerEntry[]>([])
  const [before, setBefore] = useState(beforeSequence)
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const request = useRef<AbortController | undefined>(undefined)
  const combined = [...new Map([...older, ...entries].map(item => [item.file.attachmentId, item])).values()].sort((a, b) => a.sequence - b.sequence)
  const current = combined.find(entry => entry.file.attachmentId === selectedId)
  const images = combined.filter(({ file }) => attachmentPreviewKind(file) === 'image')
  const index = images.findIndex(({ file }) => file.attachmentId === selectedId)
  const strip = useRef<HTMLDivElement>(null)
  useEffect(() => () => request.current?.abort(), [])
  useEffect(() => {
    const item = strip.current?.querySelector<HTMLButtonElement>('[aria-current="true"]')
    if (item && strip.current) strip.current.scrollLeft = item.offsetLeft - (strip.current.clientWidth - item.offsetWidth) / 2
  }, [selectedId])
  if (!current) return null
  const { file, author } = current
  const gallery = index >= 0
  const loadPrevious = async () => {
    if (before === undefined || request.current || (scope === 'private' && !peerParticipantId)) return
    const controller = new AbortController()
    request.current = controller
    setLoading(true); setFailed(false)
    try {
      let cursor: number | undefined = before
      while (cursor !== undefined) {
        const page = await listAttachments(meetingId, { scope, peerParticipantId, beforeSequence: cursor, limit: 50 }, controller.signal)
        if (controller.signal.aborted) return
        const next = page.nextBeforeSequence
        if (next !== undefined && next >= cursor) throw new Error('invalid_attachment_cursor')
        const found = page.items.filter(item => attachmentPreviewKind(item.attachment) === 'image')
          .map(item => ({ file: item.attachment, author: item.author, sequence: item.sequence })).sort((a, b) => a.sequence - b.sequence)
        setBefore(next)
        if (found.length) {
          setOlder(previous => [...found, ...previous])
          setSelectedId(found[found.length - 1].file.attachmentId)
          break
        }
        cursor = next
      }
    } catch { if (!controller.signal.aborted) setFailed(true) }
    finally { if (!controller.signal.aborted) setLoading(false); request.current = undefined }
  }
  const move = (offset: number) => {
    if (loading) return
    const next = images[index + offset]
    if (next) setSelectedId(next.file.attachmentId)
    else if (offset < 0) void loadPrevious()
  }
  const source = author + ' · ' + (scope === 'private' ? '私聊' : '公屏')
  const url = (item: ChatAttachment) => attachmentUrl(meetingId, item.attachmentId, true)
  // Keep the dialog mounted through its exit so Ant Design can restore focus to the opener.
  const close = () => setOpen(false)
  return <Modal open={open} centered={!gallery} closable={false} footer={null} onCancel={close} afterClose={onClose}
    className={gallery ? 'conversation-gallery' : 'conversation-reader'} width={gallery ? '100%' : 'min(1080px, calc(100% - 18px))'}
    modalRender={modal => <div onKeyDown={event => {
      if (!gallery) return
      if (event.key === 'ArrowLeft') { event.preventDefault(); move(-1) }
      if (event.key === 'ArrowRight') { event.preventDefault(); move(1) }
    }}>{modal}</div>}
    title={<div className="attachment-viewer-heading"><div><strong>{file.name}</strong><small>{source}</small></div>
      <a href={attachmentUrl(meetingId, file.attachmentId)} target="_blank" rel="noopener noreferrer">下载</a>
      <button type="button" aria-label="关闭预览" onClick={close}>×</button></div>}>
    <div className="attachment-viewer-center">
      <AttachmentPreview key={selectedId} file={file} url={url(file)} />
      {gallery && <>
        <button className="gallery-step gallery-prev" aria-label="上一张图片" disabled={loading || (index === 0 && before === undefined)} onClick={() => move(-1)}>‹</button>
        <button className="gallery-step gallery-next" aria-label="下一张图片" disabled={loading || index === images.length - 1} onClick={() => move(1)}>›</button>
        <div className="gallery-position" aria-live="polite">{failed ? <button onClick={() => void loadPrevious()}>加载失败，重试</button> : loading ? '正在加载更早图片…' : <>{before !== undefined && '已加载 '}{index + 1} / {images.length}</>}</div>
        <div ref={strip} className="gallery-strip">{images.map(({ file: item }) => <button key={item.attachmentId}
          aria-label={`查看 ${item.name}`} aria-current={item.attachmentId === selectedId} disabled={loading} onClick={() => setSelectedId(item.attachmentId)}>
          <img src={url(item)} loading="lazy" alt="" />
        </button>)}</div>
      </>}
    </div>
  </Modal>
}
