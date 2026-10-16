using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Application.RealtimeChat;

namespace RoundTableMeeting.RealtimeChat;

public sealed class RealtimeMessageBroadcastConsumer(
    IMessageRepository messages,
    IHubContext<MeetingChatHub> hub,
    IMeetingRealtimeGate realtimeGate,
    IMeetingRepository meetings,
    IMeetingParticipantRepository participants,
    MeetingPresenceRegistry presence, IMessageVisibilityRepository visibility, VisibleMessageMapper mapper) : IMessageEventConsumer
{
    public async Task ConsumeAsync(MessageAppendedEnvelope envelope, CancellationToken cancellationToken = default)
    {
        using var sharedLease = await realtimeGate.AcquireSharedAsync(envelope.MeetingId, cancellationToken);
        if (sharedLease is null) return;
        if (await meetings.FindActiveByIdAsync(envelope.MeetingId, cancellationToken) is null) return;
        var message = await messages.FindByIdAsync(envelope.MeetingId, envelope.MessageId, cancellationToken);
        if (message is null) return;
        var eligible = (await participants.ListAsync(envelope.MeetingId, cancellationToken))
            .Where(p => p.MembershipState == Domain.Enums.MeetingMembershipState.Joined).Select(p => p.ID).ToHashSet();
        foreach (var projection in await visibility.RecipientsAsync(envelope.MeetingId, message.ID, cancellationToken))
        {
            if (!eligible.Contains(projection.ParticipantId)) continue;
            var connections = presence.GetConnections(envelope.MeetingId, new HashSet<Guid> { projection.ParticipantId });
            if (connections.Count == 0) continue;
            var dto = await mapper.MapAsync(new(message, projection.Sequence), cancellationToken);
            await hub.Clients.Clients(connections).SendAsync("messageAppended", dto, cancellationToken);
        }
    }
}
