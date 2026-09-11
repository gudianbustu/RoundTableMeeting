using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Authentication;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.RealtimeChat;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ProductionSubscribeMeetingReturnsHumanPresenceSeparatelyFromOnlineAgentActivities()
    {
        using var factory = new TestApplicationFactory();
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Mixed subscription snapshot");
        await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "create"), RecoveryTransport(221));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var human = await db.MeetingParticipants.SingleAsync(p => p.MeetingId == meeting.MeetingId && p.ParticipantType == ParticipantType.Human);
        var agent = await db.MeetingParticipants.SingleAsync(p => p.MeetingId == meeting.MeetingId && p.ParticipantType == ParticipantType.Agent);
        using var hub = ActivatorUtilities.CreateInstance<MeetingChatHub>(scope.ServiceProvider);
        hub.Context = new SubscriptionCaller(human.UserId!.Value);
        hub.Clients = new SubscriptionClients();
        hub.Groups = new SubscriptionGroups();
        try
        {
            var snapshot = await hub.SubscribeMeeting(meeting.MeetingId);
            Assert.Equal(human.ID, Assert.Single(snapshot.OnlineParticipantIds));
            Assert.DoesNotContain(agent.ID, snapshot.OnlineParticipantIds);
            var activity = Assert.Single(snapshot.AgentActivities);
            Assert.Equal(agent.ID, activity.ParticipantId);
            Assert.Equal("initial_context_unconfirmed", activity.Reason);
            Assert.Equal("blocked", activity.State);
            var repeated = await hub.SubscribeMeeting(meeting.MeetingId);
            Assert.Equal(snapshot.OnlineParticipantIds, repeated.OnlineParticipantIds);
            Assert.Equal(snapshot.AgentActivities, repeated.AgentActivities);
            Assert.Equal(snapshot.HighWatermarkSequence, repeated.HighWatermarkSequence);
        }
        finally { await hub.UnsubscribeMeeting(); }
    }

    private sealed class SubscriptionCaller(Guid userId) : HubCallerContext
    {
        public override string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public override string? UserIdentifier => userId.ToString();
        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity([new Claim(AuthenticationConstants.ClaimType, userId.ToString())], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
    private sealed class SubscriptionGroups : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class SubscriptionClients : IHubCallerClients
    {
        private readonly IClientProxy proxy = new ActivityCaptureProxy();
        public IClientProxy All => proxy;
        public IClientProxy Caller => proxy;
        public IClientProxy Others => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> ids) => proxy;
        public IClientProxy Client(string id) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> ids) => proxy;
        public IClientProxy Group(string name) => proxy;
        public IClientProxy GroupExcept(string name, IReadOnlyList<string> ids) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> names) => proxy;
        public IClientProxy OthersInGroup(string name) => proxy;
        public IClientProxy User(string id) => proxy;
        public IClientProxy Users(IReadOnlyList<string> ids) => proxy;
    }
}
