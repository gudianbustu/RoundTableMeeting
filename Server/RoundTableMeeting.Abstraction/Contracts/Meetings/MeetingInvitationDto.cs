namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record MeetingInvitationDto(Guid MeetingId, string MeetingCode, string? PublicSiteUrl = null);

public sealed record MeetingSiteAddress(string? PublicUrl);
