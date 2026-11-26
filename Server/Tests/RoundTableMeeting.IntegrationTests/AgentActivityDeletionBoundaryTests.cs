using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ActivityPublicationIsSuppressedAfterDeletionGateWhileOtherMeetingsAndSnapshotsContinue()
    {
        var gate = new PausedDeletionGate();
        using var test = await ReliableRaceFixture.CreateAsync(meetingGate: gate);
        using var browser = test.Factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var other = await CreateMeetingAsync(browser, "Activity survives unrelated deletion");
        var otherJoin = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(test.Services,
            ReliableJoinRequest(other.MeetingCode, "IndependentWorker", "create"), RecoveryTransport(251))).Data);
        var registration = Assert.Single(test.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(test.MeetingId));
        var activity = await ReadActivityForTestAsync(test.Services, test.MeetingId, registration.ParticipantId);
        var otherActivity = await ReadActivityForTestAsync(test.Services, other.MeetingId, otherJoin.Agent.ParticipantId);
        var hub = new ActivityCaptureHub();
        var notifier = new AgentActivityNotifier(hub, test.Services.GetRequiredService<IServiceScopeFactory>(), gate,
            NullLogger<AgentActivityNotifier>.Instance);
        await notifier.NotifyAsync(activity, notifyPresence: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, hub.Proxy.Messages.Count);
        Assert.Equal("agentActivityChangedV1", hub.Proxy.Messages[0].Method);
        Assert.Equal(activity, Assert.IsType<AgentActivityDto>(Assert.Single(hub.Proxy.Messages[0].Args)));
        hub.Proxy.FailNext = true;
        await notifier.NotifyAsync(activity, notifyPresence: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, hub.Proxy.Messages.Count);
        // A lost push never mutates the persisted version; a fresh subscription can recover it.
        await using (var scope = test.Services.CreateAsyncScope())
        {
            using var lease = await gate.AcquireSharedAsync(test.MeetingId, TestContext.Current.CancellationToken);
            var snapshot = Assert.Single(await scope.ServiceProvider.GetRequiredService<IAgentActivityService>().ListAsync(test.MeetingId, TestContext.Current.CancellationToken));
            Assert.Equal(activity, snapshot);
        }
        var deletion = DeleteLifecycleMeetingAsync(browser, test.MeetingId, "Reliable ordered races");
        try
        {
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await notifier.NotifyAsync(activity, notifyPresence: true, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(2, hub.Proxy.Messages.Count);
            await notifier.NotifyAsync(otherActivity, notifyPresence: true, cancellationToken: TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(4, hub.Proxy.Messages.Count);
            Assert.Equal(otherActivity, Assert.IsType<AgentActivityDto>(Assert.Single(hub.Proxy.Messages[2].Args)));
        }
        finally { gate.Release.TrySetResult(); }
        await deletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await notifier.NotifyAsync(activity, notifyPresence: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, hub.Proxy.Messages.Count);
        await AssertAgentMeetingGoneAsync(test.Factory, test.MeetingId);
    }

    private sealed class ActivityCaptureHub : IHubContext<MeetingChatHub>
    {
        public ActivityCaptureProxy Proxy { get; } = new();
        public IHubClients Clients => new ActivityCaptureClients(Proxy);
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class ActivityCaptureClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Client(string connectionId) => proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;
        public IClientProxy Group(string groupName) => proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;
        public IClientProxy User(string userId) => proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class ActivityCaptureProxy : IClientProxy
    {
        public bool FailNext { get; set; }
        public List<(string Method, object?[] Args)> Messages { get; } = [];
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new IOException("Injected lost activity notification."); }
            Messages.Add((method, args));
            return Task.CompletedTask;
        }
    }
}
