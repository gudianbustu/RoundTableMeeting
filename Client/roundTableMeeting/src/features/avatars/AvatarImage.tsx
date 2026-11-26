import { useState, type ReactNode } from 'react'
import './avatars.css'

const pageVersion = Math.random().toString(36).slice(2)

const slotColors = [
  '#4CB5AA', '#E58D65', '#879DE8', '#D2AA45', '#BE88C7', '#77AA70', '#DC8197', '#72ADCD',
  '#357F77', '#A06347', '#5F6EA2', '#937730', '#855F8B', '#53774E', '#9A5A6A', '#507990',
]

export function AvatarImage({ src, fallback, colorSlot }: { src?: string; fallback: ReactNode; colorSlot?: number }) {
  const [failed, setFailed] = useState<string>()
  const image = src && src !== failed
    ? <img className="rt-avatar-image" src={src.startsWith('/Avatars/') ? `${src}?v=${pageVersion}` : src} alt="" decoding="async" onError={() => setFailed(src)} />
    : fallback
  if (!src || colorSlot === undefined) return <>{image}</>
  return <span className="rt-meeting-avatar" title={`头像席位 ${colorSlot + 1}`} style={{ backgroundColor: slotColors[colorSlot % slotColors.length] }}>{image}</span>
}
