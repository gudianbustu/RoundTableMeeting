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
    IMeetingRepository meetings) : IMessageEventConsumer
{
    public async Task ConsumeAsync(MessageAppendedEnvelope envelope, CancellationToken cancellationToken = default)
    {
        using var sharedLease = await realtimeGate.AcquireSharedAsync(envelope.MeetingId, cancellationToken);
        if (sharedLease is null) return;
        if (await meetings.FindActiveByIdAsync(envelope.MeetingId, cancellationToken) is null) return;
        var message = await messages.FindByIdAsync(envelope.MessageId, cancellationToken);
        if (message is null) return;
        var mentions = await messages.ListMentionsAsync(message.ID, cancellationToken);
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
        await hub.Clients.Group(MeetingChatHub.Group(envelope.MeetingId)).SendAsync("messageAppended", dto, cancellationToken);
    }
}
