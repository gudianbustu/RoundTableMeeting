import { AvatarImage } from '@/features/avatars/AvatarImage'
import { ReadingPane } from './ReadingPane'
import './WorkspaceMessages.css'
import { RobotOutlined, InfoCircleOutlined, DownOutlined, UpOutlined, ReadOutlined } from '@ant-design/icons'
import { MessageBody } from './MessageBody'
import type { ReactNode } from 'react'
import { memo, useMemo, useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Button, Empty, Tag, Typography } from 'antd'
import type { ChatAttachment, ChatMessage, ChatScrollPosition } from '../types/realtimeChat'
import { getMessage } from '../api/messageResources'
import { OriginalMessageText } from './OriginalMessageText'
import { ConversationAttachmentViewer } from './ConversationAttachmentViewer'
import { AttachmentLinks } from './AttachmentControls'

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

const GROUP_WINDOW_MS = 5 * 60 * 1000

function isGroupedWithPrevious(previous: ChatMessage | undefined, current: ChatMessage) {
  if (!previous || current.replyToMessageId) return false
  if ((previous.visibility ?? 'public') !== (current.visibility ?? 'public')) return false
  if (previous.author.kind !== current.author.kind) return false
  const sameAuthor = previous.author.participantId && current.author.participantId
    ? previous.author.participantId === current.author.participantId
    : previous.author.displayName === current.author.displayName
  if (!sameAuthor) return false
  const delta = current.createdAt.getTime() - previous.createdAt.getTime()
  return delta >= 0 && delta <= GROUP_WINDOW_MS
}

