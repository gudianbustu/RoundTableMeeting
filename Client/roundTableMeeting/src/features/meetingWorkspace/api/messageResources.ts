import { getApiClient } from '@/api/client/apiClient'
import { notifyUnauthorized } from '@/api/client/authenticationEvents'
import { mapAttachment, mapMessage, RealtimeChatApiError, type MessagePageRequest } from './listMessages'
import { meetingError, options } from './helpers'
import type { ChatMessage } from '../types/realtimeChat'

export async function getSubmission(meetingId: string, id: string, signal?: AbortSignal): Promise<ChatMessage | undefined> {
  try {
    const value = await getApiClient().api.meetings.byMeetingId(meetingId as never).submissions.bySubmissionId(id as never).get(options(signal))
    if (value?.status === 'saved' && value.message) return mapMessage(value.message)
    if (value?.status === 'not_found') return undefined
    throw new RealtimeChatApiError('invalid_submission_response')
  } catch (error) { if (error instanceof RealtimeChatApiError) throw error; throw meetingError(error) }
}
export async function getMessage(meetingId: string, id: string, signal?: AbortSignal) {
  try {
    const value = await getApiClient().api.meetings.byMeetingId(meetingId as never).messages.byMessageId(id as never).get(options(signal))
    return mapMessage(value)
  } catch (error) { if (error instanceof RealtimeChatApiError) throw error; throw meetingError(error) }
}
export async function getCommunicationCapabilities(signal?: AbortSignal) {
  const value = await getApiClient().api.system.capabilities.get(options(signal))
  const maxFileBytes = Number(value?.maxFileBytes)
  if (!Number.isSafeInteger(maxFileBytes) || maxFileBytes < 1) throw new RealtimeChatApiError('invalid_capabilities')
  return { maxFileBytes }
}
export async function uploadAttachment(meetingId: string, file: File, uploadRequestId: string, signal?: AbortSignal) {
  const body = new FormData(); body.append('uploadRequestId', uploadRequestId); body.append('file', file)
  const response = await fetch(`/api/meetings/${encodeURIComponent(meetingId)}/attachments`, { method: 'POST', credentials: 'same-origin', body, signal })
  if (response.status === 401) notifyUnauthorized()
  const value = await response.json()
  if (!response.ok) throw new RealtimeChatApiError(value.code ?? 'attachment_upload_failed', response.status)
  return mapAttachment(value)
}
export async function listAttachments(meetingId: string, request: MessagePageRequest, signal?: AbortSignal) {
  try {
    const value = await getApiClient().api.meetings.byMeetingId(meetingId as never).attachments.get({ queryParameters: request, options: options(signal)?.options } as never)
    if (!value) throw new RealtimeChatApiError('invalid_attachment_response')
    return { items: (value.items ?? []).map(item => ({ attachment: mapAttachment(item.attachment), messageId: String(item.messageId), sequence: Number(item.sequence), author: item.author?.displayName ?? '', createdAt: new Date(item.createdAt!) })), nextBeforeSequence: value.nextBeforeSequence == null ? undefined : Number(value.nextBeforeSequence) }
  } catch (error) { if (error instanceof RealtimeChatApiError) throw error; throw meetingError(error) }
}
export function attachmentUrl(meetingId: string, attachmentId: string, preview = false) {
  return `/api/meetings/${encodeURIComponent(meetingId)}/attachments/${encodeURIComponent(attachmentId)}/content?mode=${preview ? 'preview' : 'download'}`
}
