namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record CreateMeetingResultDto(MeetingDetailsDto Meeting, MeetingInvitationDto Invitation);
