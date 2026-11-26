using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task OneHundredOfflineScansDoNotSpendRetriesOrMoveTheFailedHead()
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        var registry = test.Services.GetRequiredService<IAgentConnectionRegistry>();
        Assert.True(registry.TryGetRegistration(test.Transport, out var registration));
        await test.Run(async s =>
        {
            Assert.True(await s.ReconcileConnectionAsync(registration, AgentTaskFailureCode.TransportClosed));
            return AgentOperationResult.Success(new { }, "Reconciled");
        });
        async Task<string> ReadFacts()
        {
            await using var scope = test.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var task = await db.AgentReplyTasks.SingleAsync();
            var cursor = await db.AgentContextCursors.SingleAsync();
            Assert.Equal(AgentReplyTaskState.Failed, task.State);
            Assert.Equal(0, task.AutomaticRetryCount);
            Assert.False(task.RetryExhausted);
            Assert.Equal(1, await db.AgentTaskDeliveryAttempts.CountAsync());
            Assert.Equal(AgentConnectionState.Disconnected, (await db.AgentConnectionSessions.SingleAsync()).State);
            return JsonSerializer.Serialize(new { task.ID, task.FailedAtUtc, task.FailureCode, task.NextRetryAtUtc,
                task.RetryCycle, task.CurrentAttemptId, cursor.ActivityRevision, cursor.DeliveredThroughSequence,
                cursor.AllocatedThroughSequence });
        }
        var before = await ReadFacts();
        var scanner = test.Services.GetServices<IHostedService>().OfType<AgentConnectionStartupReconciler>().Single();
        for (var i = 0; i < 100; i++)
        {
            test.Clock.Now = test.Clock.Now.AddMinutes(1);
            await scanner.ReconcileAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        Assert.Equal(before, await ReadFacts());
        var nextTransport = RecoveryTransport(245);
        Assert.True((await test.Run(s => s.ResumeReliableAsync(new(test.ConnectionId, test.RecoveryKey, Guid.NewGuid()), nextTransport))).Succeeded);
        var recovered = Assert.IsType<AgentMentionResultDto>((await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), nextTransport))).Data);
        Assert.Equal(test.Mention.TaskId, recovered.TaskId);
        Assert.Equal(test.Mention.ReplyIdempotencyKey, recovered.ReplyIdempotencyKey);
        Assert.NotEqual(test.Mention.DeliveryAttemptId, recovered.DeliveryAttemptId);
        await using var finalScope = test.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(1, (await final.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AutomaticRetryCount);
        Assert.Equal(2, await final.AgentTaskDeliveryAttempts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReliableBlockedHeadDoesNotChangeLegacyConfirmationOrStopOtherParticipants()
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        using var browser = test.Factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        using var oldHttp = test.Factory.CreateClient(new() { HandleCookies = false });
        var legacy = await JoinLifecycleAgentAsync(oldHttp, test.MeetingCode, "Legacy");
        Assert.True((await test.Run(s => s.ReportFailureReliableAsync(test.Failure, test.Transport))).Succeeded);
        await PostHumanMessageAsync(browser, test.MeetingId, "@Worker blocked successor @Legacy independent task");
        var oldTask = Data(await CallToolAsync(oldHttp, legacy.Session.TransportSessionId, 3, "wait_for_mention",
            new { connectionSessionId = legacy.ConnectionId }));
        var oldTaskId = oldTask.GetProperty("taskId").GetGuid();
        Assert.True((await ReplyLifecycleAsync(oldHttp, legacy, oldTask, 4, "@Alice legacy completed")).Payload.GetProperty("succeeded").GetBoolean());
        Assert.Equal("retry_exhausted", (await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport))).ErrorCode);
        var legacyModeError = await CallToolAsync(oldHttp, legacy.Session.TransportSessionId, 5, "wait_for_mention",
            new { connectionSessionId = legacy.ConnectionId, waitTimeoutSeconds = 1 });
        AssertToolError(legacyModeError, "reliability_mode_required");
        Assert.Equal(JsonValueKind.Null, legacyModeError.Payload.GetProperty("data").ValueKind);
        await PostHumanMessageAsync(browser, test.MeetingId, "Human continues while Worker is blocked");
        await EventuallyAsync(test.Factory, async db => await db.AgentReplyTasks.CountAsync() == 3);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await db.AgentReplyTasks.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentReplyTaskState.Replied, tasks.Single(t => t.ID == oldTaskId).State);
        Assert.Equal(AgentReplyTaskState.Failed, tasks.Single(t => t.ID == test.Mention.TaskId).State);
        Assert.Equal(AgentReplyTaskState.Queued, tasks.Single(t => t.ID != oldTaskId && t.ID != test.Mention.TaskId).State);
        Assert.Equal(4, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        var legacyConnection = await db.AgentConnectionSessions.SingleAsync(c => c.ID == legacy.ConnectionId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("legacy_v1", legacyConnection.ReliabilityMode);
        Assert.Equal(InitialContextState.Confirmed, (await db.AgentInitialContextDeliveries.SingleAsync(i => i.ConnectionSessionId == legacy.ConnectionId, cancellationToken: TestContext.Current.CancellationToken)).State);
    }
}
