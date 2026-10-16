import { useEffect, useRef, useState } from 'react'
import { getDocument, GlobalWorkerOptions } from 'pdfjs-dist'
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url'

GlobalWorkerOptions.workerSrc = workerUrl
export default function PdfAttachmentThumbnail({ url, name }: { url: string; name: string }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [pages, setPages] = useState<number>()
  const [state, setState] = useState('loading')
  useEffect(() => {
    let disposed = false; let released = false; let cancel: (() => void) | undefined
    const task = getDocument({ url, withCredentials: true, cMapUrl: '/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/pdfjs/standard_fonts/', wasmUrl: '/pdfjs/wasm/' })
    const release = () => { if (!released) { released = true; void task.destroy().catch(() => {}) } }
    void task.promise.then(async document => {
      if (disposed || !canvas.current) return
      setPages(document.numPages)
      const page = await document.getPage(1)
      if (disposed || !canvas.current) return
      const base = page.getViewport({ scale: 1 }); const viewport = page.getViewport({ scale: Math.min(560 / base.width, 4096 / base.height) })
      canvas.current.width = Math.ceil(viewport.width); canvas.current.height = Math.ceil(viewport.height)
      const render = page.render({ canvas: canvas.current, viewport }); cancel = () => render.cancel()
      await render.promise
      if (!disposed) setState('ready')
    }).catch(() => { if (!disposed) setState('failed') }).finally(release)
    return () => { disposed = true; cancel?.(); release() }
  }, [url])
  return <>{state !== 'ready' && <span className="attachment-thumbnail-status">{state === 'failed' ? '缩略预览暂不可用' : '正在加载预览…'}</span>}<canvas ref={canvas} role="img" aria-label={`${name} 首页缩略图`} style={{ visibility: state === 'ready' ? 'visible' : 'hidden' }} />{pages && <span className="attachment-page-count">PDF · {pages} 页</span>}</>
}
