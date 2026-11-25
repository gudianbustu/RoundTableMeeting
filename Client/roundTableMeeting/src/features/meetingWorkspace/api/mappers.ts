import { avatarColorSlot, avatarUrl } from '@/features/avatars/avatar'
import type { MeetingDetailsDto, ParticipantDto } from '@/api/generated/models'
import type { MeetingDetails, Participant } from '../types/meetingWorkspace'
import { guid } from './helpers'

export function mapParticipant(value: Partial<ParticipantDto>): Participant {
  if (!value.participantId || !value.displayName) throw new Error('Invalid participant response')
  const type = String(value.type).toLowerCase() === 'agent' ? 'agent' : 'human'
  return {
    participantId: guid(value.participantId),
    displayName: value.displayName,
    avatarColorSlot: avatarColorSlot(value.avatarColorSlot ?? value.additionalData?.avatarColorSlot),
    avatarUrl: avatarUrl(value.avatarUrl ?? value.additionalData?.avatarUrl),
    type,
    membershipState: value.membershipState === 'left' ? 'left' : 'joined',
    isCurrentUser: Boolean(value.isCurrentUser),
    isCreator: Boolean(value.isCreator),
    joinedAt: value.joinedAt ?? undefined,
  }
}

export function mapMeetingDetails(value: Partial<MeetingDetailsDto>): MeetingDetails {
  if (!value.meetingId || !value.name) throw new Error('Invalid meeting details response')
  return {
    meetingId: guid(value.meetingId),
    name: value.name,
    description: value.description ?? undefined,
    createdAt: value.createdAt ?? undefined,
    currentParticipant: value.currentParticipant ? mapParticipant(value.currentParticipant) : undefined,
    membershipState: value.membershipState === 'left' ? 'left' : 'joined',
    canLeave: Boolean(value.permissions?.canLeave),
    canSend: Boolean(value.permissions?.canSend),
    freeDiscussion: value.freeDiscussion ?? undefined,
    canDelete: Boolean(value.permissions?.canDelete ?? value.canDelete),
    canManageInvitation: Boolean(value.permissions?.canInvite ?? value.canManageInvitation),
    participants: (value.participants ?? []).map(mapParticipant),
  }
}
