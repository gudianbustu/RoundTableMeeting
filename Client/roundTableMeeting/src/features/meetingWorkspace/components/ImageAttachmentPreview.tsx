import { useState } from 'react'
import { Alert, Button, Spin } from 'antd'

export default function ImageAttachmentPreview({ url, name }: { url: string; name: string }) {
  const [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<'loading' | 'loaded' | 'failed'>('loading')
  return <div>
    {state === 'loading' && <Spin />}
    {state === 'failed' && <Alert type="error" title="图片预览失败，文件可能已损坏或暂时无法访问。" action={<Button onClick={() => { setState('loading'); setAttempt(value => value + 1) }}>重试</Button>} />}
    <img key={attempt} className="attachment-image-preview" style={{ display: state === 'failed' ? 'none' : undefined }} alt={name} src={url.startsWith('blob:') ? url : `${url}&previewAttempt=${attempt}`} onLoad={() => setState('loaded')} onError={() => setState('failed')} />
  </div>
}
