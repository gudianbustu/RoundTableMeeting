namespace RoundTableMeeting.Abstraction.Contracts.Meetings;
public sealed record CreateMeetingRequest(Guid CreationRequestId, string Name, string? Description, string InitialDiscussionMode = "directed");
