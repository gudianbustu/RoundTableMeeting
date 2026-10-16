import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Space, Spin } from 'antd'
import { getDocument, GlobalWorkerOptions, type PDFDocumentProxy } from 'pdfjs-dist'
import workerUrl from 'pdfjs-dist/build/pdf.worker.min.mjs?url'

GlobalWorkerOptions.workerSrc = workerUrl

export default function PdfAttachmentPreview({ url, name }: { url: string; name: string }) {
  const container = useRef<HTMLDivElement>(null)
  const [document, setDocument] = useState<PDFDocumentProxy>()
  const [pageNumber, setPageNumber] = useState(1)
  const [attempt, setAttempt] = useState(0)
  const [busy, setBusy] = useState(true)
  const [error, setError] = useState(false)

  useEffect(() => {
    let disposed = false
    setDocument(undefined); setPageNumber(1); setBusy(true); setError(false)
    const task = getDocument({ url, withCredentials: true, cMapUrl: '/pdfjs/cmaps/', cMapPacked: true, standardFontDataUrl: '/pdfjs/standard_fonts/', wasmUrl: '/pdfjs/wasm/' })
    void task.promise.then(value => { if (!disposed) setDocument(value) }).catch(() => {
      if (!disposed) { setError(true); setBusy(false) }
    })
    return () => { disposed = true; void task.destroy().catch(() => {}) }
  }, [url, attempt])

  useEffect(() => {
    if (!document || !container.current) return
    let disposed = false
    let cancel: (() => void) | undefined
    const host = container.current
    const canvas = window.document.createElement('canvas')
    canvas.setAttribute('aria-label', `${name}，第 ${pageNumber} 页`)
    canvas.setAttribute('role', 'img')
    canvas.style.maxWidth = '100%'
    canvas.style.height = 'auto'
    host.replaceChildren(canvas)
    setBusy(true); setError(false)
    void document.getPage(pageNumber).then(async page => {
      if (disposed) return
      const base = page.getViewport({ scale: 1 })
      const scale = Math.min(2, 4096 / base.width, 4096 / base.height, (host.clientWidth || 800) / base.width)
      const viewport = page.getViewport({ scale })
      canvas.width = Math.ceil(viewport.width); canvas.height = Math.ceil(viewport.height)
      const task = page.render({ canvas, viewport })
      cancel = () => task.cancel()
      await task.promise
      if (!disposed) setBusy(false)
    }).catch(() => { if (!disposed) { setError(true); setBusy(false) } })
    return () => { disposed = true; cancel?.(); canvas.remove() }
  }, [document, pageNumber, name])

  return <div>
    {document && <Space style={{ marginBottom: 12 }}>
      <Button disabled={pageNumber <= 1} onClick={() => setPageNumber(value => value - 1)}>上一页</Button>
      <span>第 {pageNumber} / {document.numPages} 页</span>
      <Button disabled={pageNumber >= document.numPages} onClick={() => setPageNumber(value => value + 1)}>下一页</Button>
    </Space>}
    {error && <Alert type="error" title="PDF 预览失败，请重试。" action={<Button onClick={() => setAttempt(value => value + 1)}>重试</Button>} />}
    {busy && <div role="status" aria-label="正在加载 PDF"><Spin /></div>}
    <div ref={container} style={{ maxHeight: '70vh', overflow: 'auto' }} />
  </div>
}
