using RoundTableMeeting.Abstraction.Contracts.Meetings;
namespace RoundTableMeeting.Abstraction.Services;

public interface IMeetingLifecycleService
{
    // Trusted adapters call this only after authenticating the stable participant identity.
    Task<string?> IssueProofAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default);
    Task<string?> GetMemberStateAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default);
    Task<MeetingLifecycleStatusDto?> GetStatusAsync(MeetingLifecycleStatusRequest request, CancellationToken cancellationToken = default);
}
