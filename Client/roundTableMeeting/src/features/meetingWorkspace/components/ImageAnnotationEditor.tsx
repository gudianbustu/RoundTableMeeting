import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from 'react'
import { Modal } from 'antd'
import { EditOutlined, UndoOutlined } from '@ant-design/icons'
import './ImageAnnotationEditor.css'

const BoxIcon = () => <span aria-hidden="true">▭</span>
const NumberIcon = () => <span aria-hidden="true">①</span>
const TextIcon = () => <span aria-hidden="true">T</span>
const ArrowIcon = () => <span aria-hidden="true">↗</span>

const tools = [
  { id: 'box', label: '框选', Icon: BoxIcon },
  { id: 'pen', label: '画笔', Icon: EditOutlined },
  { id: 'text', label: '文字', Icon: TextIcon },
  { id: 'number', label: '序号', Icon: NumberIcon },
  { id: 'arrow', label: '箭头', Icon: ArrowIcon },
] as const
type Tool = typeof tools[number]['id']
type Point = { x: number; y: number }
type Mark = { tool: Tool; points: Point[]; unit: number; label?: string }

function paint(ctx: CanvasRenderingContext2D, { tool, points, unit, label }: Mark) {
  const a = points[0], b = points[points.length - 1]
  const red = '#e94436'
  ctx.save()
  ctx.lineWidth = 3 * unit
  ctx.lineJoin = 'round'
  ctx.lineCap = 'round'
  ctx.fillStyle = ctx.strokeStyle = red
  ctx.beginPath()
  switch (tool) {
    case 'box':
      ctx.strokeRect(a.x, a.y, b.x - a.x, b.y - a.y)
      break
    case 'pen':
      ctx.moveTo(a.x, a.y)
      for (const p of points) ctx.lineTo(p.x, p.y)
      ctx.stroke()
      if (points.length === 1) { ctx.arc(a.x, a.y, ctx.lineWidth / 2, 0, Math.PI * 2); ctx.fill() }
      break
    case 'arrow': {
      const angle = Math.atan2(b.y - a.y, b.x - a.x)
      const head = Math.min(unit * 15, Math.hypot(b.x - a.x, b.y - a.y) * .6)
      ctx.moveTo(a.x, a.y); ctx.lineTo(b.x, b.y)
      for (const delta of [-.5, .5]) {
        ctx.moveTo(b.x, b.y)
        ctx.lineTo(b.x - head * Math.cos(angle + delta), b.y - head * Math.sin(angle + delta))
      }
      ctx.stroke()
      break
    }
    case 'number':
      ctx.arc(a.x, a.y, 12 * unit, 0, 2 * Math.PI); ctx.fill()
      ctx.fillStyle = 'white'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle'
      ctx.font = `bold ${15 * unit}px sans-serif`
      ctx.fillText(label ?? '', a.x, a.y)
      break
    case 'text':
      ctx.textBaseline = 'top'; ctx.font = `bold ${18 * unit}px sans-serif`
      ctx.fillText(label ?? '', a.x, a.y)
      break
  }
  ctx.restore()
}

