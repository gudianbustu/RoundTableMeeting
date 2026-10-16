using RoundTableMeeting.Domain.Entities;
namespace RoundTableMeeting.Domain.Repositories;

public interface IMeetingLifecycleRepository
{
    Task<MeetingDeletionOperation?> FindDeletionAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<MeetingLeaveOperation?> FindLeaveAsync(Guid meetingId, Guid participantId, Guid operationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeetingLeaveOperation>> ListLeavesAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<bool> HasProofAsync(Guid meetingId, string proofHash, CancellationToken cancellationToken = default);
}
