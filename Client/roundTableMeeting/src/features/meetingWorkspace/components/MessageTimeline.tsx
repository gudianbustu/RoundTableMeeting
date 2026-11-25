import { ReadingPane } from './ReadingPane'
import './WorkspaceMessages.css'
import { EllipsisOutlined, RollbackOutlined, RobotOutlined, InfoCircleOutlined } from '@ant-design/icons'
import { MessageBody } from './MessageBody'
import type { ReactNode } from 'react'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { Button, Dropdown, Empty, Tag, Typography } from 'antd'
import type { ChatMessage, ChatScrollPosition } from '../types/realtimeChat'
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

type Props = { onWidthChange?: (width: number) => void; onLocate?: (id: string) => Promise<void>; pendingMessage?: ReactNode; messages: ChatMessage[]; hasEarlierMessages: boolean; loading: boolean; onLoadEarlier: () => void; newMessageCount: number; onNewMessageClick: () => void; onReply?: (message: ChatMessage) => void; onPrivateChat?: (id: string) => void; currentParticipantId?: string; focusMessageId?: string; focusRevision?: number; canReply?: boolean; scrollPosition?: ChatScrollPosition; onScrollPositionChanged?: (position: ChatScrollPosition) => void }
export function MessageTimeline({ onWidthChange, onLocate, pendingMessage, messages, hasEarlierMessages, loading, onLoadEarlier, onReply, onPrivateChat, currentParticipantId, focusMessageId, focusRevision, canReply = true, scrollPosition, onScrollPositionChanged }: Props) {
  const [previewId, setPreviewId] = useState<string>()
  const originals = new Map(messages.map(message => [message.messageId, message]))
  const scrollRef = useRef<HTMLDivElement>(null)
  const processedFocus = useRef<string | undefined>(undefined)
  const itemsRef = useRef<HTMLDivElement>(null)
  const position = useRef<ChatScrollPosition>(scrollPosition ?? { atBottom: true })
  const jumpTarget = useRef<number | undefined>(undefined)
  const [atBottom, setAtBottom] = useState(position.current.atBottom)
  const capture = useCallback(() => {
    const element = scrollRef.current; if (!element) return
    if (jumpTarget.current !== undefined) {
      if (element.scrollTop + 2 < Math.min(jumpTarget.current, element.scrollHeight - element.clientHeight)) return
      jumpTarget.current = undefined
      element.scrollTop = element.scrollHeight
    }
    const bottom = element.scrollHeight - element.scrollTop - element.clientHeight <= 2
    const top = element.getBoundingClientRect().top
    const first = Array.from(element.querySelectorAll<HTMLElement>('[data-message-id]')).find(row => row.getBoundingClientRect().bottom > top)
    position.current = bottom ? { atBottom: true } : { atBottom: false, messageId: first?.dataset.messageId, offset: first ? first.getBoundingClientRect().top - top : 0 }
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
  useEffect(() => {
    const element = scrollRef.current; if (!element) return
    element.addEventListener('scroll', capture, { passive: true })
    const observer = typeof ResizeObserver === 'undefined' ? undefined : new ResizeObserver(restore)
    observer?.observe(element); if (itemsRef.current) observer?.observe(itemsRef.current)
    return () => { element.removeEventListener('scroll', capture); observer?.disconnect() }
  }, [capture, restore])
  const [actionMessageId, setActionMessageId] = useState<string>()
  const longPress = useRef<{ timer: number; x: number; y: number } | undefined>(undefined)
  const clearLongPress = () => {
    if (longPress.current) window.clearTimeout(longPress.current.timer)
    longPress.current = undefined
  }
  useEffect(() => clearLongPress, [])
  const openActions = (row: HTMLElement) => {
    const button = row.querySelector<HTMLButtonElement>('.message-menu-trigger')
    if (!button || getComputedStyle(button).display === 'none') return false
    button.focus({ preventScroll: true })
    setActionMessageId(row.dataset.messageId)
    return true
  }
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
    const target = element.scrollHeight - element.clientHeight
    const smooth = !window.matchMedia('(prefers-reduced-motion: reduce)').matches
    jumpTarget.current = smooth ? target : undefined
    element.scrollTo({ top: target, behavior: smooth ? 'smooth' : 'instant' })
    if (!smooth) capture()
  }
  return <section className="message-timeline">
    {hasEarlierMessages && <div className="message-timeline-toolbar">
      <Button size="small" loading={loading} onClick={() => { capture(); onLoadEarlier() }}>加载更早消息</Button>
    </div>}
    <ReadingPane onWidthChange={onWidthChange}><div ref={scrollRef} className="message-timeline-scroll" onWheelCapture={interruptJump} onPointerDownCapture={interruptJump} onKeyDownCapture={interruptJump} role="log" aria-label="会议消息"><div ref={itemsRef} className="message-timeline-items">
      {messages.length === 0 && !pendingMessage && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="还没有消息" />}
      {messages.map(message => {
        const original = message.originalTextAttachmentId
          ? message.attachments?.find(file => file.attachmentId === message.originalTextAttachmentId) : undefined
        const attachments = original ? message.attachments?.filter(file => file !== original) : message.attachments
        return <article key={message.messageId} data-message-id={message.messageId} tabIndex={0}
        onContextMenu={event => {
          clearLongPress()
          if (!(event.target instanceof Element) || !event.currentTarget.contains(event.target)
            || event.target.closest('a,button,input,textarea') || window.getSelection()?.toString()) return
          if (!openActions(event.currentTarget)) return
          event.preventDefault()
        }}
        onKeyDown={event => {
          if (event.target !== event.currentTarget) return
          if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
            if (openActions(event.currentTarget)) event.preventDefault()
          }
        }}
        onPointerDown={event => {
          clearLongPress()
          if (event.pointerType !== 'touch' || (event.target as Element).closest('button,a,input')) return
          const row = event.currentTarget
          longPress.current = { x: event.clientX, y: event.clientY, timer: window.setTimeout(() => {
            longPress.current = undefined
            if (!window.getSelection()?.toString()) openActions(row)
          }, 500) }
        }}
        onPointerMove={event => {
          const start = longPress.current
          if (start && Math.hypot(event.clientX - start.x, event.clientY - start.y) > 8) clearLongPress()
        }}
        onPointerUp={clearLongPress} onPointerCancel={clearLongPress} onPointerLeave={clearLongPress}
        className={'message-item message-' + message.author.kind + (currentParticipantId && message.author.participantId === currentParticipantId ? ' message-own' : '')}>
        <span className="chat-author-avatar" aria-hidden="true">{message.author.kind === 'agent' ? <RobotOutlined /> : message.author.kind === 'system' ? <InfoCircleOutlined /> : Array.from(message.author.displayName.trim())[0]}</span>
        <div className="chat-message-column">
          <header className="message-meta">
            <Typography.Text strong className="message-author">{message.author.displayName}</Typography.Text>
            <Tag className="message-kind">{message.author.kind === 'agent' ? 'AI Agent' : message.author.kind === 'system' ? '系统' : '人类'}</Tag>
            <time className="message-time" dateTime={message.createdAt.toISOString()} title={message.createdAt.toLocaleString()}>{message.createdAt.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false })}</time>
            {message.visibility === 'private' && <Tag color="purple">私聊</Tag>}
            <MessageActions open={actionMessageId === message.messageId} onOpenChange={open =>
              setActionMessageId(current => open ? message.messageId : current === message.messageId ? undefined : current)} message={message} onReply={canReply ? onReply : undefined}
              onPrivateChat={message.author.participantId !== currentParticipantId ? onPrivateChat : undefined} />
          </header>
          {message.replyToMessageId && <ReplyQuote key={message.meetingId + message.replyToMessageId} meetingId={message.meetingId} id={message.replyToMessageId} loaded={originals.get(message.replyToMessageId)} onLocate={onLocate} />}
          {message.content && <MessageBody content={message.content} mentions={message.mentions} />}
          {original && <OriginalMessageText key={original.attachmentId} meetingId={message.meetingId} source={original} />}
          {!!attachments?.length && <AttachmentLinks compact meetingId={message.meetingId} attachments={attachments} onOpenViewer={setPreviewId} />}
        </div>
      </article>})}
      {pendingMessage}
    </div></div></ReadingPane>
    {!atBottom && <Button className="timeline-jump-latest" shape="round" size="small" onClick={jumpToLatest}>回到最新</Button>}
    {previewId && <ConversationAttachmentViewer selectedId={previewId} messages={messages} currentParticipantId={currentParticipantId} hasEarlierMessages={hasEarlierMessages} onSelect={setPreviewId} />}
  </section>
}

