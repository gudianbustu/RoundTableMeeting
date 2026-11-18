import type { ReactNode } from 'react'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Button, Empty, Tag, Typography } from 'antd'
import type { ChatMessage, ChatScrollPosition } from '../types/realtimeChat'
import { getMessage } from '../api/messageResources'
import { AttachmentLinks } from './AttachmentControls'

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

function ReplyQuote({ meetingId, id, loaded, onLocate }: {
  meetingId: string; id: string; loaded?: ChatMessage; onLocate?: (id: string) => Promise<void>
}) {
  const [fetched, setFetched] = useState<ChatMessage>()
  const [unavailable, setUnavailable] = useState(false)
  useEffect(() => {
    if (loaded) return
    const controller = new AbortController()
    getMessage(meetingId, id, controller.signal).then(original => {
      if (!controller.signal.aborted) setFetched(original)
    }).catch(() => { if (!controller.signal.aborted) setUnavailable(true) })
    return () => controller.abort()
  }, [meetingId, id, loaded])
  const original = loaded ?? fetched
  const excerpt = original?.content.trim() || original?.attachments?.map(file => file.name).join('、') || ''
  const preview = excerpt.match(/^.{1,96}/su)?.[0] ?? ''
  const label = original ? `回复 ${original.author.displayName} · ${preview || '原消息'}${preview.length < excerpt.length ? '…' : ''}` : unavailable ? '引用的原消息暂不可用' : '正在读取引用…'
  return <button type="button" className="message-quote" title={original ? '定位到原消息' : undefined}
    disabled={!original || !onLocate} onClick={() => { void onLocate?.(id).catch(() => setUnavailable(true)) }}>
    {unavailable && original ? '暂时无法定位 · ' : ''}{label}
  </button>
}

