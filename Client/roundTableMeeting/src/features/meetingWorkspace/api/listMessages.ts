import type { MessagePageDto } from '@/api/generated/models'
import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options, guid } from './helpers'
import type { ChatAttachment, ChatMessage, MessageMention, MessagePage } from '../types/realtimeChat'

export type MessagePageRequest = { scope?: 'all' | 'public' | 'private'; peerParticipantId?: string; beforeSequence?: number; afterSequence?: number; throughSequence?: number; limit?: number }
export class RealtimeChatApiError extends Error { readonly code: string; readonly status?: number; readonly retryAfterSeconds?: number; constructor(code: string, status?: number, retryAfterSeconds?: number) { super(code); this.code = code; this.status = status; this.retryAfterSeconds = retryAfterSeconds } }

export function mapMessage(value: any): ChatMessage {
  const sequence = Number(value?.sequence); const author = value?.author; const kind = author?.kind; const createdAt = new Date(value?.createdAt)
  if (!guid(value?.messageId) || !guid(value?.meetingId) || !Number.isSafeInteger(sequence) || sequence < 1 || !['human', 'agent', 'system'].includes(kind) || typeof author?.displayName !== 'string' || typeof value?.content !== 'string' || Number.isNaN(createdAt.getTime()) || (kind !== 'system' && !guid(author?.participantId)) || (kind === 'system' && author?.participantId != null)) throw new RealtimeChatApiError('invalid_message_response')
  if (!Array.isArray(value.mentions)) throw new RealtimeChatApiError('invalid_message_response')
  const mentions: MessageMention[] = value.mentions.map((mention: any) => { if (!guid(mention?.participantId) || typeof mention?.displayName !== 'string' || !Number.isInteger(mention.startOffset) || !Number.isInteger(mention.length) || mention.startOffset < 0 || mention.length <= 1 || mention.startOffset + mention.length > value.content.length) throw new RealtimeChatApiError('invalid_message_response'); return { participantId: guid(mention.participantId), displayName: mention.displayName, startOffset: mention.startOffset, length: mention.length } })
  const visibility = value.visibility ?? 'public'
  if (!['public', 'private'].includes(visibility) || (visibility === 'private' && (!value.recipient?.participantId || typeof value.recipient.displayName !== 'string'))) throw new RealtimeChatApiError('invalid_message_response')
  return { visibility, recipient: visibility === 'private' ? { participantId: guid(value.recipient.participantId), displayName: value.recipient.displayName } : undefined, attachments: (value.attachments ?? []).map(mapAttachment), replyToMessageId: value.replyToMessageId ? guid(value.replyToMessageId) : undefined, messageId: guid(value.messageId), meetingId: guid(value.meetingId), sequence, author: { participantId: author.participantId == null ? undefined : guid(author.participantId), kind, displayName: author.displayName }, content: value.content, mentions, createdAt }
}

export async function listMessages(meetingId: string, request: MessagePageRequest = {}, signal?: AbortSignal): Promise<MessagePage> {
  try { const value = await getApiClient().api.meetings.byMeetingId(meetingId as never).messages.get({ queryParameters: request, options: signal ? options(signal)?.options : undefined } as never); if (!value) throw new RealtimeChatApiError('invalid_history_response'); const page = value as MessagePageDto; return { visibleThroughSequence: page.visibleThroughSequence == null ? undefined : Number(page.visibleThroughSequence), items: (page.items ?? []).map(mapMessage), hasMoreBefore: Boolean(page.hasMoreBefore), hasMoreAfter: Boolean(page.hasMoreAfter), firstSequence: Number(page.firstSequence ?? 0) || undefined, lastSequence: Number(page.lastSequence ?? 0) || undefined, nextBeforeSequence: Number(page.nextBeforeSequence ?? 0) || undefined, nextAfterSequence: Number(page.nextAfterSequence ?? 0) || undefined } } catch (error) { if (error instanceof RealtimeChatApiError) throw error; throw meetingError(error) }
}

export function mapAttachment(value: any): ChatAttachment {
  const size = Number(value?.sizeBytes)
  if (!value?.attachmentId || typeof value.name !== 'string' || !Number.isSafeInteger(size) || size < 0 || typeof value.mediaType !== 'string') throw new RealtimeChatApiError('invalid_attachment_response')
  return { attachmentId: guid(value.attachmentId), name: value.name, sizeBytes: size, mediaType: value.mediaType, state: value.state ?? 'staged' }
}
