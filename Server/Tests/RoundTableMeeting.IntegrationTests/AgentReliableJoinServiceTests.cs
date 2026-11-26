using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ReliableTakeoverOfLegacyFailedHeadAllowsExplicitRetryWithoutLosingItsCause()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Upgrade failed legacy queue");
        using var http = CreateAnonymousClient(factory);
        await JoinLifecycleAgentAsync(http, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker task");
        await EventuallyAsync(factory, async db => await db.AgentReplyTasks.AnyAsync());
        Guid taskId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            taskId = task.ID;
            task.MarkFailed("context_invalid", DateTimeOffset.UtcNow);
            db.Update(task);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var transport = RecoveryTransport(84);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover"), transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        var status = Assert.IsType<AgentStatusResultDto>((await ReliableForTestAsync(factory.Services, service => service.StatusReliableAsync(new(id), transport))).Data);
        Assert.Equal("context_invalid", status.QueueHead!.FailureCode);
        Assert.True(status.QueueHead.RetryExhausted);
        var retried = Assert.IsType<AgentRetryResultDto>((await ReliableForTestAsync(factory.Services, service => service.RetryReliableAsync(new(id, taskId, Guid.NewGuid()), transport))).Data);
        Assert.Equal(1, retried.RetryCycle);
        Assert.False(retried.RetryExhausted);
    }

    [Fact]
    public async Task ReliableJoinServiceReplaysItsReceiptWithoutAllocatingOrRebinding()
    {
        // Direct Application integration complements the production SDK boundary tests.
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reliable join receipt");
        var request = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var transport = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)51, 32).ToArray());
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, request, transport)).Data);
        Assert.Equal("connected", joined.Connection.State);
        Assert.Equal(1, joined.Connection.BindingVersion);
        Assert.Equal(1, joined.Connection.ConnectionGeneration);
        Assert.NotNull(joined.Connection.LeaseExpiresAt);
        Assert.Equal("confirm_context", joined.NextAction);
        var replay = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, request, transport)).Data);
        Assert.True(replay.Replayed);
        Assert.Equal(joined.InitialDeliveryId, replay.InitialDeliveryId);
        Assert.Equal(joined.Connection.ConnectionSessionId, replay.Connection.ConnectionSessionId);
        Assert.Equal("operation_conflict", (await JoinReliableForTestAsync(factory.Services, request with { ContextCharacterLimit = 6000 }, transport)).ErrorCode);
        var otherTransport = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)52, 32).ToArray());
        var recovery = Assert.IsType<AgentJoinRecoveryRequiredDto>((await JoinReliableForTestAsync(factory.Services, request, otherTransport)).Data);
        Assert.Equal(joined.Connection.ConnectionSessionId, recovery.ConnectionSessionId);
        Assert.False(factory.Services.GetRequiredService<IAgentConnectionRegistry>().ContainsTransport(otherTransport));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentInitialContextDeliveries.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("explicit_ack_v1", (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RequiredReliabilityMode);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public async Task ExplicitReliableTakeoverReusesTheLegacyIdentityAndInvalidatesItsProcessingAttempt(int retryBudget)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Takeover legacy work");
        using var oldAgent = CreateAnonymousClient(factory);
        var old = await JoinLifecycleAgentAsync(oldAgent, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker pending work");
        var work = Data(await CallToolAsync(oldAgent, old.Session.TransportSessionId, 3, "wait_for_mention", new { connectionSessionId = old.ConnectionId }));
        var taskId = work.GetProperty("taskId").GetGuid();
        var attemptId = work.GetProperty("deliveryAttemptId").GetGuid();
        factory.Services.GetRequiredService<AgentCollaborationOptions>().MaximumAutomaticRetries = retryBudget;
        var transport = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)53, 32).ToArray());
        var create = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        Assert.Equal("agent_connection_already_active", (await JoinReliableForTestAsync(factory.Services, create, transport)).ErrorCode);
        var takeover = ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover") with { ContextCharacterLimit = 6000 };
        var result = await JoinReliableForTestAsync(factory.Services, takeover, transport);
        Assert.True(result.Succeeded, result.ErrorCode);
        var joined = Assert.IsType<AgentJoinResultDto>(result.Data);
        Assert.NotEqual(old.ConnectionId, joined.Connection.ConnectionSessionId);
        Assert.Equal(2, joined.Connection.ConnectionGeneration);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var oldConnection = await db.AgentConnectionSessions.SingleAsync(item => item.ID == old.ConnectionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(oldConnection.ParticipantId, joined.Agent.ParticipantId);
        Assert.Equal(AgentConnectionState.Superseded, oldConnection.State);
        Assert.Equal(joined.Connection.ConnectionSessionId, oldConnection.SupersededByConnectionId);
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(taskId, task.ID);
        Assert.Equal(attemptId, task.CurrentAttemptId);
        Assert.Equal(AgentReplyTaskState.Failed, task.State);
        Assert.Equal("connection_taken_over", task.FailureCode);
        Assert.Equal(retryBudget == 0, task.RetryExhausted);
        if (retryBudget > 0) Assert.NotNull(task.NextRetryAtUtc);
        else Assert.Null(task.NextRetryAtUtc);
        Assert.NotNull((await db.AgentTaskDeliveryAttempts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).InvalidatedAtUtc);
        Assert.Equal(1, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeliveredThroughSequence);
        Assert.Equal(2, await db.AgentInitialContextDeliveries.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        var nextTransport = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)54, 32).ToArray());
        var next = await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover"), nextTransport);
        Assert.True(next.Succeeded, next.ErrorCode);
        db.ChangeTracker.Clear();
        var preserved = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(task.FailureCode, preserved.FailureCode);
        Assert.Equal(task.RetryCycle, preserved.RetryCycle);
        Assert.Equal(task.AutomaticRetryCount, preserved.AutomaticRetryCount);
        Assert.Equal(task.RetryExhausted, preserved.RetryExhausted);
        Assert.Equal(task.NextRetryAtUtc, preserved.NextRetryAtUtc);
        Assert.Equal("connection_superseded", (await JoinReliableForTestAsync(factory.Services, takeover, transport)).ErrorCode);
    }

    private static AgentJoinRequest ReliableJoinRequest(string meetingCode, string name, string mode) => new(meetingCode, name, 12000)
    {
        ReliabilityMode = "explicit_ack_v1", JoinMode = mode, OperationId = Guid.NewGuid(),
        RecoveryKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    };

    private static async Task<AgentOperationResult> JoinReliableForTestAsync(IServiceProvider services,
        AgentJoinRequest request, McpTransportSessionKey transport)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>().JoinReliableAsync(request, transport);
    }
}
