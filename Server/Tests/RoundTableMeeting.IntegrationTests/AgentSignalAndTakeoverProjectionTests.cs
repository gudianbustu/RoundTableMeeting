using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ReliableWaitPollsDurableQueueAfterItsTaskWakeSignalIsLost()
    {
        using var original = new TestApplicationFactory();
        var signals = new LostTaskSignals();
        var clock = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentTaskSignalRegistry>(); services.AddSingleton<IAgentTaskSignalRegistry>(signals);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Lost task wake");
        using var http = factory.CreateClient();
        await using var sdk = await CreateSdkAsync(http);
        var joined = (await ProcessCallAsync(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        clock.Now = clock.Now.AddSeconds(1);
        var wait = ProcessCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 3 });
        // The new lease proves the first transaction observed an empty queue and committed.
        await EventuallyAsync(factory, db => db.AgentConnectionSessions.AnyAsync(c => c.ID == id && c.LeaseExpiresAtUtc == clock.Now.AddSeconds(120)));
        Assert.False(wait.IsCompleted);
        var droppedBefore = signals.Dropped;
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker durable work without a signal");
        await EventuallyAsync(factory, db => db.AgentReplyTasks.AnyAsync());
        var result = (await wait.WaitAsync(TimeSpan.FromSeconds(8))).GetProperty("data");
        Assert.Equal("mention", result.GetProperty("kind").GetString());
        Assert.True(signals.Dropped > droppedBefore);
        Assert.True(signals.Timeouts >= 1);
        await ProcessCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "task",
            taskId = result.GetProperty("taskId").GetGuid(), deliveryAttemptId = result.GetProperty("deliveryAttemptId").GetGuid() });
    }

    [Fact]
    public async Task OnlineTakeoverPublishesBothDurableActivityVersionsAndReplayPublishesNeither()
    {
        using var original = new TestApplicationFactory();
        var capture = new ActivityCaptureHub();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<IHubContext<MeetingChatHub>>(); services.AddSingleton<IHubContext<MeetingChatHub>>(capture); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Takeover activity versions");
        var first = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services,
            ReliableJoinRequest(meeting.MeetingCode, "Worker", "create"), RecoveryTransport(201))).Data);
        var before = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, first.Agent.ParticipantId);
        capture.Proxy.Messages.Clear();
        var request = ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover");
        var transport = RecoveryTransport(202);
        var next = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, request, transport)).Data);
        var events = capture.Proxy.Messages.Where(m => m.Method == "agentActivityChangedV1")
            .Select(m => Assert.IsType<AgentActivityDto>(Assert.Single(m.Args))).ToArray();
        Assert.Equal(2, events.Length);
        Assert.Equal(before with { Revision = before.Revision + 1, ChangedAt = events[0].ChangedAt,
            Reason = "activating", ConnectionDisposition = "taken_over" }, events[0]);
        Assert.Equal(events[0] with { Revision = events[0].Revision + 1, ChangedAt = events[1].ChangedAt,
            Reason = "initial_context_unconfirmed" }, events[1]);
        Assert.Equal(events[1], await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, first.Agent.ParticipantId));
        Assert.NotEqual(first.Connection.ConnectionSessionId, next.Connection.ConnectionSessionId);
        var eventCount = capture.Proxy.Messages.Count;
        Assert.True((await JoinReliableForTestAsync(factory.Services, request, transport)).Succeeded);
        Assert.Equal(eventCount, capture.Proxy.Messages.Count);
        Assert.Equal(events[1], await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, first.Agent.ParticipantId));
    }

    private sealed class LostTaskSignals : IAgentTaskSignalRegistry
    {
        private readonly AgentTaskSignalRegistry inner = new(new AgentCollaborationOptions());
        private int dropped;
        private int timeouts;
        public int Dropped => Volatile.Read(ref dropped);
        public int Timeouts => Volatile.Read(ref timeouts);
        public void Signal(Guid meetingId, Guid agentParticipantId) => Interlocked.Increment(ref dropped);
        public void TerminateMeeting(Guid meetingId) => inner.TerminateMeeting(meetingId);
        public async Task<bool> WaitAsync(Guid meetingId, Guid agentParticipantId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var signalled = await inner.WaitAsync(meetingId, agentParticipantId, timeout, cancellationToken);
            if (!signalled) Interlocked.Increment(ref timeouts);
            return signalled;
        }
    }
}
