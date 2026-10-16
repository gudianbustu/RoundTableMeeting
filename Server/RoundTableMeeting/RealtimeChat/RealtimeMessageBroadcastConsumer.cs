using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using TransportAuthorKind = RoundTableMeeting.Abstraction.Contracts.RealtimeChat.MessageAuthorKind;

namespace RoundTableMeeting.RealtimeChat;

public sealed class RealtimeMessageBroadcastConsumer(
    IMessageRepository messages,
    IHubContext<MeetingChatHub> hub,
    IMeetingRealtimeGate realtimeGate,
    IMeetingRepository meetings,
    IMeetingParticipantRepository participants,
    MeetingPresenceRegistry presence) : IMessageEventConsumer
{
    public async Task ConsumeAsync(MessageAppendedEnvelope envelope, CancellationToken cancellationToken = default)
    {
        using var sharedLease = await realtimeGate.AcquireSharedAsync(envelope.MeetingId, cancellationToken);
        if (sharedLease is null) return;
        if (await meetings.FindActiveByIdAsync(envelope.MeetingId, cancellationToken) is null) return;
        var message = await messages.FindByIdAsync(envelope.MeetingId, envelope.MessageId, cancellationToken);
        if (message is null) return;
        var mentions = await messages.ListMentionsAsync(envelope.MeetingId, message.ID, cancellationToken);
        var authorKind = message.AuthorKind switch
        {
            Domain.Enums.MessageAuthorKind.Agent => TransportAuthorKind.Agent,
            Domain.Enums.MessageAuthorKind.System => TransportAuthorKind.System,
            _ => TransportAuthorKind.Human
        };
        var dto = new MessageDto(
            message.ID,
            message.MeetingId,
            message.Sequence,
            new MessageAuthorDto(message.AuthorParticipantId, authorKind, message.AuthorDisplayName),
            message.Content,
            mentions.Select(x => new MessageMentionDto(x.TargetParticipantId, x.TargetDisplayName, x.StartOffset, x.Length)).ToArray(),
            message.CreatedAtUtc);
        var eligible = (await participants.ListAsync(envelope.MeetingId, cancellationToken))
            .Where(p => p.MembershipState == Domain.Enums.MeetingMembershipState.Joined).Select(p => p.ID).ToHashSet();
        var connections = presence.GetConnections(envelope.MeetingId, eligible);
        if (connections.Count > 0) await hub.Clients.Clients(connections).SendAsync("messageAppended", dto, cancellationToken);
    }
}
