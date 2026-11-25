import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Spin } from 'antd'
import mermaidUrl from 'mermaid/dist/mermaid.min.js?url'
import ImageAttachmentPreview from './ImageAttachmentPreview'
import './MermaidDiagram.css'
import frameTemplate from './MermaidDiagramFrame.html?raw'

const escapeText = (value: string) => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')

const thumbnails = new Map<string, Blob>()
let cachedBytes = 0
function remember(source: string, blob: Blob) {
  if (thumbnails.has(source) || blob.size > 4_000_000) return
  thumbnails.set(source, blob)
  cachedBytes += blob.size
  while (thumbnails.size > 48 || cachedBytes > 8_000_000) {
    const oldest = thumbnails.keys().next().value!
    cachedBytes -= thumbnails.get(oldest)!.size
    thumbnails.delete(oldest)
  }
}

export default function MermaidDiagram({ source, onImage }: { source: string; onImage?: (url: string) => void }) {
  const [cached] = useState(() => onImage ? thumbnails.get(source) : undefined)
  const markup = useMemo(() => {
    if (source.length > 50_000) return ''
    const nonce = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')
    return frameTemplate.replaceAll('__NONCE__', nonce)
      .replace('__RENDERER__', () => new URL(mermaidUrl, window.location.href).href)
      .replace('__SOURCE__', () => escapeText(source))
  }, [source])
  const frame = useRef<HTMLIFrameElement>(null)
  const reader = useRef<HTMLElement>(null)
  const toggle = useRef<HTMLButtonElement>(null)
  const [url, setUrl] = useState('')
  const [error, setError] = useState('')
  const [expanded, setExpanded] = useState(false)

  useEffect(() => {
    let imageUrl = ''
    const display = (blob: Blob) => {
      imageUrl = URL.createObjectURL(blob)
      setUrl(imageUrl)
      onImage?.(imageUrl)
    }
    if (cached) display(cached)
    const receive = (event: MessageEvent) => {
      if (!frame.current || event.source !== frame.current.contentWindow || event.origin !== 'null') return
      const result = event.data
      if (result?.type === 'diagram-error' && typeof result.message === 'string') setError(result.message)
      if (result?.type !== 'diagram-image' || typeof result.svg !== 'string' || imageUrl) return
      // SVG is only displayed as an image. Its markup never enters the application DOM.
      const blob = new Blob([result.svg], { type: 'image/svg+xml' })
      if (onImage) remember(source, blob)
      display(blob)
    }
    window.addEventListener('message', receive)
    return () => {
      window.removeEventListener('message', receive)
      if (imageUrl) URL.revokeObjectURL(imageUrl)
      onImage?.('')
    }
  }, [source, onImage, cached])

  useEffect(() => {
    if (!expanded) return
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === 'Tab') {
        const buttons = reader.current?.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')
        if (!buttons?.length) return
        const first = buttons[0], last = buttons[buttons.length - 1]
        if (event.shiftKey && document.activeElement === first || !event.shiftKey && document.activeElement === last) {
          event.preventDefault()
          const next = event.shiftKey ? last : first
          next.focus()
        }
        return
      }
      if (event.key !== 'Escape') return
      event.preventDefault()
      event.stopImmediatePropagation()
      setExpanded(false)
      toggle.current?.focus()
    }
    window.addEventListener('keydown', handleKey, true)
    return () => window.removeEventListener('keydown', handleKey, true)
  }, [expanded])

  if (!markup) return <Alert type="warning" title={onImage ? "图表超过 50,000 字符，请查看源码。" : "图表超过 50,000 字符，请下载文件查看。"} />
  return <section ref={reader} aria-label="Mermaid 图表阅读器" className="mermaid-reader" data-expanded={expanded}>
    {!url && !error && !cached && <iframe ref={frame} className="mermaid-render-frame" title="Mermaid 渲染器" sandbox="allow-scripts"
      tabIndex={-1} aria-hidden referrerPolicy="no-referrer" srcDoc={markup} />}
    {error ? <Alert type="error" title="Mermaid 图表预览失败" description={error} />
      : url ? onImage ? null : <>
        <button type="button" ref={toggle} className="mermaid-expand" onClick={() => setExpanded(!expanded)}>{expanded ? '退出全屏' : '全屏查看'}</button>
        <ImageAttachmentPreview maxScale={32} name="Mermaid 图表" url={url} />
      </> : <Spin aria-label="正在生成图表" />}
  </section>
}
