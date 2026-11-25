using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Authentication;

namespace RoundTableMeeting.RealtimeChat;

[Authorize]
public sealed class MentionNotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirst(AuthenticationConstants.ClaimType)?.Value, out var userId))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, AccountGroup(userId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    internal static string AccountGroup(Guid userId) => $"human-mentions:{userId:N}";
}

public sealed record HumanMentionNotification(Guid MeetingId, Guid MessageId);
