namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentCollaborationCleanupService
{
    Task DeleteForMeetingAsync(Guid meetingId, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default);
}
