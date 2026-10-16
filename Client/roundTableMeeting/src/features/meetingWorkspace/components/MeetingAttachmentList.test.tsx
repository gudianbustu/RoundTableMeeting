import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { MeetingAttachmentList } from './AttachmentControls'
import { listAttachments } from '../api/messageResources'

vi.mock('../api/messageResources', async original => ({ ...await original<object>(), listAttachments: vi.fn() }))
vi.mock('./AttachmentCard', async original => ({ ...await original<object>(), default: ({ file }: { file: { name: string } }) => <div>{file.name}</div> }))
afterEach(() => { cleanup(); vi.clearAllMocks() })
const row = (id: string, mediaType: string) => ({ attachment: { attachmentId: id, name: id, mediaType, sizeBytes: 1, state: 'bound' }, messageId: `message-${id}`, sequence: 9, author: 'Alice', createdAt: new Date() })

it('keeps file and image tabs separate while retaining scope, shared pagination and locate', async () => {
  vi.mocked(listAttachments).mockResolvedValueOnce({ items: [row('report.pdf', 'application/pdf')], nextBeforeSequence: 8 })
    .mockResolvedValueOnce({ items: [row('motion.gif', 'image/gif')], nextBeforeSequence: undefined })
  const locate = vi.fn().mockResolvedValue(undefined)
  render(<MeetingAttachmentList meetingId="meeting" scope="private" peerParticipantId="bob" onLocate={locate} />)
  fireEvent.click(screen.getByRole('button', { name: '会议附件' }))
  expect(await screen.findByText('report.pdf')).toBeVisible()
  fireEvent.click(screen.getByRole('tab', { name: '图片' }))
  expect(await screen.findByText('当前已加载内容中暂无图片，可继续加载更早附件。')).toBeVisible()
  expect(screen.queryByText('report.pdf')).toBeNull()
  expect(listAttachments).toHaveBeenCalledTimes(1)
  fireEvent.click(screen.getByRole('button', { name: '加载更早附件' }))
  expect(await screen.findByText('motion.gif')).toBeVisible()
  expect(listAttachments).toHaveBeenLastCalledWith('meeting', { scope: 'private', peerParticipantId: 'bob', limit: 50, beforeSequence: 8 }, expect.any(AbortSignal))
  fireEvent.click(screen.getByRole('tab', { name: '文件' }))
  expect(await screen.findByText('report.pdf')).toBeVisible()
  expect(screen.queryByText('motion.gif')).toBeNull()
  fireEvent.click(screen.getByRole('button', { name: '跳转原消息' }))
  await waitFor(() => expect(locate).toHaveBeenCalledWith('message-report.pdf'))
})
it('shows a definitive empty state only after the available pages are exhausted', async () => {
  vi.mocked(listAttachments).mockResolvedValue({ items: [], nextBeforeSequence: undefined })
  render(<MeetingAttachmentList meetingId="meeting" scope="public" onLocate={vi.fn()} />)
  fireEvent.click(screen.getByRole('button', { name: '会议附件' }))
  expect(await screen.findByText('此交流范围还没有文件')).toBeVisible()
  fireEvent.click(screen.getByRole('tab', { name: '图片' }))
  expect(await screen.findByText('此交流范围还没有图片')).toBeVisible()
  expect(screen.queryByRole('button', { name: '加载更早附件' })).toBeNull()
})
