import { memo } from 'react'
import Markdown, { type Components } from 'react-markdown'
import { ChatDiagram } from './ChatDiagram'
import remarkGfm from 'remark-gfm'
import type {} from 'remark-parse'
import type { Plugin } from 'unified'
import type { State, Tokenizer } from 'micromark-util-types'
import type { Extension } from 'mdast-util-from-markdown'
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
const renderPre: Components['pre'] = ({ node, children }) => {
  const code = node?.children[0]
  if (code?.type === 'element' && code.tagName === 'code' && Array.isArray(code.properties.className)
    && code.properties.className.some(name => /^language-(?:mermaid|mmd)$/i.test(String(name)))) {
    const source = code.children.map(child => child.type === 'text' ? child.value : '').join('').trimEnd()
    return <ChatDiagram key={source} source={source} />
  }
  return <pre>{children}</pre>
}

export const MessageBody = memo(function MessageBody({ content, mentions, diagrams = false }: { content: string; mentions: MessageMention[]; diagrams?: boolean }) {
  if (diagrams && diagramHeader.test(content.trimStart()))
    return <div className="message-body"><ChatDiagram key={content} source={content.trim()} /></div>
  return <div className="message-body">
    <Markdown remarkPlugins={mentions?.length ? [remarkGfm, [remarkMentions, mentions]] : [remarkGfm]}
      components={{
        ...(diagrams ? { pre: renderPre } : {}),
        img: ({ alt }) => <>{alt}</>,
        a: ({ href, children }) => href ? <a href={href} target="_blank" rel="noopener noreferrer">{children}</a> : <>{children}</>,
        table: ({ children }) => <div className="message-table"><table>{children}</table></div>,
      }}>{content}</Markdown>
  </div>
})
