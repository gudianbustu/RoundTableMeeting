using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;

namespace RoundTableMeeting.RealtimeChat;

public sealed class HumanAccountPresence(IServiceScopeFactory scopes, IHubContext<MeetingChatHub> hub,
    RealtimeChatOptions options, TimeProvider clock, ILogger<HumanAccountPresence> logger)
{
    private sealed class Session
    {
        public HashSet<string> Connections { get; } = [];
        public CancellationTokenSource? Expiry { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Session> accounts = new();
    private readonly SemaphoreSlim transitions = new(1);

    public bool IsOnline(Guid? userId) => userId.HasValue && accounts.ContainsKey(userId.Value);

    public async Task ConnectAsync(Guid userId, string connectionId)
    {
        await transitions.WaitAsync();
        try
        {
            var first = !accounts.TryGetValue(userId, out var session);
            if (first) accounts[userId] = session = new Session();
            session!.Connections.Add(connectionId);
            session.Expiry?.Cancel();
            session.Expiry = null;
            if (first) await PublishAsync(userId, "online");
        }
        finally { transitions.Release(); }
    }

    public async Task DisconnectAsync(Guid userId, string connectionId)
    {
        await transitions.WaitAsync();
        try
        {
            if (!accounts.TryGetValue(userId, out var session) || !session.Connections.Remove(connectionId)
                || session.Connections.Count != 0) return;
            var expiry = new CancellationTokenSource();
            session.Expiry = expiry;
            _ = ExpireAsync(userId, expiry);
        }
        finally { transitions.Release(); }
    }

    private async Task ExpireAsync(Guid userId, CancellationTokenSource expiry)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.PresenceDisconnectGraceSeconds), clock, expiry.Token);
            await transitions.WaitAsync(expiry.Token);
            try
            {
                if (!accounts.TryGetValue(userId, out var session) || session.Expiry != expiry) return;
                accounts.TryRemove(userId, out _);
                await PublishAsync(userId, "offline");
            }
            finally { transitions.Release(); }
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested) { }
        catch (Exception error) { logger.LogError(error, "Could not publish human offline status."); }
        finally { expiry.Dispose(); }
    }

    private async Task PublishAsync(Guid userId, string state)
    {
        using var scope = scopes.CreateScope();
        var meetings = scope.ServiceProvider.GetRequiredService<IMeetingRepository>();
        foreach (var (meeting, participant) in await meetings.ListForUserAsync(userId))
        {
            if (participant.MembershipState != MeetingMembershipState.Joined) continue;
            await hub.Clients.Group(MeetingChatHub.Group(meeting.ID)).SendAsync("participantPresenceChanged",
                new ParticipantPresenceDto(participant.ID, state, clock.GetUtcNow()));
        }
    }

    public async Task<Guid[]> OnlineParticipantsAsync(Guid meetingId, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var members = await scope.ServiceProvider.GetRequiredService<IMeetingParticipantRepository>().ListAsync(meetingId, token);
        return members.Where(p => p.MembershipState == MeetingMembershipState.Joined && IsOnline(p.UserId)).Select(p => p.ID).ToArray();
    }
}
