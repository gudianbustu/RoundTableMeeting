import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { StrictMode } from 'react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import AttachmentPreview from './AttachmentPreview'
import AttachmentCard, { attachmentPreviewKind } from './AttachmentCard'

const file = { attachmentId: 'media', name: 'sample.mp4', mediaType: 'application/octet-stream', sizeBytes: 100, state: 'local' }
beforeEach(() => {
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {})
  vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {})
})
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

it.each(['mp3', 'wav', 'ogg', 'oga', 'm4a', 'aac', 'flac'])('recognizes .%s audio without trusting arbitrary extensions', extension => {
  expect(attachmentPreviewKind({ ...file, name: `sample.${extension.toUpperCase()}` })).toBe('audio')
  expect(attachmentPreviewKind({ ...file, name: 'sample.html' })).toBeUndefined()
})
it('keeps media cards lightweight and opens a controlled player only in preview', () => {
  const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher)
  const view = render(<AttachmentCard meetingId="meeting" file={file} onPreview={() => {}} />)
  expect(screen.getByText('视频预览')).toBeInTheDocument()
  expect(view.container.querySelector('video')).toBeNull()
  expect(fetcher).not.toHaveBeenCalled()
})
it.each(['audio', 'video'] as const)('uses local Blob for %s, reports failure, reloads and releases on close', kind => {
  const view = render(<StrictMode><AttachmentPreview file={{ ...file, name: kind === 'audio' ? 'sample.wav' : 'sample.webm' }} url="blob:local-media" /></StrictMode>)
  const player = view.container.querySelector(kind)!
  expect(player).toHaveAttribute('src', 'blob:local-media')
  expect(player).toHaveAttribute('controls')
  expect(player).toHaveAttribute('preload', 'metadata')
  expect(player).not.toHaveAttribute('autoplay')
  fireEvent.error(player)
  expect(screen.getByText(/当前浏览器可能不支持此编码/)).toBeVisible()
  fireEvent.click(screen.getByRole('button', { name: '重新加载' }))
  expect(player).not.toHaveAttribute('src')
  expect(HTMLMediaElement.prototype.pause).toHaveBeenCalled()
  const replacement = view.container.querySelector(kind)!
  expect(replacement).not.toBe(player)
  expect(replacement).toHaveAttribute('src', 'blob:local-media')
  view.unmount()
  expect(replacement).not.toHaveAttribute('src')
  expect(HTMLMediaElement.prototype.load).toHaveBeenCalled()
})
