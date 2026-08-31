namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record MeetingListItemDto(Guid MeetingId, string Name, string MyDisplayName, bool IsCreator, DateTimeOffset JoinedAt);
