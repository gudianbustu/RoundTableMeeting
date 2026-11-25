import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import type { ChatMessage } from '../types/realtimeChat'
import { ConversationAttachmentViewer } from './ConversationAttachmentViewer'

afterEach(cleanup)

function message(id: string, from: string, to?: string): ChatMessage {
  return {
    messageId: id, meetingId: 'meeting', sequence: 1, content: '', mentions: [], createdAt: new Date(),
    visibility: to ? 'private' : 'public', author: { participantId: from, displayName: from, kind: 'human' },
    recipient: to ? { participantId: to, displayName: to } : undefined,
    attachments: [{ attachmentId: id, name: id + '.png', mediaType: 'image/png', sizeBytes: 32, state: 'ready' }],
  }
}

it('keeps gallery navigation inside the selected conversation in both directions', () => {
  const messages = [message('public', 'alice'), message('sent', 'alice', 'bob'), message('received', 'bob', 'alice'), message('other', 'alice', 'carol')]
  const view = render(<ConversationAttachmentViewer messages={messages} selectedId="sent" onSelect={vi.fn()} />)
  expect(screen.getByRole('button', { name: '查看 received.png' })).toBeEnabled()
  expect(screen.queryByRole('button', { name: '查看 public.png' })).toBeNull()
  expect(screen.queryByRole('button', { name: '查看 other.png' })).toBeNull()
  view.rerender(<ConversationAttachmentViewer messages={messages} selectedId="public" onSelect={vi.fn()} />)
  expect(screen.getByRole('button', { name: '下一张图片' })).toBeDisabled()
  expect(screen.queryByRole('button', { name: '查看 sent.png' })).toBeNull()
})
