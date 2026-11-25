import { useMemo } from 'react'
import { Alert } from 'antd'
import mermaidUrl from 'mermaid/dist/mermaid.min.js?url'
import frameTemplate from './MermaidDiagramFrame.html?raw'

const escapeText = (value: string) => value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;')

export default function MermaidDiagram({ source }: { source: string }) {
  const markup = useMemo(() => {
    if (source.length > 50_000) return ''
    const nonce = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('')
    return frameTemplate.replaceAll('__NONCE__', nonce)
      .replace('__RENDERER__', () => new URL(mermaidUrl, window.location.href).href)
      .replace('__SOURCE__', () => escapeText(source))
  }, [source])
  if (!markup) return <Alert type="warning" title="图表超过 50,000 字符，请下载文件查看。" />
  return <iframe title="Mermaid 图表" sandbox="allow-scripts" referrerPolicy="no-referrer" srcDoc={markup}
    style={{ width: '100%', height: 'min(58vh, 520px)', border: 0, background: '#fff' }} />
}
