import type { MentionCandidateDto, SubmitMessageRequest } from '@/api/generated/models'
import { getApiClient } from '@/api/client/apiClient'
import { meetingError, options } from './helpers'
import { mapMessage, RealtimeChatApiError } from './listMessages'
import type { ChatMessage, MentionCandidate } from '../types/realtimeChat'

export async function sendMessage(meetingId: string, content: string, clientSubmissionId: string, candidates: MentionCandidate[], signal?: AbortSignal): Promise<{ message: ChatMessage; replayed: boolean }> {
  const body: SubmitMessageRequest = { clientSubmissionId: clientSubmissionId as never, content, mentionCandidates: candidates.map(candidate => ({ participantId: candidate.participantId as never, startOffset: candidate.startOffset, length: candidate.length } as MentionCandidateDto)) }
  try { const value = await getApiClient().api.meetings.byMeetingId(meetingId as never).messages.post(body, options(signal)); if (!value?.message) throw new RealtimeChatApiError('invalid_message_response'); return { replayed: Boolean(value.replayed), message: mapMessage(value.message) } } catch (error) { if (error instanceof RealtimeChatApiError) throw error; const mapped = meetingError(error); throw new RealtimeChatApiError(mapped.code ?? 'message_send_failed', mapped.status, mapped.retryAfterSeconds) }
}
