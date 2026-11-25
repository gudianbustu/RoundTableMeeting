using RoundTableMeeting.Abstraction.Contracts.Meetings;
namespace RoundTableMeeting.Abstraction.Services;

public interface IMeetingMembershipService
{
    Task<MeetingOperationResult<MeetingMembershipDto>> RemoveAsync(Guid meetingId, Guid requesterUserId, Guid participantId, Guid operationId, CancellationToken ct);
    // Caller must authenticate participantId; the use case still checks meeting membership and creator rules.
    Task<MeetingOperationResult<MeetingMembershipDto>> LeaveAsync(Guid meetingId, Guid participantId, Guid operationId, CancellationToken cancellationToken = default);
}
