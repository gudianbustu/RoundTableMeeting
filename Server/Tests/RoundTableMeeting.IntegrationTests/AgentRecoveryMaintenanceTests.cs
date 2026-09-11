using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, "transport_closed")]
    [InlineData(true, "transport_closed")]
    [InlineData(false, "connection_lease_expired")]
    [InlineData(true, "connection_lease_expired")]
    [InlineData(false, "server_restart")]
    [InlineData(true, "server_restart")]
    public async Task RecoveryMaintenanceAtomicallyDisconnectsAndFailsOnlyTheCurrentAttempt(bool confirmed, string reason)
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Recovery maintenance");
        var transport = RecoveryTransport(81);
        var request = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, request, transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        await ReliableForTestAsync(factory.Services, service => service.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport));
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second");
        await EventuallyAsync(factory, async db => await db.AgentReplyTasks.CountAsync(task => task.MeetingId == meeting.MeetingId) == 2);
        var mention = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, service => service.WaitReliableAsync(new(id), transport))).Data);
        if (confirmed) await ReliableForTestAsync(factory.Services, service => service.ConfirmReliableAsync(new(id, "task", TaskId: mention.TaskId, DeliveryAttemptId: mention.DeliveryAttemptId), transport));
        var registry = factory.Services.GetRequiredService<IAgentConnectionRegistry>();
        Assert.True(registry.TryGetRegistration(transport, out var expected));
        if (reason == "server_restart") registry.Retire(expected);
        if (reason == "connection_lease_expired") time.Now = joined.Connection.LeaseExpiresAt!.Value;
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>().ReconcileConnectionAsync(expected,
                reason == "transport_closed" ? AgentTaskFailureCode.TransportClosed : null);
        DateTimeOffset due;
        long revision;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var connection = await db.AgentConnectionSessions.SingleAsync();
            Assert.Equal(AgentConnectionState.Disconnected, connection.State);
            Assert.Equal(reason, connection.DisconnectReason);
            var tasks = await db.AgentReplyTasks.OrderBy(item => item.TriggerSequence).ToArrayAsync();
            Assert.Equal(AgentReplyTaskState.Failed, tasks[0].State);
            Assert.Equal(reason, tasks[0].FailureCode);
            Assert.Equal(0, tasks[0].AutomaticRetryCount);
            due = tasks[0].NextRetryAtUtc!.Value;
            Assert.Equal(AgentReplyTaskState.Queued, tasks[1].State);
            var attempt = await db.AgentTaskDeliveryAttempts.SingleAsync();
            Assert.Equal(reason, attempt.InvalidationReason);
            Assert.NotNull(attempt.InvalidatedAtUtc);
            revision = (await db.AgentContextCursors.SingleAsync()).ActivityRevision;
        }
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>().ReconcileConnectionAsync(expected));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(revision, (await db.AgentContextCursors.SingleAsync()).ActivityRevision);
            Assert.Equal(due, (await db.AgentReplyTasks.SingleAsync(item => item.ID == mention.TaskId)).NextRetryAtUtc);
        }
        var nextTransport = RecoveryTransport(82);
        Assert.True((await ReliableForTestAsync(factory.Services, service => service.ResumeReliableAsync(new(id, request.RecoveryKey, Guid.NewGuid()), nextTransport))).Succeeded);
        // A delayed close from the previous binding cannot disconnect the resumed connection.
        Assert.False(await factory.Services.GetRequiredService<IAgentConnectionCompensator>().DisconnectAsync(expected, time.Now));
        var status = Assert.IsType<AgentStatusResultDto>((await ReliableForTestAsync(factory.Services, service => service.StatusReliableAsync(new(id), nextTransport))).Data);
        Assert.Equal("connected", status.Connection.State);
        Assert.Equal(due, status.QueueHead!.NextRetryAt);
    }

    [Fact]
    public async Task RecoveryScannerConfirmationTimeoutKeepsLeaseAndDoesNotRepeatFailureDecision()
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Scanner confirmation deadline");
        var transport = RecoveryTransport(83);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "create"), transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        await ReliableForTestAsync(factory.Services, service => service.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport));
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        var mention = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, service => service.WaitReliableAsync(new(id), transport))).Data);
        time.Now = mention.ConfirmationDeadline!.Value;
        var registry = factory.Services.GetRequiredService<IAgentConnectionRegistry>();
        Assert.True(registry.TryGetRegistration(transport, out var expected));
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>().ReconcileConnectionAsync(expected);
        var status = Assert.IsType<AgentStatusResultDto>((await ReliableForTestAsync(factory.Services, service => service.StatusReliableAsync(new(id), transport))).Data);
        Assert.Equal("connected", status.Connection.State);
        Assert.Equal("confirmation_timeout", status.QueueHead!.FailureCode);
        Assert.Equal(0, status.QueueHead.AutomaticRetryCount);
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>().ReconcileConnectionAsync(expected));
        var repeated = Assert.IsType<AgentStatusResultDto>((await ReliableForTestAsync(factory.Services, service => service.StatusReliableAsync(new(id), transport))).Data);
        Assert.Equal(status.QueueHead.NextRetryAt, repeated.QueueHead!.NextRetryAt);
        Assert.Equal(status.Connection.LeaseExpiresAt, repeated.Connection.LeaseExpiresAt);
    }
}
