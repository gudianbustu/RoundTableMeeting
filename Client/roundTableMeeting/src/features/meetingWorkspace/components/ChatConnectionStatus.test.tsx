import { act, cleanup, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { ChatConnectionStatus } from './ChatConnectionStatus'

beforeEach(() => { vi.useFakeTimers() })
afterEach(() => { cleanup(); vi.useRealTimers() })

const loading = { connection: 'connecting', history: 'loading', onRetry: () => {} } as const
const connected = { ...loading, connection: 'connected', history: 'caughtUp' } as const
const advance = (milliseconds: number) => act(() => { vi.advanceTimersByTime(milliseconds) })

it('skips the notice when the meeting connects quickly', () => {
  const { rerender } = render(<ChatConnectionStatus {...loading} />)
  advance(299)
  expect(screen.queryByText('正在连接会议')).toBeNull()
  rerender(<ChatConnectionStatus {...connected} />)
  advance(1000)
  expect(screen.queryByText('正在连接会议')).toBeNull()
})

it('starts a new delay for each room and removes the notice on success', () => {
  const { rerender } = render(<ChatConnectionStatus key="first" {...loading} />)
  advance(240)
  rerender(<ChatConnectionStatus key="second" {...loading} />)
  advance(200)
  rerender(<ChatConnectionStatus key="third" {...loading} />)
  advance(299)
  expect(screen.queryByText('正在连接会议')).toBeNull()
  advance(1)
  expect(screen.getByText('正在连接会议')).toBeInTheDocument()
  rerender(<ChatConnectionStatus key="third" {...connected} />)
  expect(screen.queryByText('正在连接会议')).toBeNull()
})

it('shows failures immediately while the connecting notice is pending', () => {
  const { rerender } = render(<ChatConnectionStatus {...loading} />)
  advance(100)
  rerender(<ChatConnectionStatus {...loading} error="消息历史加载失败。" />)
  expect(screen.getByText('消息历史加载失败。')).toBeInTheDocument()
  advance(300)
  expect(screen.queryByText('正在连接会议')).toBeNull()
})
