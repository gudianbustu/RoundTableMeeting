import { lazy, Suspense, useEffect, useMemo, useState } from 'react'
import { MessageBody } from './MessageBody'
import './AttachmentViewer.css'
import { Alert, Button, Select, Spin } from 'antd'

const MermaidDiagram = lazy(() => import('./MermaidDiagram'))

export const textPreviewLimit = 1024 * 1024
export const isTextAttachment = (name: string) => /\.(txt|md|mmd|log|csv|tsv|json|xml|yaml|yml|ini|conf)$/i.test(name)

export function decodePreview(bytes: Uint8Array, encoding = 'auto', truncated = false) {
  let selected = encoding
  if (selected === 'auto') {
    if (bytes[0] === 0xff && bytes[1] === 0xfe) selected = 'utf-16le'
    else if (bytes[0] === 0xfe && bytes[1] === 0xff) selected = 'utf-16be'
    else {
      try { new TextDecoder('utf-8', { fatal: true }).decode(bytes, { stream: truncated }); selected = 'utf-8' }
      catch { selected = 'gb18030' }
    }
  }
  const text = new TextDecoder(selected, { fatal: true }).decode(bytes, { stream: truncated })
  if (/[\x00-\x08\x0b\x0c\x0e-\x1f]/.test(text)) throw new Error('binary_content')
  return { text, encoding: selected }
}

export default function TextAttachmentPreview({ url, sizeBytes, markdown = false, diagram = false }: { url: string; sizeBytes: number; markdown?: boolean; diagram?: boolean }) {
  const [bytes, setBytes] = useState<Uint8Array>()
  const [encoding, setEncoding] = useState('auto')
  const [attempt, setAttempt] = useState(0)
  const [error, setError] = useState(false)
  const [truncated, setTruncated] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    setBytes(undefined); setError(false); setTruncated(false)
    void (async () => {
      const response = await fetch(url, { credentials: 'same-origin', signal: controller.signal, headers: sizeBytes === 0 ? undefined : { Range: `bytes=0-${textPreviewLimit}` } })
      if (!response.ok || response.status === 204 || !response.body) throw new Error('preview_failed')
      const reader = response.body.getReader()
      const chunks: Uint8Array[] = []; let length = 0
      try {
        while (length <= textPreviewLimit) {
          const { done, value } = await reader.read()
          if (done) break
          const part = value.subarray(0, textPreviewLimit + 1 - length)
          chunks.push(part); length += part.length
        }
      } finally { await reader.cancel() }
      if (controller.signal.aborted) return
      const result = new Uint8Array(Math.min(length, textPreviewLimit)); let offset = 0
      for (const chunk of chunks) { const part = chunk.subarray(0, result.length - offset); result.set(part, offset); offset += part.length }
      setTruncated(sizeBytes > textPreviewLimit || length > textPreviewLimit); setBytes(result)
    })().catch(() => { if (!controller.signal.aborted) setError(true) })
    return () => controller.abort()
  }, [url, sizeBytes, attempt])
  const decoded = useMemo(() => {
    if (!bytes) return undefined
    try { return decodePreview(bytes, encoding, truncated) }
    catch { return { error: true } }
  }, [bytes, encoding, truncated])
  const selector = <Select aria-label="文本编码" value={encoding} onChange={setEncoding} style={{ width: 190, marginBottom: 12 }} options={[
      { value: 'auto', label: '自动识别编码' }, { value: 'utf-8', label: 'UTF-8' }, { value: 'gb18030', label: 'GB18030 / GBK' }, { value: 'utf-16le', label: 'UTF-16 LE' }, { value: 'utf-16be', label: 'UTF-16 BE' },
    ]} />
  return <div className={markdown ? 'attachment-reader' : undefined}>
    {markdown ? <details><summary>文本编码</summary>{selector}</details> : selector}
    {error && <Alert type="error" title="文本预览加载失败。" action={<Button onClick={() => setAttempt(value => value + 1)}>重试</Button>} />}
    {!bytes && !error && <Spin />}
    {decoded && 'error' in decoded && <Alert type="warning" title="无法按所选编码预览，文件可能不是纯文本；请尝试其他编码。" />}
    {!diagram && truncated && <Alert type="info" title="文件较大，仅预览前 1 MiB，后续内容未显示。" />}
    {diagram && truncated && <Alert type="warning" title="图表文件超过 1 MiB 读取上限，请下载完整文件查看。" />}
    {diagram && !truncated && decoded && 'text' in decoded && <Suspense fallback={<Spin />}><MermaidDiagram source={decoded.text} /></Suspense>}
    {!diagram && decoded && 'text' in decoded && (markdown ? <article aria-label="Markdown 正文"><MessageBody content={decoded.text || '（空文件）'} mentions={[]} /></article> : <pre aria-label="文本内容" style={{ maxHeight: '65vh', overflow: 'auto', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{decoded.text || '（空文件）'}</pre>)}
  </div>
}
