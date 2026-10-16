import { cleanup, fireEvent, render as renderComponent, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { ConfigProvider } from 'antd'
import type { ReactElement } from 'react'
import { PendingMessage } from './PendingMessage'

const render = (element: ReactElement) => renderComponent(<ConfigProvider theme={{ token: { motion: false } }}>{element}</ConfigProvider>)
afterEach(cleanup)
const submission = { clientSubmissionId: 'local', content: '这条消息待发送', candidates: [], attachmentIds: ['file'] }
const attachments = [{ attachmentId: 'file', name: '表情.gif', mediaType: 'image/gif', sizeBytes: 10, state: 'uploaded' }]
it('shows the submitted content and attachment names with a spinner while sending', () => {
  render(<PendingMessage submission={submission} status="sending" attachments={attachments} readOnly={false} onRetry={vi.fn()} />)
  expect(screen.getByText(submission.content)).toBeVisible()
  expect(screen.getByText('表情.gif')).toBeVisible()
  expect(screen.getByRole('status', { name: '发送中' })).toBeVisible()
  expect(screen.queryByRole('button')).toBeNull()
  expect(screen.queryByText('服务器已保存')).toBeNull()
})
it.each([
  [undefined, undefined, '消息发送被拒绝，请检查内容后重试。'],
  ['message_send_rate_limited', 12, '发送过于频繁，请在 12 秒后重试。'],
] as const)('opens the error reason from the failed message and retries once: %s', async (errorCode, retryAfterSeconds, reason) => {
  const retry = vi.fn().mockResolvedValue(undefined)
  render(<PendingMessage submission={{ ...submission, errorCode, retryAfterSeconds }} status="failed" attachments={attachments} readOnly={false} onRetry={retry} />)
  expect(screen.queryByText(reason)).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: '发送失败，查看原因或重试' }))
  await waitFor(() => expect(screen.getByText(reason)).toBeVisible())
  fireEvent.click(screen.getByRole('button', { name: '重试发送' }))
  await waitFor(() => expect(retry).toHaveBeenCalledOnce())
})
it.each([['pending_confirmation', false], ['failed', true]] as const)('offers confirmation rather than resend for %s / readOnly %s', async (status, readOnly) => {
  const retry = vi.fn().mockResolvedValue(undefined)
  render(<PendingMessage submission={submission} status={status} attachments={[]} readOnly={readOnly} onRetry={retry} />)
  fireEvent.click(screen.getByRole('button'))
  await waitFor(() => expect(screen.getByRole('button', { name: '核对发送结果' })).toBeVisible())
  expect(screen.queryByRole('button', { name: '重试发送' })).toBeNull()
})
