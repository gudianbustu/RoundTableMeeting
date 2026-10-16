import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
const mock = vi.hoisted(() => ({ getDocument: vi.fn() }))
vi.mock('pdfjs-dist', () => ({ getDocument: mock.getDocument, GlobalWorkerOptions: {} }))
import PdfAttachmentPreview from './PdfAttachmentPreview'

afterEach(cleanup)
beforeEach(() => mock.getDocument.mockReset())

function documentTask() {
  const cancel = vi.fn()
  const renderPage = vi.fn(() => ({ promise: Promise.resolve(), cancel }))
  const getPage = vi.fn(async () => ({ getViewport: ({ scale }: { scale: number }) => ({ width: 300 * scale, height: 200 * scale }), render: renderPage }))
  const destroy = vi.fn(async () => {})
  return { getPage, cancel, destroy, task: { promise: Promise.resolve({ numPages: 2, getPage }), destroy } }
}

it('renders pages and releases render and document resources on close', async () => {
  const value = documentTask(); mock.getDocument.mockReturnValue(value.task)
  const view = render(<PdfAttachmentPreview url="/preview" name="sample.pdf" />)
  await screen.findByRole('img', { name: 'sample.pdf，第 1 页' })
  fireEvent.click(screen.getByRole('button', { name: '下一页' }))
  await waitFor(() => expect(value.getPage).toHaveBeenCalledWith(2))
  expect(screen.getByRole('button', { name: '下一页' })).toBeDisabled()
  view.unmount()
  expect(value.destroy).toHaveBeenCalledOnce()
  expect(value.cancel).toHaveBeenCalled()
})

it('shows a retry after loading failure and can recover', async () => {
  const failedDestroy = vi.fn(async () => {})
  mock.getDocument.mockReturnValueOnce({ promise: Promise.reject(new Error('failed')), destroy: failedDestroy }).mockReturnValueOnce(documentTask().task)
  render(<PdfAttachmentPreview url="/preview" name="sample.pdf" />)
  fireEvent.click(await screen.findByRole('button', { name: /重\s*试/ }))
  await screen.findByRole('img', { name: 'sample.pdf，第 1 页' })
  expect(failedDestroy).toHaveBeenCalledOnce()
  expect(screen.queryByRole('alert')).not.toBeInTheDocument()
})

it('does not render a document that finishes after the preview closes', async () => {
  let resolve!: (value: unknown) => void
  const getPage = vi.fn()
  const destroy = vi.fn(async () => {})
  mock.getDocument.mockReturnValue({ promise: new Promise(value => { resolve = value }), destroy })
  const view = render(<PdfAttachmentPreview url="/preview" name="sample.pdf" />)
  view.unmount()
  resolve({ numPages: 1, getPage })
  await Promise.resolve()
  expect(getPage).not.toHaveBeenCalled()
  expect(destroy).toHaveBeenCalledOnce()
})
