import type { MentionCandidate } from '../types/realtimeChat'
import type { Participant } from '../types/meetingWorkspace'
export function getMentionCandidates(value: string, participants: Participant[]): MentionCandidate[] {
  const normalizedContent = value.normalize('NFC').trim()
  const result: MentionCandidate[] = []
  const ordered = [...participants].sort((a, b) => b.displayName.length - a.displayName.length)
  for (let index = 0; index < normalizedContent.length; index++) {
    if (normalizedContent[index] !== '@') continue
    for (const participant of ordered) {
      const name = participant.displayName.normalize('NFC')
      const candidate = normalizedContent.slice(index + 1, index + 1 + name.length)
      const end = index + 1 + name.length
      const boundary = normalizedContent[end]
      if (candidate.toLocaleUpperCase('en-US') !== name.toLocaleUpperCase('en-US') || (boundary && !/[\s\p{P}\p{S}]/u.test(boundary))) continue
      result.push({ participantId: participant.participantId, displayName: participant.displayName, startOffset: index, length: name.length + 1 })
      index = end - 1
      break
    }
  }
  return result
}
export function normalizeCandidates(content: string, candidates: MentionCandidate[]): MentionCandidate[] {
  const normalized = content.normalize('NFC').trim(); return candidates.filter(c => c.startOffset >= 0 && c.length > 1 && c.startOffset + c.length <= normalized.length)
}
