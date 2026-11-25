using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Enums;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Domain.UnitOfWork;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.Meetings;

public sealed class MeetingNicknameService(IMeetingRepository meetings, IMeetingParticipantRepository members,
    IMeetingTransactionRunner transactions, IUnitOfWork unitOfWork, IHubContext<MeetingChatHub> hub)
{
    public Task<MeetingOperationStatus> RenameHumanAsync(Guid meetingId, Guid userId, string name, CancellationToken ct)
        => RenameAsync(meetingId, member => member.UserId == userId && member.ParticipantType == ParticipantType.Human, name, ct);

    public Task<MeetingOperationStatus> RenameAgentAsync(Guid meetingId, Guid participantId, string name, CancellationToken ct)
        => RenameAsync(meetingId, member => member.ID == participantId && member.ParticipantType == ParticipantType.Agent, name, ct);

    private async Task<MeetingOperationStatus> RenameAsync(Guid meetingId, Func<MeetingParticipant, bool> isSelf, string name, CancellationToken ct)
    {
        MeetingOperationStatus result;
        try
        {
            result = await transactions.RunAsync(async token =>
            {
                if (await meetings.FindActiveByIdAsync(meetingId, token) is null) return MeetingOperationStatus.MeetingNotFound;
                var roster = await members.ListAsync(meetingId, token);
                var self = roster.SingleOrDefault(isSelf);
                if (self?.MembershipState != MeetingMembershipState.Joined) return MeetingOperationStatus.MeetingNotFound;
                self.Rename(name);
                if (roster.Any(other => other.ID != self.ID && other.NormalizedDisplayName == self.NormalizedDisplayName))
                    return MeetingOperationStatus.DisplayNameTaken;
                unitOfWork.RegisterEdit(self);
                return MeetingOperationStatus.Success;
            }, ct);
        }
        catch (ArgumentException) { return MeetingOperationStatus.ValidationFailed; }
        catch (MeetingJoinConflictException) { return MeetingOperationStatus.DisplayNameTaken; }
        if (result == MeetingOperationStatus.Success)
            await hub.Clients.Group(MeetingChatHub.Group(meetingId)).SendAsync("participantRenamed", cancellationToken: ct);
        return result;
    }
}
