import { memo } from 'react'
import Markdown from 'react-markdown'
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

export const MessageBody = memo(function MessageBody({ content, mentions }: { content: string; mentions: MessageMention[] }) {
  return <div className="message-body">
    <Markdown remarkPlugins={mentions?.length ? [remarkGfm, [remarkMentions, mentions]] : [remarkGfm]}
      components={{
        img: ({ alt }) => <>{alt}</>,
        a: ({ href, children }) => href ? <a href={href} target="_blank" rel="noopener noreferrer">{children}</a> : <>{children}</>,
        table: ({ children }) => <div className="message-table"><table>{children}</table></div>,
      }}>{content}</Markdown>
  </div>
})
