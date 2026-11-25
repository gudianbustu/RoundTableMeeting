using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Application.RealtimeChat;

namespace RoundTableMeeting.RealtimeChat;

public sealed class RealtimeMessageBroadcastConsumer(
    IMessageRepository messages,
    IHubContext<MeetingChatHub> hub,
    IHubContext<MentionNotificationHub> notificationHub,
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
            .Where(p => p.MembershipState == Domain.Enums.MeetingMembershipState.Joined).ToDictionary(p => p.ID);
        var recipients = await visibility.RecipientsAsync(envelope.MeetingId, message.ID, cancellationToken);
        var mentions = await messages.ListMentionsAsync(envelope.MeetingId, message.ID, cancellationToken);
        var targets = mentions.Select(m => m.TargetParticipantId).ToHashSet();
        var accountGroups = new HashSet<string>();
        var activityGroups = new HashSet<string>();
        foreach (var projection in recipients)
        {
            if (!eligible.TryGetValue(projection.ParticipantId, out var participant)) continue;
            if (participant.ID != message.AuthorParticipantId && participant.ParticipantType == Domain.Enums.ParticipantType.Human &&
                participant.UserId is Guid userId)
            {
                activityGroups.Add(MentionNotificationHub.ActivityGroup(userId));
                if (targets.Contains(participant.ID)) accountGroups.Add(MentionNotificationHub.AccountGroup(userId));
            }
            var connections = presence.GetConnections(envelope.MeetingId, new HashSet<Guid> { projection.ParticipantId });
            if (connections.Count == 0) continue;
            var dto = await mapper.MapAsync(new(message, projection.Sequence), cancellationToken);
            await hub.Clients.Clients(connections).SendAsync("messageAppended", dto, cancellationToken);
        }
        if (activityGroups.Count > 0)
            await notificationHub.Clients.Groups(activityGroups.ToArray()).SendAsync("meetingActivityChanged", new { message.MeetingId }, cancellationToken);
        if (accountGroups.Count > 0)
            await notificationHub.Clients.Groups(accountGroups.ToArray()).SendAsync("humanMentioned",
                new HumanMentionNotification(message.MeetingId, message.ID), cancellationToken);
    }
}
