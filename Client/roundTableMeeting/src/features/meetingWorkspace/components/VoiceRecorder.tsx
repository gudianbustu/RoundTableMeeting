import { useEffect, useRef, useState } from 'react'
import { AudioOutlined, PauseOutlined, CaretRightOutlined } from '@ant-design/icons'
import { Button } from 'antd'

function clock(seconds: number) {
  return [Math.floor(seconds / 60), seconds % 60].map(value => String(value).padStart(2, '0')).join(':')
}

export function VoiceRecorder({ disabled, onFile, onActiveChange }: { onActiveChange?: (active: boolean) => void; disabled: boolean; onFile: (file: File) => void }) {
  const [phase, setPhase] = useState<'idle' | 'requesting' | 'recording' | 'finishing'>('idle')
  const active = phase !== 'idle'
  useEffect(() => { onActiveChange?.(active) }, [active, onActiveChange])
  const [elapsed, setElapsed] = useState(0)
  const [error, setError] = useState('')
  const session = useRef<{ recorder?: MediaRecorder; stream?: MediaStream; started: number; keep: boolean } | null>(null)
  const release = () => {
    const current = session.current
    session.current = null
    if (current?.recorder?.state === 'recording') current.recorder.stop()
    current?.stream?.getTracks().forEach(track => track.stop())
  }
  useEffect(() => release, [])
  useEffect(() => {
    if (disabled) { release(); setPhase('idle') }
  }, [disabled])
  useEffect(() => {
    if (phase !== 'recording') return
    const timer = window.setInterval(() => {
      if (session.current) setElapsed(Math.floor((performance.now() - session.current.started) / 1000))
    }, 250)
    return () => window.clearInterval(timer)
  }, [phase])
  const start = async () => {
    if (disabled || session.current) return
    setError('')
    if (!navigator.mediaDevices?.getUserMedia || typeof MediaRecorder === 'undefined') {
      setError('当前浏览器无法录音，请在支持录音的安全连接中打开。')
      return
    }
    const current: NonNullable<typeof session.current> = { started: 0, keep: false }
    session.current = current
    setPhase('requesting')
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      if (session.current !== current) { stream.getTracks().forEach(track => track.stop()); return }
      current.stream = stream
      const recorder = new MediaRecorder(stream)
      current.recorder = recorder
      const chunks: Blob[] = []
      recorder.ondataavailable = event => { if (event.data.size) chunks.push(event.data) }
      recorder.onerror = () => {
        if (session.current !== current) return
        release(); setPhase('idle'); setError('录音中断，请重新录制。')
      }
      recorder.onstop = () => {
        if (session.current !== current) return
        const duration = Math.max(1, Math.round((performance.now() - current.started) / 1000))
        release(); setPhase('idle')
        if (!current.keep || !chunks.length) { setError('未取得完整录音，请重新录制。'); return }
        const type = recorder.mimeType || chunks[0].type
        const extension = type.includes('mp4') ? 'm4a' : type.includes('ogg') ? 'ogg' : 'webm'
        onFile(new File(chunks, `录音-${Date.now()}-${duration}s.${extension}`, { type }))
      }
      recorder.start()
      current.started = performance.now()
      setElapsed(0); setPhase('recording')
    } catch {
      if (session.current !== current) return
      release(); setPhase('idle'); setError('无法使用麦克风，请检查设备和录音权限。')
    }
  }
  return <span className="voice-recorder" data-active={active || undefined}>
    {phase === 'idle' ? <Button aria-label="录音" title="录音" icon={<AudioOutlined />} disabled={disabled} onClick={() => void start()} /> : <>
      <span className="voice-recorder-time">{phase === 'requesting' ? '等待麦克风' : clock(elapsed)}</span>
      <Button size="small" aria-label="取消" onClick={() => { release(); setPhase('idle') }}>取消</Button>
      {phase === 'recording' && <Button type="primary" aria-label="完成" size="small" onClick={() => {
        const current = session.current
        if (current?.recorder?.state !== 'recording') return
        current.keep = true; setPhase('finishing'); current.recorder.stop()
      }}>完成</Button>}
    </>}
    {error && <span role="alert">{error}</span>}
  </span>
}

export function DraftVoice({ file }: { file: File }) {
  const audio = useRef<HTMLAudioElement>(null)
  const [playing, setPlaying] = useState(false)
  const [error, setError] = useState(false)
  const duration = Number(/-(\d+)s\./.exec(file.name)?.[1] ?? 0)
  useEffect(() => {
    const element = audio.current!
    const url = URL.createObjectURL(file)
    element.src = url
    return () => { element.removeAttribute('src'); element.load(); URL.revokeObjectURL(url) }
  }, [file])
  return <><audio ref={audio} preload="none" onPlaying={() => setPlaying(true)} onPause={() => setPlaying(false)} onEnded={() => setPlaying(false)} />
    <button type="button" className="draft-attachment-preview" aria-label={`${playing ? '暂停' : '试听'} ${file.name}`} onClick={() => {
      if (playing) audio.current?.pause()
      else void audio.current?.play().catch(() => setError(true))
    }}>{playing ? <PauseOutlined /> : <CaretRightOutlined />} {clock(duration)}</button>
    {error && <span role="alert">无法播放</span>}</>
}
