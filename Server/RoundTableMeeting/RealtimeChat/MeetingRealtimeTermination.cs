using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.RealtimeChat;

public sealed class MeetingRealtimeTermination(IHubContext<MeetingChatHub> hub, MeetingPresenceRegistry presence) : IMeetingRealtimeTermination
{
    public async Task NotifyAndTerminateAsync(Guid meetingId, DateTimeOffset deletedAtUtc, CancellationToken cancellationToken = default)
    {
        await hub.Clients.Group(MeetingChatHub.Group(meetingId)).SendAsync("meetingDeleted", new MeetingDeletedDto(meetingId, deletedAtUtc), cancellationToken);
        presence.AbortMeeting(meetingId);
        presence.RemoveMeeting(meetingId);
    }
}
