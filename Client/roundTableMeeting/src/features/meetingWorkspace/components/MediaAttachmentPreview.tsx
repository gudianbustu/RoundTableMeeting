import { useEffect, useRef, useState } from 'react'
import { Alert, Button } from 'antd'

function Player({ kind, name, url }: { kind: 'audio' | 'video'; name: string; url: string }) {
  const media = useRef<HTMLMediaElement | null>(null)
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    const element = media.current
    if (element) element.src = url
    return () => {
      if (!element) return
      element.pause()
      element.removeAttribute('src')
      element.load()
    }
  }, [url])
  const props = { src: url, controls: true, preload: 'metadata', 'aria-label': name, onError: () => setFailed(true), style: { width: '100%', maxHeight: '65vh', display: failed ? 'none' : 'block' } }
  return <>
    {kind === 'video' ? <video {...props} playsInline ref={element => { media.current = element }} /> : <audio {...props} ref={element => { media.current = element }} />}
    {failed && <Alert type="error" title="媒体预览失败，当前浏览器可能不支持此编码，或文件已损坏、暂时无法访问。" />}
  </>
}

export default function MediaAttachmentPreview({ kind, name, url }: { kind: 'audio' | 'video'; name: string; url: string }) {
  const [attempt, setAttempt] = useState(0)
  return <div>
    <Player key={`${url}:${attempt}`} kind={kind} name={name} url={url} />
    <Button size="small" style={{ marginTop: 12 }} onClick={() => setAttempt(value => value + 1)}>重新加载</Button>
  </div>
}
