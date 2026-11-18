using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.RealtimeChat;

public sealed class MeetingRealtimeTermination(IHubContext<MeetingChatHub> hub, MeetingPresenceRegistry presence, IAgentConnectionRegistry agents) : IMeetingRealtimeTermination
{
    public Task NotifyDeletingAsync(Guid meetingId, CancellationToken cancellationToken = default)
        => hub.Clients.Group(MeetingChatHub.Group(meetingId)).SendAsync("meetingDeleting", new { meetingId }, cancellationToken);

    public async Task TerminateParticipantAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default)
    {
        foreach (var registration in agents.ListRegistrations(meetingId).Where(r => r.ParticipantId == participantId)) agents.Retire(registration);
        await hub.Clients.Group(MeetingChatHub.Group(meetingId)).SendAsync("membershipLeft", new { meetingId, participantId }, cancellationToken);
        var connections = presence.GetConnections(meetingId, new HashSet<Guid> { participantId });
        try
        {
            foreach (var connection in connections)
                await hub.Groups.RemoveFromGroupAsync(connection, MeetingChatHub.Group(meetingId), cancellationToken);
        }
        finally { presence.AbortParticipant(meetingId, participantId); }
    }

    public async Task NotifyAndTerminateAsync(Guid meetingId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default)
    {
        await hub.Clients.Group(MeetingChatHub.Group(meetingId)).SendAsync("meetingDeleted", new MeetingDeletedDto(meetingId, deletedAtUtc), cancellationToken);
        presence.AbortMeeting(meetingId);
        presence.RemoveMeeting(meetingId);
    }
}
