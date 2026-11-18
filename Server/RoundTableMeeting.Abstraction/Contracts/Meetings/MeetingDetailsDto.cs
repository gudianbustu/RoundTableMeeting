namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record SetDiscussionModeRequest(string Mode);
public sealed record MeetingDetailsDto(Guid MeetingId, string Name, string? Description, DateTimeOffset CreatedAt, ParticipantDto? CurrentParticipant, bool CanDelete, bool CanManageInvitation, IReadOnlyList<ParticipantDto> Participants)
{
    public bool FreeDiscussion { get; init; }
    public string MembershipState { get; init; } = "joined";
    public MeetingPermissionsDto? Permissions { get; init; }
}
