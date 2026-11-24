import { createRef, useState } from 'react'
import type { MessageComposerHandle } from './MessageComposer'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MessageComposer } from './MessageComposer'

afterEach(cleanup)

it('accepts pasted images and dropped files but preserves ordinary text paste and blocks locked intake', () => {
  const onFiles = vi.fn()
  const view = (disabled = false) => <MessageComposer value="原文" send="idle" participants={[]} onChange={vi.fn()} onSend={vi.fn()} onFiles={onFiles} disabled={disabled} />
  const { rerender } = render(view())
  const input = screen.getByPlaceholderText('输入消息')
  expect(fireEvent.paste(input, { clipboardData: { files: [], getData: () => '文字' } })).toBe(true)
  const picture = new File(['a'], 'paste.png', { type: 'image/png' })
  expect(fireEvent.paste(input, { clipboardData: { files: [picture] } })).toBe(false)
  expect(onFiles).toHaveBeenLastCalledWith([picture])
  const files = [new File(['b'], 'b.txt'), picture]
  expect(fireEvent.drop(input, { dataTransfer: { types: ['Files'], files } })).toBe(false)
  expect(onFiles).toHaveBeenLastCalledWith(files)
  rerender(view(true))
  fireEvent.paste(input, { clipboardData: { files: [picture] } })
  expect(fireEvent.drop(input, { dataTransfer: { types: ['Files'], files } })).toBe(false)
  expect(onFiles).toHaveBeenCalledTimes(2)
})

describe('Message sending feedback', () => {
  it('does not submit while the IME is composing', () => {
    const onSend = vi.fn()
    render(<MessageComposer value="中文输入" send="idle" participants={[]} onChange={vi.fn()} onSend={onSend} />)
    fireEvent.keyDown(screen.getByPlaceholderText('输入消息'), { key: 'Enter', isComposing: true })
    expect(onSend).not.toHaveBeenCalled()
  })
})
it('appends repeated mentions without losing the draft and focuses the end', () => {
  const ref = createRef<MessageComposerHandle>()
  function Harness() {
    const [value, setValue] = useState('现有草稿')
    return <><button onClick={() => ref.current?.mention('助手一号')}>快捷提及</button><MessageComposer composerRef={ref} value={value} send="idle" participants={[]} onChange={setValue} onSend={vi.fn()} /></>
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
  render(<MessageComposer composerRef={ref} value="提交中" send="sending" participants={[]} onChange={onChange} onSend={vi.fn()} />)
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
    ]} onChange={setValue} onSend={onSend} />
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
    ]} onChange={setValue} onSend={onSend} />
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

it('cancels just the reply on empty Backspace outside IME composition', () => {
  const cancel = vi.fn()
  const change = vi.fn()
  const compose = (value: string) => <MessageComposer value={value} send="idle" participants={[]} onChange={change}
    onSend={vi.fn()} onCancelReply={cancel} hasAttachments attachments={<span>待发文件</span>} />
  const view = render(compose('正文'))
  const input = screen.getByPlaceholderText('输入消息')
  fireEvent.keyDown(input, { key: 'Backspace' })
  view.rerender(compose(''))
  fireEvent.keyDown(input, { key: 'Backspace', isComposing: true })
  expect(cancel).not.toHaveBeenCalled()
  fireEvent.keyDown(input, { key: 'Backspace' })
  expect(cancel).toHaveBeenCalledOnce()
  expect(change).not.toHaveBeenCalled()
  expect(screen.getByText('待发文件')).toBeInTheDocument()
})
