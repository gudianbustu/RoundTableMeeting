import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import TextAttachmentPreview, { decodePreview, isTextAttachment, textPreviewLimit } from './TextAttachmentPreview'
import ImageAttachmentPreview from './ImageAttachmentPreview'

afterEach(() => { cleanup(); vi.unstubAllGlobals() })
it('decodes UTF-8, BOM UTF-16 and GBK without interpreting markup', () => {
  expect(decodePreview(new TextEncoder().encode('中文 <script>alert(1)</script>')).text).toBe('中文 <script>alert(1)</script>')
  expect(decodePreview(new Uint8Array([0xff, 0xfe, 0x2d, 0x4e, 0x87, 0x65])).text).toBe('中文')
  expect(decodePreview(new Uint8Array([0xfe, 0xff, 0x4e, 0x2d, 0x65, 0x87])).text).toBe('中文')
  expect(decodePreview(new Uint8Array([0xd6, 0xd0, 0xce, 0xc4])).text).toBe('中文')
  expect(() => decodePreview(new Uint8Array([0, 1, 2]))).toThrow('binary_content')
  expect(isTextAttachment('NOTE.TXT')).toBe(true)
  expect(isTextAttachment('active.html')).toBe(false)
  expect(isTextAttachment('active.svg')).toBe(false)
})
it('renders hostile markup as text and handles an empty file', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response('<img src=x onerror=alert(1)>中文')).mockResolvedValueOnce(new Response('')))
  const view = render(<TextAttachmentPreview url="/one" sizeBytes={100} />)
  expect(await screen.findByText('<img src=x onerror=alert(1)>中文')).toBeInTheDocument()
  expect(view.container.querySelector('img')).toBeNull()
  view.rerender(<TextAttachmentPreview url="/empty" sizeBytes={0} />)
  expect(await screen.findByText('（空文件）')).toBeInTheDocument()
})
it('bounds a server response that ignores Range and reports truncation', async () => {
  const fetchMock = vi.fn().mockResolvedValue(new Response('a'.repeat(textPreviewLimit + 500)))
  vi.stubGlobal('fetch', fetchMock)
  render(<TextAttachmentPreview url="/large" sizeBytes={textPreviewLimit + 500} />)
  const content = await screen.findByLabelText('文本内容')
  expect(content.textContent?.length).toBe(textPreviewLimit)
  expect(screen.getByText(/仅预览前 1 MiB/)).toBeInTheDocument()
  expect(fetchMock.mock.calls[0][1].headers.Range).toBe(`bytes=0-${textPreviewLimit}`)
})
it('retries text fetch failures and cancels on close', async () => {
  const fetchMock = vi.fn().mockRejectedValueOnce(new Error('network')).mockResolvedValueOnce(new Response('recovered'))
  vi.stubGlobal('fetch', fetchMock)
  const view = render(<TextAttachmentPreview url="/retry" sizeBytes={9} />)
  fireEvent.click(await screen.findByRole('button', { name: /重\s*试/ }))
  await screen.findByText('recovered')
  view.unmount()
  expect(fetchMock.mock.calls[1][1].signal.aborted).toBe(true)
})
it('shows an image failure instead of a blank preview and retries', () => {
  render(<ImageAttachmentPreview url="/image?mode=preview" name="broken.png" />)
  fireEvent.error(screen.getByAltText('broken.png'))
  expect(screen.getByRole('alert')).toHaveTextContent('图片预览失败')
  fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }))
  const image = screen.getByAltText('broken.png')
  expect(image).toHaveAttribute('src', '/image?mode=preview&previewAttempt=1')
  fireEvent.load(image)
  expect(screen.queryByRole('alert')).toBeNull()
})

it('preserves blob URLs when previewing and retrying local images', () => {
  render(<ImageAttachmentPreview url="blob:local-image" name="local.png" />)
  expect(screen.getByAltText('local.png')).toHaveAttribute('src', 'blob:local-image')
  fireEvent.error(screen.getByAltText('local.png'))
  fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }))
  expect(screen.getByAltText('local.png')).toHaveAttribute('src', 'blob:local-image')
})