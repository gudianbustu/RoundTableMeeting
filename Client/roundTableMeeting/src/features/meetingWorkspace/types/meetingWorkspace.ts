import type { ApiError } from '@/api/errors/ApiError'

export type ParticipantKind = 'human' | 'agent'
export type MeetingListItem = { meetingId: string; name: string; myDisplayName: string; isCreator: boolean; joinedAt?: Date }
export type Participant = { membershipState?: 'joined' | 'left'; participantId: string; displayName: string; type: ParticipantKind; isCurrentUser: boolean; isCreator: boolean; joinedAt?: Date }
export type MeetingDetails = { freeDiscussion?: boolean; meetingId: string; name: string; description?: string; createdAt?: Date; currentParticipant?: Participant; membershipState?: 'joined' | 'left'; canLeave?: boolean; canSend?: boolean; canDelete: boolean; canManageInvitation: boolean; participants: Participant[] }
export type MeetingInvitation = { meetingId: string; meetingCode: string }
export type JoinPreview = { meetingId: string; meetingName: string; description?: string; defaultDisplayName: string; alreadyJoined: boolean; existingDisplayName?: string }
export type CreateMeetingInput = { creationRequestId: string; name: string; description?: string }
export type MeetingWorkspaceApiError = ApiError & { retryAfterSeconds?: number }
