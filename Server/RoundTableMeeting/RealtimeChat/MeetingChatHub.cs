using Microsoft.AspNetCore.Authorization;
using RoundTableMeeting.AgentCollaboration;
using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Authentication;

namespace RoundTableMeeting.RealtimeChat;

[Authorize]
public sealed class MeetingChatHub(IMeetingChatSubscriptionService subscriptions, MeetingPresenceRegistry presence, IMeetingRealtimeGate realtimeGate, TimeProvider timeProvider, ILogger<MeetingChatHub> logger, CurrentMcpSessionService? mcp = null, HumanAccountPresence? accounts = null) : Hub
{
    public async Task<MeetingChatSubscriptionDto> SubscribeMeeting(Guid meetingId)
    {
        try
        {
        var cancellationToken = Context.ConnectionAborted;
        using var sharedLease = await realtimeGate.AcquireSharedAsync(meetingId, cancellationToken);
        if (sharedLease is null) throw new HubException("meeting_not_found");
        var userIdClaim = Context.User?.FindFirst(AuthenticationConstants.ClaimType)?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId)) throw new HubException("meeting_not_found");
        var result = await subscriptions.AuthorizeAsync(meetingId, userId, cancellationToken);
        if (result.Status != MessageOperationStatus.Success || result.Data is null)
        {
            logger.LogWarning("Meeting chat subscription rejected. MeetingId: {MeetingId}, ConnectionId: {ConnectionId}, HubAuthenticated: {HubAuthenticated}, HubUserIdClaimPresent: {HubUserIdClaimPresent}, AuthorizationStatus: {AuthorizationStatus}", meetingId, Context.ConnectionId, Context.User?.Identity?.IsAuthenticated == true, Context.User?.FindFirst(AuthenticationConstants.ClaimType) is not null, result.Status);
            throw new HubException("meeting_not_found");
        }
        var old = presence.Get(Context.ConnectionId);
        if (old is not null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(old.Value.MeetingId));
            presence.UnregisterImmediately(Context.ConnectionId);
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(meetingId), cancellationToken);
        var becameOnline = presence.Register(Context.ConnectionId, meetingId, result.Data.ParticipantId, Context.Abort);
        if (accounts is not null) await accounts.ConnectAsync(userId, Context.ConnectionId);
        var accountMembers = accounts is null ? Array.Empty<Guid>() : await accounts.OnlineParticipantsAsync(meetingId, cancellationToken);
        var online = presence.GetOnline(meetingId).Concat(accountMembers).Concat(mcp is null ? [] : await mcp.GetOnlineAsync(meetingId, cancellationToken)).Distinct().ToArray();
        if (becameOnline)
            await Clients.Group(Group(meetingId)).SendAsync("participantPresenceChanged", new ParticipantPresenceDto(result.Data.ParticipantId, "online", timeProvider.GetUtcNow()), cancellationToken);
        return new MeetingChatSubscriptionDto(online, result.Data.HighWatermarkSequence, []);
        }
        catch (Exception exception) when (exception is not HubException)
        {
            logger.LogError("Meeting chat subscription failed. MeetingId: {MeetingId}, ConnectionId: {ConnectionId}, ExceptionType: {ExceptionType}", meetingId, Context.ConnectionId, exception.GetType().Name);
            throw;
        }
    }

    public async Task UnsubscribeMeeting()
    {
        var current = presence.Get(Context.ConnectionId);
        if (current is null) return;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(current.Value.MeetingId));
        presence.UnregisterImmediately(Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var current = presence.Get(Context.ConnectionId);
        if (current is not null) await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(current.Value.MeetingId));
        presence.UnregisterImmediately(Context.ConnectionId);
        if (accounts is not null && Guid.TryParse(Context.User?.FindFirst(AuthenticationConstants.ClaimType)?.Value, out var userId))
            await accounts.DisconnectAsync(userId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public static string Group(Guid meetingId) => "meeting:" + meetingId.ToString("N");
}
