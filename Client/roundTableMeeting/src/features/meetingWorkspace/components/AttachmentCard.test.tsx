import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import AttachmentCard from './AttachmentCard'
import { AttachmentLinks } from './AttachmentControls'

let intersect: IntersectionObserverCallback
const disconnect = vi.fn()
beforeEach(() => {
  vi.stubGlobal('IntersectionObserver', class { constructor(callback: IntersectionObserverCallback) { intersect = callback } observe() {} disconnect = disconnect })
})
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks() })
const file = { attachmentId: 'file', name: 'example.txt', mediaType: 'application/octet-stream', sizeBytes: 50, state: 'bound' }
const show = (visible: boolean) => act(() => intersect([{ isIntersecting: visible } as IntersectionObserverEntry], {} as IntersectionObserver))

it('loads only when near the viewport, clips text, and cancels when leaving', async () => {
  const fetcher = vi.fn().mockResolvedValue(new Response('中文\n<script>unsafe</script>\n3\n4\n5\n6\n7'))
  vi.stubGlobal('fetch', fetcher)
  const view = render(<AttachmentCard meetingId="meeting" file={file} onPreview={() => {}} />)
  expect(fetcher).not.toHaveBeenCalled()
  show(true)
  await waitFor(() => expect(view.container.querySelector('pre')).toHaveTextContent('中文'))
  expect(view.container.querySelector('pre')?.textContent?.split('\n')).toHaveLength(6)
  expect(view.container.querySelector('script')).toBeNull()
  expect(fetcher.mock.calls[0][1].headers.Range).toBe('bytes=0-8191')
  show(false)
  expect(fetcher.mock.calls[0][1].signal.aborted).toBe(true)
})
it('keeps unsupported files download-only and shows the full name in a tooltip', () => {
  const view = render(<AttachmentCard meetingId="meeting" file={{ ...file, name: 'report.zip' }} onPreview={() => {}} />)
  expect(screen.queryByRole('button', { name: /预览/ })).toBeNull()
  expect(screen.getByRole('link', { name: /下载/ })).toHaveAttribute('href', '/api/meetings/meeting/attachments/file/content?mode=download')
  expect(view.container.querySelector('[title="report.zip"]')).toBeInTheDocument()
})
it('renders GIF using a native image, loads on visibility and releases it offscreen', () => {
  const preview = vi.fn()
  const view = render(<AttachmentCard meetingId="meeting" file={{ ...file, name: 'motion.gif', mediaType: 'image/gif' }} onPreview={preview} />)
  expect(view.container.querySelector('img')).toBeNull()
  show(true)
  const image = screen.getByAltText('motion.gif')
  expect(image).toHaveAttribute('src', '/api/meetings/meeting/attachments/file/content?mode=preview')
  fireEvent.load(image)
  expect(image).toBeVisible()
  expect(view.container.querySelector('canvas')).toBeNull()
  expect(view.container.querySelector('.attachment-card')).toBeNull()
  expect(screen.queryByRole('link', { name: /下载/ })).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: '预览 motion.gif' }))
  expect(preview).toHaveBeenCalledOnce()
  show(false)
  expect(view.container.querySelector('img')).toBeNull()
})
it('retains an accessible preview action after image failure and retries on reentry', () => {
  const preview = vi.fn()
  render(<AttachmentCard meetingId="meeting" file={{ ...file, name: 'broken.png', mediaType: 'image/png' }} onPreview={preview} />)
  show(true)
  fireEvent.error(screen.getByAltText('broken.png'))
  expect(screen.getByText('图片加载失败，点击展开后重试')).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: '预览 broken.png' }))
  expect(preview).toHaveBeenCalledOnce()
  show(false); show(true)
  expect(screen.getByText('正在加载图片…')).toBeVisible()
})
it('opens the complete image preview in a modal from the card', async () => {
  render(<AttachmentLinks meetingId="meeting" attachments={[{ ...file, name: 'photo.png', mediaType: 'image/png' }]} />)
  fireEvent.click(screen.getByRole('button', { name: '预览 photo.png' }))
  expect(await screen.findByRole('dialog', { name: 'photo.png' })).toBeInTheDocument()
  expect(screen.getByRole('link', { name: /下载/ })).toHaveAttribute('href', '/api/meetings/meeting/attachments/file/content?mode=download')
  expect(screen.getByAltText('photo.png')).toHaveAttribute('src', '/api/meetings/meeting/attachments/file/content?mode=preview&previewAttempt=0')
})