type Props = { onWidthChange?: (width: number) => void; onLocate?: (id: string) => Promise<void>; pendingMessage?: ReactNode; messages: ChatMessage[]; hasEarlierMessages: boolean; loading: boolean; onLoadEarlier: () => void; newMessageCount: number; onNewMessageClick: () => void; onReply?: (message: ChatMessage) => void; onPrivateChat?: (id: string) => void; onReadInPanel?: (message: ChatMessage) => void; onReadMarkdownFile?: (entry: { file: ChatAttachment; author?: string; createdAt?: Date }) => void; currentParticipantId?: string; focusMessageId?: string; focusRevision?: number; canReply?: boolean; scrollPosition?: ChatScrollPosition; onScrollPositionChanged?: (position: ChatScrollPosition) => void }
export function MessageTimeline({ onWidthChange, onLocate, pendingMessage, messages, hasEarlierMessages, loading, onLoadEarlier, newMessageCount, onNewMessageClick, onReply, onPrivateChat, onReadInPanel, onReadMarkdownFile, currentParticipantId, focusMessageId, focusRevision, canReply = true, scrollPosition, onScrollPositionChanged }: Props) {
  const [previewId, setPreviewId] = useState<string>()
  const originals = useMemo(() => new Map(messages.map(message => [message.messageId, message])), [messages])
  const scrollRef = useRef<HTMLDivElement>(null)
  const processedFocus = useRef<string | undefined>(undefined)
  const itemsRef = useRef<HTMLDivElement>(null)
  const position = useRef<ChatScrollPosition>(scrollPosition ?? { atBottom: true })
  const restoredTop = useRef<number | undefined>(undefined)
  const jumpTarget = useRef<number | undefined>(undefined)
  const jumpDeadline = useRef<number | undefined>(undefined)
  useEffect(() => () => window.clearTimeout(jumpDeadline.current), [])
  const [atBottom, setAtBottom] = useState(position.current.atBottom)
  useEffect(() => {
    if (atBottom && newMessageCount > 0) onNewMessageClick()
  }, [atBottom, newMessageCount, onNewMessageClick])
  const capture = useCallback(() => {
    const element = scrollRef.current; if (!element) return
    // A scroll event from restore() may arrive after content has grown.
    // Keep the saved intent until the viewport actually moves independently.
    if (jumpTarget.current === undefined && restoredTop.current === element.scrollTop) return
    restoredTop.current = undefined
    if (jumpTarget.current !== undefined) {
      if (element.scrollTop + 2 < Math.min(jumpTarget.current, element.scrollHeight - element.clientHeight)) return
      jumpTarget.current = undefined
      element.scrollTop = element.scrollHeight
    }
    const bottom = element.scrollHeight - element.scrollTop - element.clientHeight <= 2
    if (bottom) {
      position.current = { atBottom: true }
    } else {
      const top = element.getBoundingClientRect().top
      const rows = element.querySelectorAll<HTMLElement>('[data-message-id]')
      let low = 0
      let high = rows.length
      while (low < high) {
        const middle = Math.floor((low + high) / 2)
        if (rows[middle].getBoundingClientRect().bottom <= top) low = middle + 1
        else high = middle
      }
      const first = rows[low]
      position.current = { atBottom: false, messageId: first?.dataset.messageId, offset: first ? first.getBoundingClientRect().top - top : 0 }
    }
    setAtBottom(bottom); onScrollPositionChanged?.(position.current)
  }, [onScrollPositionChanged])
  const restore = useCallback(() => {
    const element = scrollRef.current; if (!element) return
    if (jumpTarget.current !== undefined) return
    if (position.current.atBottom) element.scrollTop = element.scrollHeight
    else if (position.current.messageId) {
      const row = Array.from(element.querySelectorAll<HTMLElement>('[data-message-id]')).find(item => item.dataset.messageId === position.current.messageId)
      if (row) element.scrollTop += row.getBoundingClientRect().top - element.getBoundingClientRect().top - (position.current.offset ?? 0)
    }
    restoredTop.current = element.scrollTop
  }, [])
  useLayoutEffect(restore, [messages, pendingMessage, restore])
  useLayoutEffect(() => {
    if (!focusMessageId || processedFocus.current === `${focusMessageId}:${focusRevision}`) return
    const element = scrollRef.current
    const row = Array.from(element?.querySelectorAll<HTMLElement>('[data-message-id]') ?? []).find(item => item.dataset.messageId === focusMessageId)
    if (element && row) {
      jumpTarget.current = undefined
      processedFocus.current = `${focusMessageId}:${focusRevision}`
      position.current = { atBottom: false, messageId: focusMessageId, offset: 0 }; restore(); setAtBottom(false)
      row.focus({ preventScroll: true }); onScrollPositionChanged?.(position.current)
    }
  }, [focusMessageId, focusRevision, messages, restore, onScrollPositionChanged])
  useLayoutEffect(() => {
    const element = scrollRef.current; if (!element) return
    element.addEventListener('scroll', capture, { passive: true })
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(restore)
    observer?.observe(element); if (itemsRef.current) observer?.observe(itemsRef.current)
    return () => { element.removeEventListener('scroll', capture); observer?.disconnect() }
  }, [capture, restore])
  const interruptJump = () => {
    const element = scrollRef.current
    if (jumpTarget.current === undefined || !element) return
    jumpTarget.current = undefined
    element.scrollTo({ top: element.scrollTop, behavior: 'instant' })
    capture()
  }
  const jumpToLatest = () => {
    const element = scrollRef.current
    if (!element) return
    restoredTop.current = undefined
    const target = element.scrollHeight - element.clientHeight
    const smooth = !window.matchMedia('(prefers-reduced-motion: reduce)').matches
    jumpTarget.current = smooth ? target : undefined
    window.clearTimeout(jumpDeadline.current)
    element.scrollTo({ top: target, behavior: smooth ? 'smooth' : 'instant' })
    if (!smooth) { capture(); return }
    // A layout change can cancel a native animation without a final scroll event.
    jumpDeadline.current = window.setTimeout(() => {
      if (jumpTarget.current === undefined || scrollRef.current !== element) return
      jumpTarget.current = undefined
      element.scrollTo({ top: element.scrollHeight - element.clientHeight, behavior: 'instant' })
      capture()
    }, 1200)
  }
  return <section className="message-timeline">
    {hasEarlierMessages && <div className="message-timeline-toolbar">
      <Button type="text" size="small" loading={loading} onClick={() => { capture(); onLoadEarlier() }}>加载更早消息</Button>
    </div>}
    <ReadingPane onWidthChange={onWidthChange}><div ref={scrollRef} className="message-timeline-scroll" tabIndex={0}
      onWheelCapture={interruptJump} onPointerDownCapture={interruptJump} onKeyDownCapture={interruptJump} role="log" aria-label="会议消息"><div ref={itemsRef} className="message-timeline-items">
      {messages.length === 0 && !pendingMessage && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="还没有消息" />}
      {messages.map((message, index) => <MessageRow key={message.messageId} message={message}
        grouped={isGroupedWithPrevious(messages[index - 1], message)}
        quoted={message.replyToMessageId ? originals.get(message.replyToMessageId) : undefined}
        currentParticipantId={currentParticipantId} onReply={canReply ? onReply : undefined}
        onPrivateChat={onPrivateChat} onReadInPanel={onReadInPanel} onReadMarkdownFile={onReadMarkdownFile} onLocate={onLocate} onPreview={setPreviewId} />)}
      {pendingMessage}
    </div></div></ReadingPane>
    {!atBottom && <Button className="timeline-jump-latest" shape="round" size="small" onClick={jumpToLatest}>
      <span aria-hidden="true">↓</span> 最新消息
      {newMessageCount > 0 && <span className="timeline-new-count">{newMessageCount > 999 ? '999+' : newMessageCount}</span>}
    </Button>}
    {previewId && <ConversationAttachmentViewer selectedId={previewId} messages={messages} currentParticipantId={currentParticipantId} hasEarlierMessages={hasEarlierMessages} onSelect={setPreviewId} />}
  </section>
}

const COLLAPSE_THRESHOLD_PX = 420

