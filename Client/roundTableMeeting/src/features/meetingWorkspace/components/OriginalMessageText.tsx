import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Modal, Spin } from 'antd'
import { copyText } from '@/shared/utils/copyText'
import { attachmentUrl } from '../api/messageResources'
import type { ChatAttachment } from '../types/realtimeChat'

type Props = { meetingId: string; source: ChatAttachment }

export function OriginalMessageText({ meetingId, source }: Props) {
  const [open, setOpen] = useState(false)
  const [text, setText] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [feedback, setFeedback] = useState('')
  const controller = useRef<AbortController | undefined>(undefined)
  useEffect(() => () => controller.current?.abort(), [])
  function close() {
    controller.current?.abort()
    setBusy(false); setOpen(false); setText(undefined); setFeedback('')
  }
  async function load(action: 'read' | 'copy') {
    controller.current?.abort()
    const request = new AbortController()
    controller.current = request
    setBusy(true); setFeedback(''); setText(undefined)
    if (action === 'read') setOpen(true)
    try {
      const response = await fetch(attachmentUrl(meetingId, source.attachmentId), {
        credentials: 'same-origin', signal: request.signal, cache: 'no-store',
      })
      if (response.status !== 200) throw new Error()
      const bytes = await response.arrayBuffer()
      if (bytes.byteLength !== source.sizeBytes) throw new Error()
      const original = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes)
      if (request.signal.aborted) return
      if (action === 'read') setText(original)
      else {
        try { await copyText(original); if (!request.signal.aborted) setFeedback('已复制全文') }
        catch {
          if (request.signal.aborted) return
          setText(original); setOpen(true); setFeedback('复制失败，请选择正文后手动复制。')
        }
      }
    } catch {
      if (!request.signal.aborted) setFeedback('无法读取完整原文，请重新读取。')
    } finally { if (!request.signal.aborted) setBusy(false) }
  }
  async function copyVisible() {
    if (text === undefined) return
    try { await copyText(text); setFeedback('已复制全文') }
    catch { setFeedback('复制失败，请选择正文后手动复制。') }
  }
  return <>
    <div className="original-message-actions">
      <Button type="link" size="small" disabled={busy} onClick={() => void load('read')}>阅读全文</Button>
      <Button type="link" size="small" disabled={busy} onClick={() => void load('copy')}>复制全文</Button>
      {!open && feedback && <span role="status">{feedback}</span>}
    </div>
    <Modal className="original-message-modal" centered open={open} title="消息全文" onCancel={close} destroyOnHidden width="min(960px, 96vw)"
      footer={<Button disabled={text === undefined} onClick={() => void copyVisible()}>复制全文</Button>}>
      {busy && <Spin aria-label="正在读取全文" />}
      {text !== undefined && <pre className="original-message-text" aria-label="消息原文" tabIndex={0}>{text}</pre>}
      {feedback && (text === undefined && !busy
        ? <Alert type="error" title={feedback} action={<Button onClick={() => void load('read')}>重试</Button>} />
        : <span role="status">{feedback}</span>)}
    </Modal>
  </>
}
