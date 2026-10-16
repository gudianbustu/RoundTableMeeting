import { createRef, useState } from 'react'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { AttachmentUpload, type AttachmentUploadHandle } from './AttachmentControls'
import type { ChatAttachment } from '../types/realtimeChat'
const upload = vi.hoisted(() => vi.fn())
vi.mock('../api/messageResources', () => ({ getCommunicationCapabilities: async () => ({ maxFileBytes: 10 }), uploadAttachment: upload }))
afterEach(() => { cleanup(); vi.clearAllMocks() })
it('adds multiple local files without uploads and preserves their File objects and stable request ids', async () => {
  const changed = vi.fn(), ref = createRef<AttachmentUploadHandle>()
  function Harness() {
    const [attachments, setAttachments] = useState<ChatAttachment[]>([])
    return <AttachmentUpload uploadRef={ref} attachments={attachments} onChange={next => { changed(next); setAttachments(next) }} />
  }
  render(<Harness />)
  await waitFor(() => expect(screen.getByLabelText('选择附件')).toBeEnabled())
  const files = [new File(['a'], 'a.png', { type: 'image/png' }), new File(['b'], 'b.txt', { type: 'text/plain' })]
  fireEvent.change(screen.getByLabelText('选择附件'), { target: { files } })
  expect(changed.mock.lastCall?.[0]).toHaveLength(2)
  expect(changed.mock.lastCall?.[0][0]).toMatchObject({ state: 'local', localPreviewFile: files[0] })
  expect(changed.mock.lastCall?.[0][1]).toMatchObject({ mediaType: 'application/octet-stream', localPreviewFile: files[1] })
  expect(changed.mock.lastCall?.[0][0].uploadRequestId).toBe(changed.mock.lastCall?.[0][0].attachmentId)
  act(() => ref.current?.addFiles([new File(['c'], 'c.txt')]))
  expect(changed.mock.lastCall?.[0]).toHaveLength(3)
  expect(upload).not.toHaveBeenCalled()
})
it('enforces size and total count locally and blocks changes while sending', async () => {
  const changed = vi.fn(), ref = createRef<AttachmentUploadHandle>()
  const attachments = Array.from({ length: 19 }, (_, i) => ({ attachmentId: `${i}`, name: `${i}`, sizeBytes: 1, mediaType: 'application/octet-stream', state: 'local' }))
  const { rerender } = render(<AttachmentUpload uploadRef={ref} attachments={attachments} onChange={changed} />)
  await waitFor(() => expect(screen.getByLabelText('选择附件')).toBeEnabled())
  fireEvent.change(screen.getByLabelText('选择附件'), { target: { files: [new File(['12345678901'], 'large.txt'), new File(['a'], 'ok.txt'), new File(['b'], 'extra.txt')] } })
  expect(changed.mock.lastCall?.[0]).toHaveLength(20)
  expect(screen.getByRole('alert')).toHaveTextContent('large.txt 超过附件大小限制')
  expect(screen.getByRole('alert')).toHaveTextContent('最多附带 20 个文件')
  rerender(<AttachmentUpload uploadRef={ref} attachments={attachments} disabled onChange={changed} />)
  act(() => ref.current?.addFiles([new File(['b'], 'blocked.txt')]))
  expect(changed).toHaveBeenCalledOnce()
  expect(upload).not.toHaveBeenCalled()
})