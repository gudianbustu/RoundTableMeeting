import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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

it('replies directly from the message header', () => {
  const { rows, reply } = timeline()
  fireEvent.click(within(rows[1]).getByRole('button', { name: '回复' }))
  expect(reply).toHaveBeenCalledWith(messages[1])
  expect(screen.queryByRole('menu')).not.toBeInTheDocument()
})

it('retains native context menus for links and selected text and switches actions between messages', async () => {
  const { rows, reply } = timeline()
  expect(fireEvent.contextMenu(within(rows[0]).getByRole('link'))).toBe(true)
  const selection = window.getSelection()!
  const range = document.createRange()
  range.selectNodeContents(within(rows[0]).getByText('First'))
  selection.addRange(range)
  try { expect(fireEvent.contextMenu(rows[0])).toBe(true) } finally { selection.removeAllRanges() }
  expect(fireEvent.contextMenu(rows[0])).toBe(false)
  await screen.findByRole('menuitem', { name: '回复' })
  fireEvent.contextMenu(rows[1])
  await waitFor(() => expect(within(rows[1]).getByRole('group', { name: /的快捷操作/ })).toHaveClass('ant-dropdown-open'))
  expect(within(rows[0]).getByRole('group', { name: /的快捷操作/ })).not.toHaveClass('ant-dropdown-open')
  const menu = screen.getByRole('menu', { name: 'Second的消息操作' })
  fireEvent.click(within(menu).getByRole('menuitem', { name: '回复' }))
  expect(reply).toHaveBeenCalledWith(messages[1])
})

it('opens via Shift+F10 and preserves native keyboard navigation within the menu', async () => {
  const { rows, reply } = timeline()
  act(() => rows[0].focus())
  fireEvent.keyDown(rows[0], { key: 'F10', shiftKey: true })
  const item = await screen.findByRole('menuitem', { name: '回复' })
  fireEvent.keyDown(item, { key: 'ContextMenu' })
  fireEvent.click(item)
  expect(reply).toHaveBeenCalledWith(messages[0])
})

it('opens after a stationary long press and cancels movement, release, and link presses', async () => {
  const { rows } = timeline()
  function touch(target: Element, type: string, x = 0) {
    const event = new MouseEvent(type, { bubbles: true, clientX: x })
    Object.defineProperty(event, 'pointerType', { value: 'touch' })
    fireEvent(target, event)
  }
  touch(rows[0], 'pointerdown')
  touch(rows[0], 'pointermove', 15)
  touch(rows[1], 'pointerdown')
  touch(rows[1], 'pointerup')
  touch(within(rows[0]).getByRole('link'), 'pointerdown')
  await act(() => new Promise<void>(resolve => setTimeout(resolve, 550)))
  expect(screen.queryByRole('menu')).not.toBeInTheDocument()
  touch(rows[1], 'pointerdown')
  expect(await screen.findByRole('menu', { name: 'Second的消息操作' })).toBeInTheDocument()
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
