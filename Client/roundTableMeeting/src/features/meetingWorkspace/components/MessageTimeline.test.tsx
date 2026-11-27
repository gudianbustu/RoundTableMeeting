import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { MessageTimeline } from './MessageTimeline'
import type { ChatMessage } from '../types/realtimeChat'

afterEach(cleanup)

const messages: ChatMessage[] = ['First', 'Second'].map((displayName, i) => ({
  messageId: String(i), meetingId: 'meeting', sequence: i + 1, visibility: 'public',
  author: { kind: 'human', displayName, participantId: displayName }, content: '[文档](https://example.com/)',
  mentions: [], createdAt: new Date(0),
}))
function timeline() {
  const reply = vi.fn()
  render(<MessageTimeline messages={messages} loading={false} hasEarlierMessages={false} onLoadEarlier={vi.fn()}
    newMessageCount={0} onNewMessageClick={vi.fn()} onReply={reply} />)
  return { rows: screen.getAllByRole('article'), reply }
}

it('replies directly from the message action bar and preserves the native context menu', () => {
  const { rows, reply } = timeline()
  expect(fireEvent.contextMenu(rows[0])).toBe(true)
  expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  fireEvent.click(within(rows[1]).getByRole('button', { name: '回复' }))
  expect(reply).toHaveBeenCalledWith(messages[1])
})

it.each([false, true])('keeps the reader in history until a jump is requested (reduced motion %s)', reduced => {
  const props = { loading: false, hasEarlierMessages: false, onLoadEarlier: vi.fn(), newMessageCount: 0, onNewMessageClick: vi.fn() }
  const view = render(<MessageTimeline {...props} messages={messages} />)
  const log = screen.getByRole('log')
  const scrollTo = vi.fn()
  Object.defineProperties(log, { clientHeight: { value: 200 }, scrollHeight: { value: 1000 }, scrollTo: { value: scrollTo } })
  log.scrollTop = 300
  fireEvent.scroll(log)
  view.rerender(<MessageTimeline {...props} messages={[...messages, { ...messages[0], messageId: 'new', sequence: 3 }]} />)
  expect(log.scrollTop).toBe(300)
  expect(scrollTo).not.toHaveBeenCalled()
  const original = window.matchMedia.bind(window)
  const media = vi.spyOn(window, 'matchMedia').mockImplementation(query => query.includes('prefers-reduced-motion')
    ? { ...original(query), matches: reduced } : original(query))
  try {
    fireEvent.click(screen.getByRole('button', { name: '最新消息' }))
    expect(scrollTo).toHaveBeenCalledWith({ top: 800, behavior: reduced ? 'instant' : 'smooth' })
  } finally { media.mockRestore() }
})


it('keeps a restored bottom lock when a delayed scroll event arrives after layout grows', () => {
  const changed = vi.fn()
  const props = { loading: false, hasEarlierMessages: false, onLoadEarlier: vi.fn(), newMessageCount: 0,
    onNewMessageClick: vi.fn(), onScrollPositionChanged: changed, scrollPosition: { atBottom: true } }
  const view = render(<MessageTimeline {...props} messages={messages} />)
  const log = screen.getByRole('log')
  let height = 1000, top = 0
  Object.defineProperties(log, {
    scrollHeight: { get: () => height }, clientHeight: { value: 200 },
    scrollTop: { get: () => top, set: (value: number) => { top = Math.min(value, height - 200) } },
  })
  view.rerender(<MessageTimeline {...props} messages={[...messages]} />)
  expect(top).toBe(800)
  height = 1100
  fireEvent.scroll(log)
  expect(changed).not.toHaveBeenCalled()
  view.rerender(<MessageTimeline {...props} messages={[...messages]} />)
  expect(top).toBe(900)
  expect(screen.queryByRole('button', { name: '最新消息' })).toBeNull()
})

it('restores the saved history row and offset when a returning conversation reflows', () => {
  const props = { loading: false, hasEarlierMessages: false, onLoadEarlier: vi.fn(), newMessageCount: 0,
    onNewMessageClick: vi.fn(), scrollPosition: { atBottom: false, messageId: '1', offset: -12 } }
  const view = render(<MessageTimeline {...props} messages={messages} />)
  const log = screen.getByRole('log')
  const row = screen.getAllByRole('article')[1]
  let rowTop = 350
  vi.spyOn(row, 'getBoundingClientRect').mockImplementation(() => new DOMRect(0, rowTop - log.scrollTop, 300, 100))
  view.rerender(<MessageTimeline {...props} messages={[...messages]} />)
  expect(log.scrollTop).toBe(362)
  rowTop += 75
  fireEvent.scroll(log)
  view.rerender(<MessageTimeline {...props} messages={[...messages]} />)
  expect(row.getBoundingClientRect().top).toBe(-12)
  expect(screen.getByRole('button', { name: '最新消息' })).toBeInTheDocument()
})

it('groups consecutive messages from the same author within five minutes while keeping actions accessible', () => {
  const reply = vi.fn()
  const groupedMessages: ChatMessage[] = [
    { ...messages[0], messageId: 'g1', sequence: 1, createdAt: new Date('2026-01-01T10:00:00Z'), content: '第一句' },
    { ...messages[0], messageId: 'g2', sequence: 2, createdAt: new Date('2026-01-01T10:02:00Z'), content: '第二句' },
    { ...messages[0], messageId: 'g3', sequence: 3, createdAt: new Date('2026-01-01T10:10:00Z'), content: '间隔较长' },
  ]
  render(<MessageTimeline messages={groupedMessages} loading={false} hasEarlierMessages={false} onLoadEarlier={vi.fn()}
    newMessageCount={0} onNewMessageClick={vi.fn()} onReply={reply} />)
  const rows = screen.getAllByRole('article')
  expect(rows[0]).not.toHaveClass('message-grouped')
  expect(rows[1]).toHaveClass('message-grouped')
  expect(rows[2]).not.toHaveClass('message-grouped')
  fireEvent.click(within(rows[1]).getByRole('button', { name: '回复' }))
  expect(reply).toHaveBeenCalledWith(groupedMessages[1])
})
