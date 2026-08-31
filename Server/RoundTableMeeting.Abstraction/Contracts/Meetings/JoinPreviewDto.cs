namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record JoinPreviewDto(Guid MeetingId, string MeetingName, string? Description, string DefaultDisplayName, bool AlreadyJoined, string? ExistingDisplayName);
