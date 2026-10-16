using RoundTableMeeting.Abstraction.Contracts.Meetings;
namespace RoundTableMeeting.Abstraction.Services;

public interface IMeetingMembershipService
{
    // Caller must authenticate participantId; the use case still checks meeting membership and creator rules.
    Task<MeetingOperationResult<MeetingMembershipDto>> LeaveAsync(Guid meetingId, Guid participantId, Guid operationId, CancellationToken cancellationToken = default);
}
