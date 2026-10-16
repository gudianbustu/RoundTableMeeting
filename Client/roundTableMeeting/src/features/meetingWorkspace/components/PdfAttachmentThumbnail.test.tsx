import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
const mocks = vi.hoisted(() => ({ getDocument: vi.fn() }))
vi.mock('pdfjs-dist', () => ({ getDocument: mocks.getDocument, GlobalWorkerOptions: {} }))
import PdfAttachmentThumbnail from './PdfAttachmentThumbnail'
afterEach(() => { cleanup(); mocks.getDocument.mockReset() })
it('renders only page one, labels the total, and releases the document after painting', async () => {
  const destroy = vi.fn(async () => {})
  const getPage = vi.fn(async () => ({ getViewport: ({ scale }: { scale: number }) => ({ width: 600 * scale, height: 800 * scale }), render: () => ({ promise: Promise.resolve(), cancel: vi.fn() }) }))
  mocks.getDocument.mockReturnValue({ promise: Promise.resolve({ numPages: 4, getPage }), destroy })
  render(<PdfAttachmentThumbnail url="/pdf" name="report.pdf" />)
  await screen.findByText('PDF · 4 页')
  await waitFor(() => expect(destroy).toHaveBeenCalledOnce())
  expect(getPage).toHaveBeenCalledExactlyOnceWith(1)
  expect(screen.getByRole('img')).toHaveStyle({ visibility: 'visible' })
})
it('keeps a fallback for malformed PDFs and still releases loading resources', async () => {
  const destroy = vi.fn(async () => {})
  mocks.getDocument.mockReturnValue({ promise: Promise.reject(new Error('invalid')), destroy })
  render(<PdfAttachmentThumbnail url="/broken" name="broken.pdf" />)
  await screen.findByText('缩略预览暂不可用')
  expect(destroy).toHaveBeenCalledOnce()
})
