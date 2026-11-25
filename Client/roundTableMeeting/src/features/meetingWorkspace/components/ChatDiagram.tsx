import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import ImageAttachmentPreview from './ImageAttachmentPreview'
import './MermaidDiagram.css'

const Renderer = lazy(() => import('./MermaidDiagram'))

export function ChatDiagram({ source }: { source: string }) {
  const host = useRef<HTMLDivElement>(null)
  const dialog = useRef<HTMLDialogElement>(null)
  const [visible, setVisible] = useState(false)
  const [opened, setOpened] = useState(false)
  const [requested, setRequested] = useState(false)
  const [svgUrl, setSvgUrl] = useState('')
  const large = source.length > 4000 || source.split(/[;\n]/, 61).length > 60 || (source.match(/-->|==>|-\.->|--?>>?/g)?.length ?? 0) > 60
  useEffect(() => {
    const target = host.current
    if (!target || !window.IntersectionObserver) return
    const observer = new IntersectionObserver(([entry]) => setVisible(entry.isIntersecting), {
      root: target.closest('[role="log"]'),
    })
    observer.observe(target)
    return () => observer.disconnect()
  }, [])
  useEffect(() => {
    if (!svgUrl || !requested) return
    dialog.current?.showModal()
    setOpened(true)
    setRequested(false)
  }, [svgUrl, requested])
  const active = opened || requested || visible && !large
  return <div ref={host} className="chat-diagram" data-large={large}>
    <button type="button" className="chat-diagram-open" onClick={() => setRequested(true)}>
      {svgUrl && active ? <img src={svgUrl} alt="全屏查看 Mermaid 图表" /> : '查看 Mermaid 图表'}
    </button>
    {active && <Suspense fallback={null}><Renderer source={source} onImage={setSvgUrl} /></Suspense>}
    <dialog ref={dialog} aria-label="Mermaid 图表全屏查看" className="chat-diagram-dialog" onClose={() => setOpened(false)}>
      <button type="button" className="mermaid-expand" onClick={() => dialog.current?.close()}>退出全屏</button>
      {opened && svgUrl && <ImageAttachmentPreview name="Mermaid 图表" url={svgUrl} maxScale={32} />}
    </dialog>
    <details><summary>查看图表源码</summary><pre>{source}</pre></details>
  </div>
}
