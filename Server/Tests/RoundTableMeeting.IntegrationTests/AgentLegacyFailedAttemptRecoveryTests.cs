using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("Prepared")]
    [InlineData("Delivered")]
    [InlineData("Abandoned")]
    public async Task LegacyFailedHeadWithHistoricalAttemptCanActuallyDeliverAfterExplicitRetry(string legacyAttemptState)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Legacy failed attempt recovery");
        using var oldHttp = CreateAnonymousClient(factory);
        var old = await JoinLifecycleAgentAsync(oldHttp, meeting.MeetingCode, "Worker");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker Original failed task");
        var delivered = Data(await CallToolAsync(oldHttp, old.Session.TransportSessionId, 3, "wait_for_mention", new { connectionSessionId = old.ConnectionId }));
        var taskId = delivered.GetProperty("taskId").GetGuid();
        var attemptId = delivered.GetProperty("deliveryAttemptId").GetGuid();
        DateTimeOffset failedAt;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            failedAt = DateTimeOffset.UtcNow;
            task.MarkFailed("context_invalid", failedAt);
            db.Update(task);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            // Reproduce the old-format attempts admitted by the transactional migration:
            // no explicit acknowledgment, invalidation timestamp or replacement linkage.
            if (legacyAttemptState == "Prepared")
                await db.Database.ExecuteSqlRawAsync("UPDATE AgentTaskDeliveryAttempts SET State = 'Prepared', DeliveredFirstSequence = NULL, DeliveredLastSequence = NULL, DeliveredAtUtc = NULL", cancellationToken: TestContext.Current.CancellationToken);
            else if (legacyAttemptState == "Abandoned")
                await db.Database.ExecuteSqlRawAsync("UPDATE AgentTaskDeliveryAttempts SET State = 'Abandoned'", cancellationToken: TestContext.Current.CancellationToken);
        }
        var transport = RecoveryTransport(231);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services,
            ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover"), transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        Assert.True((await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport))).Succeeded);
        Assert.Equal("retry_exhausted", (await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).ErrorCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var task = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("context_invalid", task.FailureCode);
            Assert.Equal(failedAt, task.FailedAtUtc);
            Assert.Equal(0, task.RetryCycle);
            Assert.True(task.RetryExhausted);
        }
        Assert.True((await ReliableForTestAsync(factory.Services, s => s.RetryReliableAsync(new(id, taskId, Guid.NewGuid()), transport))).Succeeded);
        var next = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        Assert.Equal(taskId, next.TaskId);
        Assert.NotEqual(attemptId, next.DeliveryAttemptId);
        Assert.Equal(delivered.GetProperty("replyIdempotencyKey").GetGuid(), next.ReplyIdempotencyKey);
        Assert.True((await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "task", TaskId: taskId, DeliveryAttemptId: next.DeliveryAttemptId), transport))).Succeeded);
        Assert.True((await ReliableForTestAsync(factory.Services, s => s.ReplyReliableAsync(new(id, taskId,
            next.DeliveryAttemptId, next.ReplyIdempotencyKey, "Recovered legacy reply"), transport))).Succeeded);
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var finalTask = await final.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var previous = await final.AgentTaskDeliveryAttempts.SingleAsync(a => a.ID == attemptId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentReplyTaskState.Replied, finalTask.State);
        Assert.Equal(1, finalTask.RetryCycle);
        Assert.Equal(0, finalTask.AutomaticRetryCount);
        Assert.Equal(AgentTaskDeliveryAttemptState.Abandoned, previous.State);
        Assert.NotNull(previous.InvalidatedAtUtc);
        Assert.Equal(next.DeliveryAttemptId, previous.ReplacementAttemptId);
        Assert.Equal(2, await final.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