type Props = { onLocate?: (id: string) => Promise<void>; pendingMessage?: ReactNode; messages: ChatMessage[]; hasEarlierMessages: boolean; loading: boolean; onLoadEarlier: () => void; newMessageCount: number; onNewMessageClick: () => void; onReply?: (message: ChatMessage) => void; onPrivateChat?: (id: string) => void; currentParticipantId?: string; focusMessageId?: string; focusRevision?: number; canReply?: boolean; scrollPosition?: ChatScrollPosition; onScrollPositionChanged?: (position: ChatScrollPosition) => void }
export function MessageTimeline({ onLocate, pendingMessage, messages, hasEarlierMessages, loading, onLoadEarlier, onReply, onPrivateChat, currentParticipantId, focusMessageId, focusRevision, canReply = true, scrollPosition, onScrollPositionChanged }: Props) {
  const originals = new Map(messages.map(message => [message.messageId, message]))
  const scrollRef = useRef<HTMLDivElement>(null)
  const processedFocus = useRef<string | undefined>(undefined)
  const itemsRef = useRef<HTMLDivElement>(null)
  const position = useRef<ChatScrollPosition>(scrollPosition ?? { atBottom: true })
  const [atBottom, setAtBottom] = useState(position.current.atBottom)
  const capture = useCallback(() => {
    const element = scrollRef.current; if (!element) return
    const bottom = element.scrollHeight - element.scrollTop - element.clientHeight <= 48
    const top = element.getBoundingClientRect().top
    const first = Array.from(element.querySelectorAll<HTMLElement>('[data-message-id]')).find(row => row.getBoundingClientRect().bottom > top)
    position.current = bottom ? { atBottom: true } : { atBottom: false, messageId: first?.dataset.messageId, offset: first ? first.getBoundingClientRect().top - top : 0 }
    setAtBottom(bottom); onScrollPositionChanged?.(position.current)
  }, [onScrollPositionChanged])
  const restore = useCallback(() => {
    const element = scrollRef.current; if (!element) return
    if (position.current.atBottom) element.scrollTop = element.scrollHeight
    else if (position.current.messageId) {
      const row = Array.from(element.querySelectorAll<HTMLElement>('[data-message-id]')).find(item => item.dataset.messageId === position.current.messageId)
      if (row) element.scrollTop += row.getBoundingClientRect().top - element.getBoundingClientRect().top - (position.current.offset ?? 0)
    }
  }, [])
  useLayoutEffect(restore, [messages, pendingMessage, restore])
  useLayoutEffect(() => {
    if (!focusMessageId || processedFocus.current === `${focusMessageId}:${focusRevision}`) return
    const element = scrollRef.current
    const row = Array.from(element?.querySelectorAll<HTMLElement>('[data-message-id]') ?? []).find(item => item.dataset.messageId === focusMessageId)
    if (element && row) {
      processedFocus.current = `${focusMessageId}:${focusRevision}`
      position.current = { atBottom: false, messageId: focusMessageId, offset: 0 }; restore(); setAtBottom(false)
      row.focus({ preventScroll: true }); onScrollPositionChanged?.(position.current)
    }
  }, [focusMessageId, focusRevision, messages, restore, onScrollPositionChanged])
  useEffect(() => {
    const element = scrollRef.current; if (!element) return
    element.addEventListener('scroll', capture, { passive: true })
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(restore)
    observer?.observe(element); if (itemsRef.current) observer?.observe(itemsRef.current)
    return () => { element.removeEventListener('scroll', capture); observer?.disconnect() }
  }, [capture, restore])
  const jumpToLatest = () => { position.current = { atBottom: true }; restore(); setAtBottom(true); onScrollPositionChanged?.(position.current) }
  return <section className="message-timeline">
    {(hasEarlierMessages || !atBottom) && <div className="message-timeline-toolbar">
      {hasEarlierMessages && <Button size="small" loading={loading} onClick={() => { capture(); onLoadEarlier() }}>加载更早消息</Button>}
      {!atBottom && <Button type="link" size="small" onClick={jumpToLatest}>回到最新</Button>}
    </div>}
    <div ref={scrollRef} className="message-timeline-scroll" role="log" aria-label="会议消息"><div ref={itemsRef} className="message-timeline-items">
      {messages.length === 0 && !pendingMessage && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="还没有消息" />}
      {messages.map(message => <article key={message.messageId} data-message-id={message.messageId} tabIndex={-1} className={'message-item message-' + message.author.kind + (currentParticipantId && message.author.participantId === currentParticipantId ? ' message-own' : '')}>
        <header className="message-meta">
          <Typography.Text strong className="message-author">{message.author.displayName}</Typography.Text>
          <time className="message-time" dateTime={message.createdAt.toISOString()}>{message.createdAt.toLocaleString()}</time>
          <Tag className="message-kind" color={message.author.kind === 'agent' ? 'cyan' : message.author.kind === 'human' ? 'blue' : 'default'}>{message.author.kind === 'agent' ? 'Agent' : message.author.kind === 'system' ? '系统' : '人类'}</Tag>
          {message.visibility === 'private' && <Tag color="purple">私聊</Tag>}
          {canReply && onReply && <Button size="small" type="text" onClick={() => onReply(message)}>回复</Button>}
          {onPrivateChat && message.author.participantId && message.author.participantId !== currentParticipantId && <Button size="small" type="text" onClick={() => onPrivateChat(message.author.participantId!)}>私聊</Button>}
        </header>
        {message.replyToMessageId && <ReplyQuote key={message.meetingId + message.replyToMessageId} meetingId={message.meetingId} id={message.replyToMessageId} loaded={originals.get(message.replyToMessageId)} onLocate={onLocate} />}
        {message.content && <Typography.Paragraph className="message-body">{contentNodes(message)}</Typography.Paragraph>}
        {!!message.attachments?.length && <AttachmentLinks meetingId={message.meetingId} attachments={message.attachments} />}
      </article>)}
      {pendingMessage}
    </div></div>
  </section>
}
