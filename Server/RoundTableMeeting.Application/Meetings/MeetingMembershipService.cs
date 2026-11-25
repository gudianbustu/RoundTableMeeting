using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.Meetings;
using RoundTableMeeting.Abstraction.Enums;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
namespace RoundTableMeeting.Application.Meetings;

public sealed class MeetingMembershipService(IMeetingRepository meetings, IMeetingParticipantRepository participants,
    IMessageVisibilityRepository visibility, IMeetingLifecycleRepository lifecycle, IMeetingTransactionRunner runner,
    IUnitOfWork unitOfWork, IMeetingRealtimeGate gate, IMeetingRealtimeTermination termination, RealtimeChatOptions options)
    : IMeetingMembershipService, IScopedDependency<IMeetingMembershipService>
{
    public Task<MeetingOperationResult<MeetingMembershipDto>> LeaveAsync(Guid meetingId, Guid participantId, Guid operationId, CancellationToken cancellationToken = default)
        => EndMembershipAsync(meetingId, participantId, operationId, null, cancellationToken);

    public Task<MeetingOperationResult<MeetingMembershipDto>> RemoveAsync(Guid meetingId, Guid requesterUserId, Guid participantId, Guid operationId, CancellationToken ct)
        => EndMembershipAsync(meetingId, participantId, operationId, requesterUserId, ct);

    private async Task<MeetingOperationResult<MeetingMembershipDto>> EndMembershipAsync(Guid meetingId, Guid participantId, Guid operationId, Guid? requesterUserId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) return new(MeetingOperationStatus.ValidationFailed,
            Errors: new Dictionary<string, string[]> { ["operationId"] = ["A non-empty operation ID is required."] });
        using var lease = await gate.AcquireExclusiveMutationAsync(meetingId, TimeSpan.FromSeconds(options.GateCloseTimeoutSeconds), cancellationToken);
        if (lease is null) return new(MeetingOperationStatus.DeleteBusy);
        var result = await runner.RunAsync(async token =>
        {
            var meeting = await meetings.FindActiveByIdAsync(meetingId, token);
            var participant = (await participants.ListAsync(meetingId, token)).SingleOrDefault(p => p.ID == participantId);
            if (meeting is null || participant is null) return (new MeetingOperationResult<MeetingMembershipDto>(MeetingOperationStatus.MeetingNotFound), false);
            if (requesterUserId.HasValue && meeting.CreatorUserId != requesterUserId.Value)
            {
                var requester = await participants.FindByUserAsync(meetingId, requesterUserId.Value, token);
                return (new MeetingOperationResult<MeetingMembershipDto>(requester?.MembershipState == MeetingMembershipState.Joined
                    ? MeetingOperationStatus.CreatorRequired : MeetingOperationStatus.MeetingNotFound), false);
            }
            if (participant.UserId == meeting.CreatorUserId) return (new MeetingOperationResult<MeetingMembershipDto>(MeetingOperationStatus.CreatorCannotLeave), false);
            var previous = await lifecycle.FindLeaveAsync(meetingId, participantId, operationId, token);
            if (previous is not null) return (new MeetingOperationResult<MeetingMembershipDto>(MeetingOperationStatus.Success,
                new(participantId, "left", previous.VisibleThroughSequence, previous.MembershipRevision)), participant.MembershipState == MeetingMembershipState.Left);
            var sequence = await visibility.HighWatermarkAsync(meetingId, participantId, token);
            if (requesterUserId.HasValue) participant.Remove(sequence);
            else participant.Leave(sequence);
            unitOfWork.RegisterEdit(participant);
            unitOfWork.RegisterAdd(MeetingLeaveOperation.Create(participant, operationId));
            return (new MeetingOperationResult<MeetingMembershipDto>(MeetingOperationStatus.Success,
                new(participantId, "left", participant.LeftThroughSequence, participant.MembershipRevision)), true);
        }, cancellationToken);
        if (result.Item2) await termination.TerminateParticipantAsync(meetingId, participantId, CancellationToken.None);
        return result.Item1;
    }
}
