import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Spin } from 'antd'
import './AttachmentViewer.css'

const fit = { scale: 1, x: 0, y: 0 }

export default function ImageAttachmentPreview({ url, name, maxScale = 8 }: { url: string; name: string; maxScale?: number }) {
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<'loading' | 'loaded' | 'failed'>('loading')
  const [view, setView] = useState(fit)
  const stage = useRef<HTMLDivElement>(null)
  const image = useRef<HTMLImageElement>(null)
  const drag = useRef<{ id: number; x: number; y: number } | null>(null)

  function contained(scale: number, x: number, y: number) {
    const limitX = Math.max(0, ((image.current?.clientWidth ?? 0) * scale - (stage.current?.clientWidth ?? 0)) / 2)
    const limitY = Math.max(0, ((image.current?.clientHeight ?? 0) * scale - (stage.current?.clientHeight ?? 0)) / 2)
    return { scale, x: Math.min(limitX, Math.max(-limitX, x)), y: Math.min(limitY, Math.max(-limitY, y)) }
  }

  function zoom(factor: number, cx = 0, cy = 0) {
    setView(previous => {
      const scale = Math.min(maxScale, Math.max(1, previous.scale * factor))
      const ratio = scale / previous.scale
      return contained(scale, cx + (previous.x - cx) * ratio, cy + (previous.y - cy) * ratio)
    })
  }

  useEffect(() => {
    const element = stage.current
    if (!element || state !== 'loaded') return
    const wheel = (event: WheelEvent) => {
      event.preventDefault()
      if (!event.deltaY) return
      const rect = element.getBoundingClientRect()
      zoom(event.deltaY < 0 ? 1.2 : 1 / 1.2, event.clientX - rect.left - rect.width / 2, event.clientY - rect.top - rect.height / 2)
    }
    element.addEventListener('wheel', wheel, { passive: false })
    const resize = new ResizeObserver(() => setView(v => contained(v.scale, v.x, v.y)))
    resize.observe(element)
    return () => { element.removeEventListener('wheel', wheel); resize.disconnect() }
  }, [state, maxScale])

  return <div className="image-viewer">
    <div ref={stage} className="image-viewer-stage" data-zoomed={view.scale > 1}
      onPointerDown={event => {
        if (event.button !== 0 || view.scale <= 1 || drag.current) return
        drag.current = { id: event.pointerId, x: event.clientX, y: event.clientY }
        event.currentTarget.setPointerCapture(event.pointerId)
      }}
      onPointerMove={event => {
        if (!drag.current || drag.current.id !== event.pointerId) return
        const dx = event.clientX - drag.current.x, dy = event.clientY - drag.current.y
        drag.current.x = event.clientX; drag.current.y = event.clientY
        setView(v => contained(v.scale, v.x + dx, v.y + dy))
      }}
      onLostPointerCapture={() => { drag.current = null }}>
      {state === 'loading' && <Spin />}
      {state === 'failed' && <Alert type="error" title="图片预览失败，文件可能已损坏或暂时无法访问。" action={
        <Button onClick={() => { setView(fit); setState('loading'); setAttempt(value => value + 1) }}>重试</Button>} />}
      <img key={attempt} ref={image} alt={name} draggable={false} hidden={state === 'failed'}
        src={url.startsWith('blob:') ? url : `${url}${url.includes('?') ? '&' : '?'}previewAttempt=${attempt}`}
        style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.scale})` }}
        onLoad={() => setState('loaded')} onError={() => setState('failed')} />
    </div>
    {state === 'loaded' && <div className="image-viewer-tools">
      <button type="button" aria-label="缩小图片" disabled={view.scale <= 1} onClick={() => zoom(.8)}>−</button>
      <button type="button" aria-label="恢复适应画面" onClick={() => setView(fit)}>{Math.round(view.scale * 100)}%</button>
      <button type="button" aria-label="放大图片" disabled={view.scale >= maxScale} onClick={() => zoom(1.25)}>＋</button>
    </div>}
  </div>
}
