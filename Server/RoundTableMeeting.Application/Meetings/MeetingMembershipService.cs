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
    IMessageRepository messages, IMeetingLifecycleRepository lifecycle, IMeetingTransactionRunner runner,
    IUnitOfWork unitOfWork, IMeetingRealtimeGate gate, IMeetingRealtimeTermination termination, RealtimeChatOptions options)
    : IMeetingMembershipService, IScopedDependency<IMeetingMembershipService>
{
    public async Task<MeetingOperationResult<MeetingMembershipDto>> LeaveAsync(Guid meetingId, Guid participantId, Guid operationId, CancellationToken cancellationToken = default)
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
            if (participant.UserId == meeting.CreatorUserId) return (new MeetingOperationResult<MeetingMembershipDto>(MeetingOperationStatus.CreatorCannotLeave), false);
            var previous = await lifecycle.FindLeaveAsync(meetingId, participantId, operationId, token);
            if (previous is not null) return (new MeetingOperationResult<MeetingMembershipDto>(MeetingOperationStatus.Success,
                new(participantId, "left", previous.VisibleThroughSequence, previous.MembershipRevision)), participant.MembershipState == MeetingMembershipState.Left);
            // All messages are public in batch 1. Batch 2 replaces this with the participant projection watermark.
            var sequence = await messages.GetSequenceSummaryAsync(meetingId, token);
            participant.Leave(sequence.HighWatermarkSequence);
            unitOfWork.RegisterEdit(participant);
            unitOfWork.RegisterAdd(MeetingLeaveOperation.Create(participant, operationId));
            return (new MeetingOperationResult<MeetingMembershipDto>(MeetingOperationStatus.Success,
                new(participantId, "left", participant.LeftThroughSequence, participant.MembershipRevision)), true);
        }, cancellationToken);
        if (result.Item2) await termination.TerminateParticipantAsync(meetingId, participantId, CancellationToken.None);
        return result.Item1;
    }
}
