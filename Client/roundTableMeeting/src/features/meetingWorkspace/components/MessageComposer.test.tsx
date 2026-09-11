import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MessageComposer } from './MessageComposer'

afterEach(cleanup)

describe('Message sending feedback', () => {
  it.each([
    [undefined, undefined, '消息发送失败，请重试。'],
    ['message_send_rate_limited', 12, '发送过于频繁，请在 12 秒后重试。'],
  ] as const)('shows %s only after failure, and hides it during retry', (errorCode, retryAfterSeconds, message) => {
    const props = {
      value: 'A pending message', participants: [], onChange: vi.fn(), onSend: vi.fn(), onRetry: vi.fn(),
      failedSubmission: { clientSubmissionId: 'submission', content: 'A pending message', candidates: [], errorCode, retryAfterSeconds },
    }
    const { rerender } = render(<MessageComposer {...props} send="sending" />)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '重试发送' })).not.toBeInTheDocument()
    expect(screen.getByPlaceholderText('输入消息')).toBeDisabled()
    rerender(<MessageComposer {...props} send="failed" />)
    expect(screen.getByRole('alert')).toHaveTextContent(message)
    fireEvent.click(screen.getByRole('button', { name: '重试发送' }))
    expect(props.onRetry).toHaveBeenCalledOnce()
    rerender(<MessageComposer {...props} send="sending" />)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: '重试发送' })).not.toBeInTheDocument()
    rerender(<MessageComposer {...props} send="idle" failedSubmission={undefined} value="" />)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    expect(screen.getByPlaceholderText('输入消息')).not.toBeDisabled()
  })
})
