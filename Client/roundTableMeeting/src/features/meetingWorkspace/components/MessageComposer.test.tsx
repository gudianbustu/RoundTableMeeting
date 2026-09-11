import { createRef, useState } from 'react'
import type { MessageComposerHandle } from './MessageComposer'
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

  it('does not submit while the IME is composing', () => {
    const onSend = vi.fn()
    render(<MessageComposer value="中文输入" send="idle" failedSubmission={undefined} participants={[]} onChange={vi.fn()} onSend={onSend} onRetry={vi.fn()} />)
    fireEvent.keyDown(screen.getByPlaceholderText('输入消息'), { key: 'Enter', isComposing: true })
    expect(onSend).not.toHaveBeenCalled()
  })
})
it('appends repeated mentions without losing the draft and focuses the end', () => {
  const ref = createRef<MessageComposerHandle>()
  function Harness() {
    const [value, setValue] = useState('现有草稿')
    return <><button onClick={() => ref.current?.mention('助手一号')}>快捷提及</button><MessageComposer composerRef={ref} value={value} send="idle" participants={[]} onChange={setValue} onSend={vi.fn()} onRetry={vi.fn()} /></>
  }
  render(<Harness />)
  fireEvent.click(screen.getByText('快捷提及'))
  const input = screen.getByPlaceholderText('输入消息') as HTMLTextAreaElement
  expect(input.value).toBe('现有草稿 @助手一号 ')
  expect(input).toHaveFocus()
  expect(input.selectionStart).toBe(input.value.length)
  fireEvent.click(screen.getByText('快捷提及'))
  expect(input.value).toBe('现有草稿 @助手一号 @助手一号 ')
})

it('does not change a draft while sending', () => {
  const ref = createRef<MessageComposerHandle>()
  const onChange = vi.fn()
  render(<MessageComposer composerRef={ref} value="提交中" send="sending" participants={[]} onChange={onChange} onSend={vi.fn()} onRetry={vi.fn()} />)
  ref.current?.mention('Alice')
  expect(onChange).not.toHaveBeenCalled()
})
it('filters mentions and selects with Enter without sending, preserving trailing text', async () => {
  const onSend = vi.fn()
  function Harness() {
    const [value, setValue] = useState('')
    return <MessageComposer value={value} send="idle" participants={[
      { participantId: 'alice', displayName: 'Alice', type: 'human', isCreator: false, isCurrentUser: false },
      { participantId: 'agent', displayName: '助手', type: 'agent', isCreator: false, isCurrentUser: false },
    ]} onChange={setValue} onSend={onSend} onRetry={vi.fn()} />
  }
  render(<Harness />)
  const input = screen.getByPlaceholderText('输入消息') as HTMLTextAreaElement
  fireEvent.focus(input)
  fireEvent.change(input, { target: { value: '前文 @Al 后文', selectionStart: 6, selectionEnd: 6 } })
  fireEvent.keyUp(input, { key: 'l', keyCode: 76, which: 76 })
  expect(await screen.findByText('Alice')).toBeInTheDocument()
  expect(screen.queryByText('助手')).not.toBeInTheDocument()
  fireEvent.keyDown(input, { key: 'Enter', keyCode: 13, which: 13, isComposing: true })
  expect(input.value).toBe('前文 @Al 后文')
  fireEvent.keyDown(input, { key: 'Enter', keyCode: 13, which: 13 })
  expect(input.value).toContain('@Alice ')
  expect(input.value).toContain('后文')
  expect(onSend).not.toHaveBeenCalled()
})
it('selects the highlighted mention with Tab and leaves ordinary Tab navigation intact', async () => {
  const onSend = vi.fn()
  function Harness() {
    const [value, setValue] = useState('')
    return <MessageComposer value={value} send="idle" participants={[
      { participantId: 'alice', displayName: 'Alice', type: 'human', isCreator: false, isCurrentUser: false },
      { participantId: 'bob', displayName: 'Bob', type: 'human', isCreator: false, isCurrentUser: false },
    ]} onChange={setValue} onSend={onSend} onRetry={vi.fn()} />
  }
  render(<Harness />)
  const input = screen.getByPlaceholderText('输入消息') as HTMLTextAreaElement
  input.focus()
  const open = () => {
    fireEvent.change(input, { target: { value: '@', selectionStart: 1, selectionEnd: 1 } })
    fireEvent.keyUp(input, { key: '@', keyCode: 50, which: 50 })
  }
  open()
  expect(await screen.findByText('Alice')).toBeInTheDocument()
  fireEvent.keyDown(input, { key: 'ArrowDown', keyCode: 40, which: 40 })
  expect(fireEvent.keyDown(input, { key: 'Tab', keyCode: 9, which: 9 })).toBe(false)
  expect(input.value).toBe('@Bob ')
  expect(input).toHaveFocus()
  expect(onSend).not.toHaveBeenCalled()
  expect(fireEvent.keyDown(input, { key: 'Tab', keyCode: 9, which: 9 })).toBe(true)
  open()
  fireEvent.keyDown(input, { key: 'Escape', keyCode: 27, which: 27 })
  expect(fireEvent.keyDown(input, { key: 'Tab', keyCode: 9, which: 9 })).toBe(true)
  fireEvent.change(input, { target: { value: '@Nobody', selectionStart: 7, selectionEnd: 7 } })
  fireEvent.keyUp(input, { key: 'y', keyCode: 89, which: 89 })
  expect(fireEvent.keyDown(input, { key: 'Tab', keyCode: 9, which: 9 })).toBe(true)
})
