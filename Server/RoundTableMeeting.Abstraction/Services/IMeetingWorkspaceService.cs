using RoundTableMeeting.Abstraction.Contracts.Meetings;
namespace RoundTableMeeting.Abstraction.Services;
public interface IMeetingWorkspaceService
{
    Task<MeetingOperationResult<CreateMeetingResultDto>> CreateAsync(CreateMeetingRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeetingListItemDto>> ListAsync(CancellationToken cancellationToken = default, bool history = false);
    Task<MeetingOperationResult<MeetingDetailsDto>> GetAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<MeetingOperationResult<MeetingInvitationDto>> InvitationAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<MeetingOperationResult<JoinPreviewDto>> PreviewJoinAsync(PreviewJoinRequest request, CancellationToken cancellationToken = default);
    Task<MeetingOperationResult<MeetingDetailsDto>> JoinAsync(JoinMeetingRequest request, CancellationToken cancellationToken = default);
    Task<MeetingOperationResult<object>> DeleteAsync(Guid meetingId, string confirmationName, CancellationToken cancellationToken = default, Guid? operationId = null);
    Task<MeetingOperationResult<MeetingMembershipDto>> LeaveAsync(Guid meetingId, LeaveMeetingRequest request, CancellationToken cancellationToken = default);
    Task<MeetingOperationResult<MeetingDeletionStatusDto>> DeletionStatusAsync(Guid meetingId, Guid operationId, CancellationToken cancellationToken = default);
}
