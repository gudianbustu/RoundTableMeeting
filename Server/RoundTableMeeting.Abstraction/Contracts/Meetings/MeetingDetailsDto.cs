namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record MeetingDetailsDto(Guid MeetingId, string Name, string? Description, DateTimeOffset CreatedAt, ParticipantDto? CurrentParticipant, bool CanDelete, bool CanManageInvitation, IReadOnlyList<ParticipantDto> Participants);
