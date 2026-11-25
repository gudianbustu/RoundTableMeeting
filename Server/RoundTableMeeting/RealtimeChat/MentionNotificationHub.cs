using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Authentication;

namespace RoundTableMeeting.RealtimeChat;

[Authorize]
public sealed class MentionNotificationHub(HumanAccountPresence presence) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirst(AuthenticationConstants.ClaimType)?.Value, out var userId))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, AccountGroup(userId), Context.ConnectionAborted);
        await presence.ConnectAsync(userId, Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Guid.TryParse(Context.User?.FindFirst(AuthenticationConstants.ClaimType)?.Value, out var userId))
            await presence.DisconnectAsync(userId, Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task SubscribeMeetingActivity()
    {
        if (!Guid.TryParse(Context.User?.FindFirst(AuthenticationConstants.ClaimType)?.Value, out var userId))
            throw new HubException("unauthenticated");
        return Groups.AddToGroupAsync(Context.ConnectionId, ActivityGroup(userId), Context.ConnectionAborted);
    }

    internal static string ActivityGroup(Guid userId) => $"meeting-activity:{userId:N}";
    internal static string AccountGroup(Guid userId) => $"human-mentions:{userId:N}";
}

public sealed record HumanMentionNotification(Guid MeetingId, Guid MessageId);
