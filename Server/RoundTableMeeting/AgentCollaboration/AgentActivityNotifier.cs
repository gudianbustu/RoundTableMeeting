using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.RealtimeChat;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class AgentActivityNotifier(
    IHubContext<MeetingChatHub> hub,
    IServiceScopeFactory scopes,
    IMeetingRealtimeGate gate,
    ILogger<AgentActivityNotifier> logger) : IAgentActivityNotifier
{
    public async Task NotifyAsync(AgentActivityDto activity, bool notifyPresence = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var lease = await gate.AcquireSharedAsync(activity.MeetingId, cancellationToken);
            if (lease is null) return;
            await using var scope = scopes.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<IMeetingRepository>()
                .FindActiveByIdAsync(activity.MeetingId, cancellationToken) is null) return;
            var group = hub.Clients.Group(MeetingChatHub.Group(activity.MeetingId));
            await group.SendAsync("agentActivityChangedV1", activity, cancellationToken);
            if (notifyPresence)
            {
                await group.SendAsync("participantPresenceChanged",
                    new ParticipantPresenceDto(activity.ParticipantId,
                        activity.State == "disconnected" || activity.Reason == "activating" ? "offline" : "online",
                        activity.ChangedAt),
                    cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Failed to publish Agent activity. MeetingId: {MeetingId}, ParticipantId: {ParticipantId}, State: {State}, ExceptionType: {ExceptionType}",
                activity.MeetingId, activity.ParticipantId, activity.State, exception.GetType().Name);
        }
    }
}
