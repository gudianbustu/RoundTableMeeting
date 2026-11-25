import { useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react'
import './ReadingPane.css'

const minimum = 480
const initial = 1060

export function ReadingPane({ children, onWidthChange }: { children: ReactNode; onWidthChange?: (width: number) => void }) {
  const container = useRef<HTMLDivElement>(null)
  const frame = useRef<HTMLDivElement>(null)
  const drag = useRef<{ pointer: number; start: number; width: number } | undefined>(undefined)
  const [available, setAvailable] = useState(initial)
  const [preferred, setPreferred] = useState(initial)
  const [dragging, setDragging] = useState(false)
  const lower = Math.min(minimum, available)
  const clamp = (value: number) => Math.max(lower, Math.min(Math.round(value), available))
  const width = clamp(preferred)

  useLayoutEffect(() => { onWidthChange?.(width) }, [onWidthChange, width])

  useLayoutEffect(() => {
    const element = container.current
    if (!element) return
    const measure = () => setAvailable(element.clientWidth)
    measure()
    const observer = new ResizeObserver(measure)
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  function start(event: PointerEvent<HTMLDivElement>) {
    if (!event.isPrimary || event.button !== 0 || !frame.current) return
    event.currentTarget.setPointerCapture(event.pointerId)
    drag.current = { pointer: event.pointerId, start: event.clientX, width: frame.current.getBoundingClientRect().width }
    setDragging(true)
  }
  function move(event: PointerEvent<HTMLDivElement>) {
    const active = drag.current
    if (active?.pointer === event.pointerId)
      setPreferred(clamp(active.width + (event.clientX - active.start) * 2))
  }
  function finish() { drag.current = undefined; setDragging(false) }
  function keydown(event: KeyboardEvent) {
    const values: Record<string, number> = { ArrowLeft: width - 16, ArrowRight: width + 16, Home: lower, End: available }
    if (!Object.hasOwn(values, event.key)) return
    event.preventDefault()
    setPreferred(clamp(values[event.key]))
  }
  return <div ref={container} className={'reading-pane' + (dragging ? ' reading-pane-dragging' : '')}>
    <div ref={frame} className="reading-frame" style={{ maxWidth: width }}>
      {children}
      <div className="reading-resizer" role="separator" aria-orientation="vertical" aria-label="调整阅读区右边缘"
        aria-valuemin={lower} aria-valuemax={available} aria-valuenow={width} tabIndex={0}
        onPointerDown={start} onPointerMove={move} onPointerUp={finish} onPointerCancel={finish}
        onLostPointerCapture={finish} onKeyDown={keydown} />
    </div>
  </div>
}