function MessageActions({ message, onReply, onPrivateChat, open, onOpenChange }: Pick<Props, 'onReply' | 'onPrivateChat'> & { message: ChatMessage; open: boolean; onOpenChange: (open: boolean) => void }) {
  const authorId = message.author.participantId
  const items = [
    ...(onReply ? [{ key: 'reply', label: '回复', onClick: () => onReply(message) }] : []),
    ...(authorId && onPrivateChat ? [{ key: 'private', label: '私聊', onClick: () => onPrivateChat(authorId) }] : []),
  ]
  if (!items.length) return null
  const replyOnly = items.length === 1 && items[0].key === 'reply'
  return <>
    {replyOnly && <Button type="text" size="small" className="message-quick-reply" aria-label="回复消息" title="回复消息"
      icon={<RollbackOutlined />} onClick={items[0].onClick} />}
    <Dropdown destroyOnHidden open={open} onOpenChange={onOpenChange} classNames={{ root: 'message-action-menu' }} trigger={['click']} autoFocus menu={{ items, 'aria-label': message.author.displayName + '的消息操作' }}>
      <Button size="small" type="text" className={replyOnly ? "message-menu-trigger reply-only-menu" : "message-menu-trigger"} aria-label="消息操作" icon={<EllipsisOutlined />} />
    </Dropdown>
  </>
}
