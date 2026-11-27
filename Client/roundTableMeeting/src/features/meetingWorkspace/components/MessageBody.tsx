import { memo, useState } from 'react'
import { CheckOutlined, CopyOutlined } from '@ant-design/icons'
import Markdown, { type Components } from 'react-markdown'
import { ChatDiagram } from './ChatDiagram'
import rehypeHighlight from 'rehype-highlight'
import remarkGfm from 'remark-gfm'
import type {} from 'remark-parse'
import type { Plugin } from 'unified'
import type { State, Tokenizer } from 'micromark-util-types'
import type { Extension } from 'mdast-util-from-markdown'
import { copyText } from '@/shared/utils/copyText'
import type { MessageMention } from '../types/realtimeChat'

declare module 'micromark-util-types' {
  interface TokenTypeMap { chatMention: 'chatMention' }
}

// Tokenize confirmed ranges before inline formatting so punctuation in a name stays literal.
const remarkMentions: Plugin<[MessageMention[]]> = function (mentions) {
  const ends = new Map(mentions.filter(m => m.length > 1).map(m => [m.startOffset, m.startOffset + m.length]))
  const tokenize: Tokenizer = function (effects, ok, nok) {
    const end = ends.get(this.now().offset)
    const consume: State = code => {
      if (this.now().offset === end) {
        effects.exit('chatMention')
        return ok(code)
      }
      if (code === null || code < 0) return nok(code)
      effects.consume(code)
      return consume
    }
    return code => {
      if (end === undefined) return nok(code)
      effects.enter('chatMention')
      return consume(code)
    }
  }
  const extension: Extension = {
    enter: { chatMention(token) {
      this.enter({ type: 'strong', data: { hName: 'mark' }, children: [{ type: 'text', value: this.sliceSerialize(token) }] }, token)
    } },
    exit: { chatMention(token) { this.exit(token) } },
  }
  const data = this.data()
  ;(data.micromarkExtensions ??= []).push({ text: { 64: { tokenize } } })
  ;(data.fromMarkdownExtensions ??= []).push(extension)
}

// A bare message needs a diagram declaration. Prose discussing a diagram type stays Markdown.
const diagramHeader = /^(?:(?:flowchart|graph)[ \t]+(?:TD|TB|BT|LR|RL)|(?:sequenceDiagram|classDiagram|stateDiagram(?:-v2)?|erDiagram|mindmap|timeline|journey|gantt|gitGraph|quadrantChart|requirementDiagram|C4Context|C4Container|C4Component|C4Dynamic|C4Deployment|sankey-beta|xychart-beta|block-beta|packet-beta|architecture-beta|kanban|radar-beta|treemap-beta)|pie(?:[ \t]+showData)?(?:[ \t]+title[ \t]+[^\n]+)?)[ \t]*(?:;|\r?\n|$)/

type HastNode = { type?: string; value?: string; tagName?: string; properties?: { className?: unknown }; children?: HastNode[] }

function extractHastText(node: HastNode | undefined): string {
  if (!node) return ''
  if (node.type === 'text') return node.value ?? ''
  return (node.children ?? []).map(extractHastText).join('')
}

function extractCodeLanguage(code: HastNode | undefined): string | undefined {
  const classes = code?.properties?.className
  if (!Array.isArray(classes)) return undefined
  for (const item of classes) {
    const match = /^language-([a-z0-9_+#.-]+)$/i.exec(String(item))
    if (match) return match[1]
  }
  return undefined
}

function CodeBlock({ language, source, children }: { language?: string; source: string; children: React.ReactNode }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    try {
      await copyText(source)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1500)
    } catch { /* ignore clipboard failure */ }
  }
  return <div className="message-code-block">
    <div className="message-code-header">
      <span className="message-code-lang">{language ?? 'code'}</span>
      <button type="button" className="message-code-copy" aria-label="复制代码" onClick={() => void copy()}>
        {copied ? <><CheckOutlined aria-hidden="true" /><span>已复制</span></> : <><CopyOutlined aria-hidden="true" /><span>复制</span></>}
      </button>
    </div>
    <pre>{children}</pre>
  </div>
}

function createPreRenderer(diagrams: boolean): Components['pre'] {
  return ({ node, children }) => {
    const code = (node as HastNode | undefined)?.children?.[0]
    const language = code?.type === 'element' && code.tagName === 'code' ? extractCodeLanguage(code) : undefined
    const source = extractHastText(code).trimEnd()
    if (diagrams && language && /^(?:mermaid|mmd)$/i.test(language)) {
      return <ChatDiagram key={source} source={source} />
    }
    return <CodeBlock language={language} source={source}>{children}</CodeBlock>
  }
}

const highlightOptions = { detect: false, ignoreMissing: true, plainText: ['mermaid', 'mmd'] }

export const MessageBody = memo(function MessageBody({ content, mentions, diagrams = false }: { content: string; mentions: MessageMention[]; diagrams?: boolean }) {
  if (diagrams && diagramHeader.test(content.trimStart()))
    return <div className="message-body"><ChatDiagram key={content} source={content.trim()} /></div>
  return <div className="message-body">
    <Markdown remarkPlugins={mentions?.length ? [remarkGfm, [remarkMentions, mentions]] : [remarkGfm]}
      rehypePlugins={[[rehypeHighlight, highlightOptions]]}
      components={{
        pre: createPreRenderer(diagrams),
        img: ({ alt }) => <>{alt}</>,
        a: ({ href, children }) => href ? <a href={href} target="_blank" rel="noopener noreferrer">{children}</a> : <>{children}</>,
        table: ({ children }) => <div className="message-table"><table>{children}</table></div>,
      }}>{content}</Markdown>
  </div>
})
