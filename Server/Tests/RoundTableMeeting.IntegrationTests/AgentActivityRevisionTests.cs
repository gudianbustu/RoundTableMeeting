using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ActivityRevisionChangesOnlyForTheFinalVisiblePayloadOfEachTransaction()
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Activity revisions");
        var transport = RecoveryTransport(91);
        var request = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, request, transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        var initial = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        Assert.Equal(2, initial.Revision);
        Assert.Equal("initial_context_unconfirmed", initial.Reason);
        time.Now = time.Now.AddSeconds(10);
        await ReliableForTestAsync(factory.Services, service => service.RenewReliableAsync(new(id), transport));
        await ReliableForTestAsync(factory.Services, service => service.StatusReliableAsync(new(id), transport));
        Assert.Equal(initial, await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId));
        await ReliableForTestAsync(factory.Services, service => service.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport));
        var confirmed = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        Assert.Equal(initial.Revision + 1, confirmed.Revision);
        Assert.Equal("waiting", confirmed.Reason);
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        var task = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, service => service.WaitReliableAsync(new(id), transport))).Data);
        var delivered = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        Assert.Equal(confirmed.Revision + 1, delivered.Revision);
        Assert.Equal("awaiting_context_confirmation", delivered.Reason);
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second");
        await EventuallyAsync(factory, async db => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(db.AgentReplyTasks) == 2);
        Assert.Equal(delivered, await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId));
        await ReliableForTestAsync(factory.Services, service => service.ConfirmReliableAsync(new(id, "task", TaskId: task.TaskId, DeliveryAttemptId: task.DeliveryAttemptId), transport));
        var processing = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        Assert.Equal(delivered.Revision + 1, processing.Revision);
        Assert.Equal("processing", processing.Reason);
        var failure = new AgentFailureRequest(id, task.TaskId, task.DeliveryAttemptId, "agent_permanent_failure");
        await ReliableForTestAsync(factory.Services, service => service.ReportFailureReliableAsync(failure, transport));
        var failed = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        Assert.Equal(processing.Revision + 1, failed.Revision);
        Assert.True(failed.RetryExhausted);
        Assert.Equal("retry_exhausted", failed.Reason);
        await ReliableForTestAsync(factory.Services, service => service.ReportFailureReliableAsync(failure, transport));
        Assert.Equal(failed, await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId));
        await ReliableForTestAsync(factory.Services, service => service.RetryReliableAsync(new(id, task.TaskId, Guid.NewGuid()), transport));
        var retried = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        Assert.Equal(failed.Revision + 1, retried.Revision);
        Assert.Equal("retry_pending", retried.Reason);
        await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(new(id, request.RecoveryKey, Guid.NewGuid()), RecoveryTransport(92)));
        var resumed = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        Assert.Equal(retried.Revision + 2, resumed.Revision);
        Assert.Equal("resumed", resumed.ConnectionDisposition);
        Assert.Equal(retried.NextRetryAt, resumed.NextRetryAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionSnapshotConvergesExpiredOrMissingBindingOnceAndReturnsThatSameRevision(bool registryMissing)
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Consistent snapshot");
        var transport = RecoveryTransport(93);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "create"), transport)).Data);
        var before = await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId);
        if (registryMissing)
        {
            var registry = factory.Services.GetRequiredService<IAgentConnectionRegistry>();
            Assert.True(registry.TryGetRegistration(transport, out var registration));
            registry.Retire(registration);
        }
        else time.Now = joined.Connection.LeaseExpiresAt!.Value;
        // An event projection never invents a new payload from local clock/registry state.
        Assert.Equal(before, await ReadActivityForTestAsync(factory.Services, meeting.MeetingId, joined.Agent.ParticipantId));
        await using var scope = factory.Services.CreateAsyncScope();
        using var gate = await scope.ServiceProvider.GetRequiredService<IMeetingRealtimeGate>().AcquireSharedAsync(meeting.MeetingId, TestContext.Current.CancellationToken);
        var activities = scope.ServiceProvider.GetRequiredService<IAgentActivityService>();
        var snapshot = Assert.Single(await activities.ListAsync(meeting.MeetingId, TestContext.Current.CancellationToken));
        Assert.Equal(before.Revision + 1, snapshot.Revision);
        Assert.Equal("disconnected", snapshot.State);
        Assert.Equal(registryMissing ? "server_restart" : "connection_lease_expired", snapshot.Reason);
        Assert.Equal(snapshot, await activities.FindAsync(meeting.MeetingId, joined.Agent.ParticipantId, TestContext.Current.CancellationToken));
        time.Now = time.Now.AddSeconds(1);
        Assert.Equal(snapshot, Assert.Single(await activities.ListAsync(meeting.MeetingId, TestContext.Current.CancellationToken)));
    }

    private static async Task<AgentActivityDto> ReadActivityForTestAsync(IServiceProvider services, Guid meetingId, Guid participantId)
    {
        await using var scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IAgentActivityService>().FindAsync(meetingId, participantId))!;
    }
}