const MessageRow = memo(function MessageRow({ message, grouped, quoted, currentParticipantId, onReply, onPrivateChat, onReadInPanel, onReadMarkdownFile, onLocate, onPreview }:
  Pick<Props, 'currentParticipantId' | 'onReply' | 'onPrivateChat' | 'onReadInPanel' | 'onReadMarkdownFile' | 'onLocate'> & {
    message: ChatMessage; grouped: boolean; quoted?: ChatMessage; onPreview: (id: string) => void
  }) {
  const bodyHostRef = useRef<HTMLDivElement>(null)
  const [collapsible, setCollapsible] = useState(false)
  const [expanded, setExpanded] = useState(false)
  useLayoutEffect(() => {
    const host = bodyHostRef.current
    if (!host || !message.content) { setCollapsible(false); return }
    const measure = () => {
      const fullHeight = host.scrollHeight
      setCollapsible(fullHeight > COLLAPSE_THRESHOLD_PX + 24)
    }
    measure()
    if (typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(measure)
    observer.observe(host)
    return () => observer.disconnect()
  }, [message.content])
  const original = message.originalTextAttachmentId
    ? message.attachments?.find(file => file.attachmentId === message.originalTextAttachmentId) : undefined
  const attachments = original ? message.attachments?.filter(file => file !== original) : message.attachments
  const isCollapsed = collapsible && !expanded
  return <article data-message-id={message.messageId} tabIndex={0}
    className={'message-item message-' + message.author.kind
      + (currentParticipantId && message.author.participantId === currentParticipantId ? ' message-own' : '')
      + (grouped ? ' message-grouped' : '')}>
    {grouped
      ? <span className="chat-author-gutter">
          <time className="message-time message-grouped-time" dateTime={message.createdAt.toISOString()} title={message.createdAt.toLocaleString()}>
            {message.createdAt.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })}
          </time>
        </span>
      : <span className="chat-author-avatar" aria-hidden="true"><AvatarImage src={message.author.avatarUrl} colorSlot={message.author.avatarColorSlot} fallback={message.author.kind === 'agent' ? <RobotOutlined /> : message.author.kind === 'system' ? <InfoCircleOutlined /> : Array.from(message.author.displayName.trim())[0]} /></span>}
    <div className="chat-message-column">
      {!grouped && <header className="message-meta">
        <Typography.Text strong className="message-author">{message.author.displayName}</Typography.Text>
        {message.author.kind !== 'human' && <Tag className="message-kind">{message.author.kind === 'agent' ? 'AI Agent' : '系统'}</Tag>}
        {currentParticipantId && message.author.participantId === currentParticipantId && <span className="message-own-mark">我</span>}
        <time className="message-time" dateTime={message.createdAt.toISOString()} title={message.createdAt.toLocaleString()}>{message.createdAt.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })}</time>
        {message.visibility === 'private' && <Tag color="purple">私聊</Tag>}
      </header>}
      <MessageActions message={message} onReply={onReply}
        onPrivateChat={message.author.participantId !== currentParticipantId ? onPrivateChat : undefined}
        onReadInPanel={collapsible ? onReadInPanel : undefined} />
      {message.replyToMessageId && <ReplyQuote key={message.meetingId + message.replyToMessageId} meetingId={message.meetingId} id={message.replyToMessageId} loaded={quoted} onLocate={onLocate} />}
      {message.content && <div className={isCollapsed ? 'message-body-collapsible is-collapsed' : 'message-body-collapsible'}>
        <div ref={bodyHostRef} className="message-body-viewport">
          <MessageBody diagrams content={message.content} mentions={message.mentions} />
        </div>
        {collapsible && <div className="message-collapse-bar">
          <button type="button" className="message-collapse-toggle" onClick={() => setExpanded(open => !open)}>
            {expanded ? <><UpOutlined aria-hidden="true" /><span>收起</span></> : <><DownOutlined aria-hidden="true" /><span>展开全部</span></>}
          </button>
          {onReadInPanel && <button type="button" className="message-collapse-toggle" onClick={() => onReadInPanel(message)}>
            <ReadOutlined aria-hidden="true" /><span>在右侧栏阅读</span>
          </button>}
        </div>}
      </div>}
      {original && <OriginalMessageText key={original.attachmentId} meetingId={message.meetingId} source={original} />}
      {!!attachments?.length && <AttachmentLinks compact meetingId={message.meetingId} attachments={attachments} onOpenViewer={onPreview}
        onOpenMarkdownFile={onReadMarkdownFile ? file => onReadMarkdownFile({ file, author: message.author.displayName, createdAt: message.createdAt }) : undefined} />}
    </div>
  </article>
})

function MessageActions({ message, onReply, onPrivateChat, onReadInPanel }: Pick<Props, 'onReply' | 'onPrivateChat' | 'onReadInPanel'> & { message: ChatMessage }) {
  const authorId = message.author.participantId
  const items = [
    ...(onReply ? [{ key: 'reply', label: '回复', onClick: () => onReply(message) }] : []),
    ...(authorId && onPrivateChat ? [{ key: 'private', label: '私聊', onClick: () => onPrivateChat(authorId) }] : []),
    ...(onReadInPanel ? [{ key: 'read', label: '侧栏阅读', onClick: () => onReadInPanel(message) }] : []),
  ]
  if (!items.length) return null
  return <span className="message-inline-actions" role="group" aria-label={message.author.displayName + '的快捷操作'}>
    {items.map(item => <Button key={item.key} size="small" type="text" onClick={item.onClick}>{item.label}</Button>)}
  </span>
}
