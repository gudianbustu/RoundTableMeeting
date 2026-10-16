import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { DraftAttachments } from './DraftAttachments'
import { MessageComposer } from './MessageComposer'
import type { ChatAttachment } from '../types/realtimeChat'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })
const file: ChatAttachment = { attachmentId: 'file', name: '长文件名.txt', sizeBytes: 1200, mediaType: 'application/octet-stream', state: 'ready' }

it('previews local text through a blob without server access and revokes it on removal', async () => {
  const changed = vi.fn(), revoke = vi.fn()
  vi.stubGlobal('URL', { createObjectURL: () => 'blob:local-preview', revokeObjectURL: revoke })
  const fetcher = vi.fn(async (_url: string) => new Response('local preview'))
  vi.stubGlobal('fetch', fetcher)
  const { rerender } = render(<DraftAttachments attachments={[{ ...file, localPreviewFile: new File(['local preview'], file.name) }]} disabled onChange={changed} />)
  fireEvent.click(screen.getByRole('button', { name: `预览附件 ${file.name}` }))
  await screen.findByText('local preview')
  expect(fetcher.mock.calls[0]?.[0]).toBe('blob:local-preview')
  expect(changed).not.toHaveBeenCalled()
  expect(screen.getByRole('button', { name: `移除附件 ${file.name}` })).toBeDisabled()
  rerender(<DraftAttachments attachments={[]} onChange={changed} />)
  await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  expect(revoke).toHaveBeenCalledWith('blob:local-preview')
})

it('uses the shared unsupported-type decision without fetching active file content', async () => {
  const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher)
  vi.stubGlobal('URL', { createObjectURL: () => 'blob:html', revokeObjectURL: vi.fn() })
  render(<DraftAttachments attachments={[{ ...file, name: 'active.html', localPreviewFile: new File(['html'], 'active.html') }]} onChange={vi.fn()} />)
  fireEvent.click(screen.getByRole('button', { name: '预览附件 active.html' }))
  expect(await screen.findByText('此文件类型暂不支持预览，可继续作为附件发送。')).toBeInTheDocument()
  expect(fetcher).not.toHaveBeenCalled()
})
it('keeps attachment removal separate from the text draft and locks it during sending', () => {
  const onChange = vi.fn(), onDraft = vi.fn()
  const view = (disabled = false) => <MessageComposer value="保留正文" send={disabled ? 'sending' : 'idle'} participants={[]} onChange={onDraft} onSend={vi.fn()} attachments={<DraftAttachments attachments={[file]} disabled={disabled} onChange={onChange} />} />
  const { rerender } = render(view())
  expect(screen.getByRole('list', { name: '待发送附件' }).compareDocumentPosition(screen.getByPlaceholderText('输入消息')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  fireEvent.click(screen.getByRole('button', { name: '移除附件 长文件名.txt' }))
  expect(onChange).toHaveBeenCalledWith([])
  expect(onDraft).not.toHaveBeenCalled()
  expect(screen.getByPlaceholderText('输入消息')).toHaveValue('保留正文')
  rerender(view(true))
  expect(screen.getByRole('button', { name: '移除附件 长文件名.txt' })).toBeDisabled()
})

it('creates local image previews and revokes them on scope unmount, then recreates them on return', () => {
  const create = vi.fn().mockReturnValueOnce('blob:first').mockReturnValueOnce('blob:second'), revoke = vi.fn()
  vi.stubGlobal('URL', { createObjectURL: create, revokeObjectURL: revoke })
  const picture: ChatAttachment = { ...file, name: '图片.png', mediaType: 'image/png', localPreviewFile: new File(['pixels'], '图片.png', { type: 'image/png' }) }
  const first = render(<DraftAttachments attachments={[picture]} onChange={vi.fn()} />)
  expect(screen.getByAltText('图片.png')).toHaveAttribute('src', 'blob:first')
  fireEvent.error(screen.getByAltText('图片.png'))
  expect(screen.getByText('图片预览不可用')).toBeVisible()
  expect(screen.getByRole('button', { name: '移除附件 图片.png' })).toBeEnabled()
  first.unmount()
  expect(revoke).toHaveBeenCalledWith('blob:first')
  const second = render(<DraftAttachments attachments={[picture]} onChange={vi.fn()} />)
  expect(screen.getByAltText('图片.png')).toHaveAttribute('src', 'blob:second')
  second.unmount()
  expect(revoke).toHaveBeenCalledWith('blob:second')
})