export function ImageAnnotationEditor({ url, name, onAdd, onCancel }: {
  url: string; name: string; onAdd: (file: File) => boolean; onCancel: () => void
}) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const pointer = useRef<number | undefined>(undefined)
  const contacts = useRef(new Map<number, Point>())
  const [view, setView] = useState({ x: 0, y: 0, scale: 1 })
  const gesture = useRef<{ center: Point; span: number; view: typeof view } | undefined>(undefined)
  const alive = useRef(true)
  const [image, setImage] = useState<HTMLImageElement>()
  const [tool, setTool] = useState<Tool>('box')
  const [marks, setMarks] = useState<Mark[]>([])
  const [stroke, setStroke] = useState<Mark>()
  const [textMark, setTextMark] = useState<Mark>()
  const [error, setError] = useState('')
  const [saving, setSaving] = useState(false)
  useEffect(() => {
    alive.current = true
    const source = new Image()
    source.onload = () => setImage(source)
    source.onerror = () => setError('原图加载失败，请取消后重试。')
    source.src = url
    return () => { alive.current = false; source.onload = null; source.onerror = null }
  }, [url])
  useLayoutEffect(() => {
    const ctx = canvas.current?.getContext('2d')
    if (!ctx || !image) return
    ctx.clearRect(0, 0, image.naturalWidth, image.naturalHeight)
    ctx.drawImage(image, 0, 0)
    for (const mark of marks) paint(ctx, mark)
    if (stroke && stroke.tool !== 'text' && stroke.tool !== 'number') paint(ctx, stroke)
  }, [image, marks, stroke])

  function point(event: PointerEvent<HTMLDivElement>): Point {
    const element = canvas.current!, rect = element.getBoundingClientRect()
    return {
      x: Math.min(element.width, Math.max(0, (event.clientX - rect.left) * element.width / rect.width)),
      y: Math.min(element.height, Math.max(0, (event.clientY - rect.top) * element.height / rect.height)),
    }
  }
  function start(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || saving || textMark || !image || !canvas.current) return
    if (event.target !== canvas.current && !contacts.current.size) return
    contacts.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    event.currentTarget.setPointerCapture(event.pointerId)
    if (contacts.current.size >= 2) {
      pointer.current = undefined
      setStroke(undefined)
      if (contacts.current.size === 2) gesture.current = { ...pair(), view }
      return
    }
    if (gesture.current) return
    pointer.current = event.pointerId
    const mark: Mark = { tool, points: [point(event)], unit: canvas.current.width / canvas.current.getBoundingClientRect().width }
    setStroke(mark)
  }
  function extend(mark: Mark, next: Point): Mark {
    return { ...mark, points: mark.tool === 'pen' ? [...mark.points, next] : [mark.points[0], next] }
  }
  function pair() {
    const [a, b] = contacts.current.values()
    return { center: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }, span: Math.max(1, Math.hypot(a.x - b.x, a.y - b.y)) }
  }
  function move(event: PointerEvent<HTMLDivElement>) {
    if (!contacts.current.has(event.pointerId)) return
    contacts.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    const initial = gesture.current
    if (initial) {
      if (contacts.current.size !== 2 || !canvas.current) return
      const { center, span } = pair()
      const area = event.currentTarget.getBoundingClientRect()
      const scale = Math.max(1, Math.min(8, initial.view.scale * span / initial.span))
      const ratio = scale / initial.view.scale
      const axis = (mid: number, start: number, anchor: number, offset: number, limit: number) =>
        Math.max(-limit, Math.min(limit, mid - anchor - (start - anchor - offset) * ratio))
      setView({
        scale,
        x: axis(center.x, initial.center.x, area.left + area.width / 2, initial.view.x, canvas.current.clientWidth * (scale - 1) / 2),
        y: axis(center.y, initial.center.y, area.top + area.height / 2, initial.view.y, canvas.current.clientHeight * (scale - 1) / 2),
      })
    } else if (pointer.current === event.pointerId && tool !== 'text' && tool !== 'number') {
      const next = point(event)
      setStroke(current => current && extend(current, next))
    }
  }
  function finish(event: PointerEvent<HTMLDivElement>) {
    if (!contacts.current.delete(event.pointerId)) return
    if (!contacts.current.size) gesture.current = undefined
    if (pointer.current !== event.pointerId) return
    pointer.current = undefined
    setStroke(undefined)
    if (!stroke || event.type !== 'pointerup') return
    if (stroke.tool === 'text') setTextMark({ ...stroke, label: '' })
    else if (stroke.tool === 'number') setMarks(previous => [...previous, { ...stroke, label: String(previous.filter(item => item.tool === 'number').length + 1) }])
    else {
      const mark = extend(stroke, point(event))
      setMarks(previous => [...previous, mark])
    }
  }
  function zoom(factor: number) {
    if (!canvas.current) return
    const { clientWidth, clientHeight } = canvas.current
    setView(previous => {
      const scale = Math.max(1, Math.min(8, previous.scale * factor))
      const ratio = scale / previous.scale
      const offset = (value: number, size: number) => {
        const limit = size * (scale - 1) / 2
        return Math.max(-limit, Math.min(limit, value * ratio))
      }
      return { scale, x: offset(previous.x, clientWidth), y: offset(previous.y, clientHeight) }
    })
  }
  function save() {
    if (!canvas.current || saving) return
    setSaving(true); setError('')
    canvas.current.toBlob(blob => {
      if (!alive.current) return
      const file = blob && new File([blob], name.replace(/\.[^.]+$/, '') + '-批注.png', { type: 'image/png' })
      if (file && onAdd(file)) return
      setSaving(false)
      setError(file ? '无法加入待发附件，请检查附件数量及大小限制。' : '无法导出原图尺寸的 PNG。')
    }, 'image/png')
  }
  return <Modal open closable={false} footer={null} width="100%" className="annotation-dialog" onCancel={onCancel}
    title={<div className="annotation-heading"><button aria-label="取消批注" onClick={onCancel}>×</button><strong>{name}</strong>
      <button className="annotation-add" disabled={!image || !marks.length || !!stroke || !!textMark || saving} onClick={save}>添加到聊天框</button></div>}>
    <div className="annotation-space"><div className="annotation-tools" role="toolbar" aria-label="批注工具">
      {tools.map(({ id, label, Icon }) => <button key={id} title={label} aria-label={label} aria-pressed={tool === id}
        disabled={saving || !!textMark} onClick={() => setTool(id)}><Icon /><small>{label}</small></button>)}
      <button title="撤销" aria-label="撤销" disabled={!marks.length || saving || !!textMark} onClick={() => setMarks(marks.slice(0, -1))}><UndoOutlined /><small>撤销</small></button>
    </div><div className="annotation-workarea"><div className="annotation-stage" onPointerDown={start} onPointerMove={move}
      onPointerUp={finish} onPointerCancel={finish} onLostPointerCapture={finish}>
      {image && <canvas ref={canvas} aria-label="批注画布" width={image.naturalWidth} height={image.naturalHeight}
        style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.scale})` }} />}
      {textMark && <form className="annotation-text" onSubmit={e => {
        e.preventDefault()
        if (textMark.label?.trim()) { setMarks([...marks, textMark]); setTextMark(undefined) }
      }}><input autoFocus aria-label="批注文字" value={textMark.label} onChange={e => setTextMark({ ...textMark, label: e.target.value })} />
        <button type="submit">确定</button><button type="button" onClick={() => setTextMark(undefined)}>取消</button></form>}
      {error && <span className="annotation-error" role="alert">{error}</span>}
    </div>
    <div className="annotation-zoom" role="group" aria-label="画布缩放">
      <button aria-label="缩小画布" disabled={!image || view.scale <= 1} onClick={() => zoom(.8)}>−</button>
      <button aria-label="恢复适应画面" onClick={() => setView({ x: 0, y: 0, scale: 1 })}>{Math.round(view.scale * 100)}%</button>
      <button aria-label="放大画布" disabled={!image || view.scale >= 8} onClick={() => zoom(1.25)}>＋</button>
    </div></div></div>
  </Modal>
}
