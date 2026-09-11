import type { ReactNode } from 'react'
import { useEffect, useRef, useState } from 'react'
import { Button, Empty, Tag, Typography } from 'antd'
import type { ChatMessage } from '../types/realtimeChat'

function contentNodes(message: ChatMessage): ReactNode[]
{
  const ranges = message.mentions.filter(mention => mention.startOffset >= 0 && mention.length > 1 && mention.startOffset + mention.length <= message.content.length).sort((a, b) => a.startOffset - b.startOffset)
  const nodes: ReactNode[] = []
  let cursor = 0
  for (const range of ranges) {
    if (range.startOffset < cursor) continue
    nodes.push(message.content.slice(cursor, range.startOffset))
    nodes.push(<mark key={range.startOffset}>{message.content.slice(range.startOffset, range.startOffset + range.length)}</mark>)
    cursor = range.startOffset + range.length
  }
  nodes.push(message.content.slice(cursor))
  return nodes
}

export function MessageTimeline({ messages, hasEarlierMessages, loading, onLoadEarlier, newMessageCount, onNewMessageClick }: { messages: ChatMessage[]; hasEarlierMessages: boolean; loading: boolean; onLoadEarlier: () => void; newMessageCount: number; onNewMessageClick: () => void })
{
  const scrollRef = useRef<HTMLDivElement>(null)
  const [atBottom, setAtBottom] = useState(true)
  const updateBottom = () => { const element = scrollRef.current; if (element) setAtBottom(element.scrollHeight - element.scrollTop - element.clientHeight < 24) }
  useEffect(() => { const element = scrollRef.current; if (!element) return; element.addEventListener('scroll', updateBottom, { passive: true }); updateBottom(); return () => element.removeEventListener('scroll', updateBottom) }, [])
  useEffect(() => { const element = scrollRef.current; if (element && atBottom) element.scrollTop = element.scrollHeight }, [messages, atBottom])
  useEffect(() => { if (atBottom && newMessageCount > 0) onNewMessageClick() }, [atBottom, newMessageCount, onNewMessageClick])
  const jumpToLatest = () => { const element = scrollRef.current; if (element) element.scrollTop = element.scrollHeight; setAtBottom(true); onNewMessageClick() }
  return <section className="message-timeline">
    {(hasEarlierMessages || (newMessageCount > 0 && !atBottom)) && <div className="message-timeline-toolbar">
      {hasEarlierMessages && <Button size="small" loading={loading} onClick={onLoadEarlier}>加载更早消息</Button>}
      {newMessageCount > 0 && !atBottom && <Button type="link" size="small" onClick={jumpToLatest}>有 {newMessageCount} 条新消息</Button>}
    </div>}
    {messages.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="还没有消息" /> : <div ref={scrollRef} className="message-timeline-scroll" role="log" aria-label="会议消息">
      {messages.map(message => <article key={message.messageId} className={'message-item message-' + message.author.kind}>
        <header className="message-meta">
          <Tag className="message-sequence">#{message.sequence}</Tag>
          <Typography.Text strong className="message-author">{message.author.displayName}</Typography.Text>
          <time className="message-time" dateTime={message.createdAt.toISOString()}>{message.createdAt.toLocaleString()}</time>
          <Tag className="message-kind" color={message.author.kind === 'agent' ? 'cyan' : message.author.kind === 'human' ? 'blue' : 'default'}>{message.author.kind === 'agent' ? 'Agent' : message.author.kind === 'system' ? '系统' : '人类'}</Tag>
        </header>
        <Typography.Paragraph className="message-body">{contentNodes(message)}</Typography.Paragraph>
      </article>)}
    </div>}
  </section>
}
