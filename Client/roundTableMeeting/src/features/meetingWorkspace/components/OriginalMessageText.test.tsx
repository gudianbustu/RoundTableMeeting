import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { OriginalMessageText } from './OriginalMessageText'
import { copyText } from '@/shared/utils/copyText'

vi.mock('@/shared/utils/copyText', () => ({ copyText: vi.fn().mockResolvedValue(undefined) }))
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.clearAllMocks() })

const original = '\uFEFF圆桌🌱\r\n  原文\n'
const source = { attachmentId: 'full', name: '全文.txt', sizeBytes: new TextEncoder().encode(original).byteLength, mediaType: 'application/octet-stream', state: 'ready' }
it('loads the entire UTF-8 attachment on demand and copies the original characters', async () => {
  const fetcher = vi.fn(() => Promise.resolve(new Response(original)))
  vi.stubGlobal('fetch', fetcher)
  render(<OriginalMessageText meetingId="meeting" source={source} />)
  expect(fetcher).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: '阅读全文' }))
  const dialog = await screen.findByRole('dialog')
  const pre = await within(dialog).findByLabelText('消息原文')
  expect(pre.textContent).toBe(original)
  fireEvent.click(within(dialog).getByRole('button', { name: '复制全文' }))
  expect(copyText).toHaveBeenCalledWith(original)
  expect(fetcher).toHaveBeenCalledTimes(1)
})
it('rejects incomplete responses and retries without publishing partial text to the clipboard', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(new Response('short')).mockResolvedValueOnce(new Response(original)))
  render(<OriginalMessageText meetingId="meeting" source={source} />)
  fireEvent.click(screen.getByRole('button', { name: '复制全文' }))
  await screen.findByText('无法读取完整原文，请重新读取。')
  expect(copyText).not.toHaveBeenCalled()
  fireEvent.click(screen.getByRole('button', { name: '复制全文' }))
  await waitFor(() => expect(copyText).toHaveBeenCalledWith(original))
})
