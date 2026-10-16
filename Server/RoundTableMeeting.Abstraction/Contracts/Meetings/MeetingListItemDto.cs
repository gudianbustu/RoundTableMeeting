namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record MeetingListItemDto(Guid MeetingId, string Name, string MyDisplayName, bool IsCreator, DateTimeOffset JoinedAt)
{
    public string MembershipState { get; init; } = "joined";
    public MeetingPermissionsDto? Permissions { get; init; }
}
