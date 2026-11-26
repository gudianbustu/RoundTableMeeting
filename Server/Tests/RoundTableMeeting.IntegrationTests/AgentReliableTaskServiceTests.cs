using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ReliableTaskCycleConfirmsExplicitlyAndReplaysReplyAfterResume()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reliable task cycle");
        var join = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var transport = RecoveryTransport(71);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, join, transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        Assert.Equal("initial_context_unconfirmed", (await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).ErrorCode);
        Assert.True((await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport))).Succeeded);
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first task");
        var mention = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        var replay = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        Assert.True(replay.Replayed);
        Assert.Equal(mention.DeliveryAttemptId, replay.DeliveryAttemptId);
        Assert.Equal(mention.ConfirmationDeadline, replay.ConfirmationDeadline);
        var replyRequest = new AgentReplyRequest(id, mention.TaskId, mention.DeliveryAttemptId, mention.ReplyIdempotencyKey, "@Alice completed");
        Assert.Equal("context_confirmation_required", (await ReliableForTestAsync(factory.Services, s => s.ReplyReliableAsync(replyRequest, transport))).ErrorCode);
        var status = Assert.IsType<AgentStatusResultDto>((await ReliableForTestAsync(factory.Services, s => s.StatusReliableAsync(new(id), transport))).Data);
        Assert.Equal(0, status.DeliveredThroughSequence);
        Assert.Equal("delivered", status.QueueHead!.State);
        Assert.True((await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "task", TaskId: mention.TaskId, DeliveryAttemptId: mention.DeliveryAttemptId), transport))).Succeeded);
        Assert.Equal("task_in_progress", (await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).ErrorCode);
        var reply = Assert.IsType<AgentReplyResultDto>((await ReliableForTestAsync(factory.Services, s => s.ReplyReliableAsync(replyRequest, transport))).Data);
        Assert.False(reply.Replayed);
        Assert.Single(reply.Message.Mentions);
        var nextTransport = RecoveryTransport(72);
        Assert.True((await ReliableForTestAsync(factory.Services, s => s.ResumeReliableAsync(new(id, join.RecoveryKey, Guid.NewGuid()), nextTransport))).Succeeded);
        var replayedReply = Assert.IsType<AgentReplyResultDto>((await ReliableForTestAsync(factory.Services, s => s.ReplyReliableAsync(replyRequest, nextTransport))).Data);
        Assert.True(replayedReply.Replayed);
        Assert.Equal(reply.Message.MessageId, replayedReply.Message.MessageId);
        Assert.Equal("reply_idempotency_mismatch", (await ReliableForTestAsync(factory.Services, s => s.ReplyReliableAsync(replyRequest with { Content = "different" }, nextTransport))).ErrorCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(2, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(AgentReplyTaskState.Replied, (await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task FailedHeadBlocksFifoUntilExplicitRetryAndOldFailureReceiptCannotResetTheCycle()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Failure cycle");
        var join = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var transport = RecoveryTransport(73);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, join, transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport));
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second");
        var allocationDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            await using var allocationScope = factory.Services.CreateAsyncScope();
            var allocationDb = allocationScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var allocated = await allocationDb.AgentReplyTasks.CountAsync(task => task.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken);
            if (allocated == 2) break;
            Assert.True(DateTimeOffset.UtcNow < allocationDeadline, $"Expected two allocated tasks, found {allocated}.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        var first = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "task", TaskId: first.TaskId, DeliveryAttemptId: first.DeliveryAttemptId), transport));
        var failureRequest = new AgentFailureRequest(id, first.TaskId, first.DeliveryAttemptId, "agent_permanent_failure");
        var failed = Assert.IsType<AgentFailureResultDto>((await ReliableForTestAsync(factory.Services, s => s.ReportFailureReliableAsync(failureRequest, transport))).Data);
        Assert.True(failed.RetryExhausted);
        Assert.Equal("retry_exhausted", (await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).ErrorCode);
        var retryRequest = new AgentRetryRequest(id, first.TaskId, Guid.NewGuid());
        var retry = Assert.IsType<AgentRetryResultDto>((await ReliableForTestAsync(factory.Services, s => s.RetryReliableAsync(retryRequest, transport))).Data);
        Assert.Equal(1, retry.RetryCycle);
        Assert.True(Assert.IsType<AgentRetryResultDto>((await ReliableForTestAsync(factory.Services, s => s.RetryReliableAsync(retryRequest, transport))).Data).Replayed);
        var replacement = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        Assert.Equal(first.TaskId, replacement.TaskId);
        Assert.Equal(first.ReplyIdempotencyKey, replacement.ReplyIdempotencyKey);
        Assert.NotEqual(first.DeliveryAttemptId, replacement.DeliveryAttemptId);
        Assert.Equal("delivery_attempt_stale", (await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "task", TaskId: first.TaskId, DeliveryAttemptId: first.DeliveryAttemptId), transport))).ErrorCode);
        var receipt = Assert.IsType<AgentFailureResultDto>((await ReliableForTestAsync(factory.Services, s => s.ReportFailureReliableAsync(failureRequest, transport))).Data);
        Assert.True(receipt.Replayed);
        Assert.Equal(0, receipt.RetryCycle);
        Assert.Equal(1, receipt.CurrentRetryCycle);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await db.AgentReplyTasks.OrderBy(task => task.TriggerSequence).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, tasks[0].AutomaticRetryCount);
        Assert.Equal(AgentReplyTaskState.Delivered, tasks[0].State);
        Assert.Equal(AgentReplyTaskState.Queued, tasks[1].State);
        Assert.Equal(AgentTaskDeliveryAttemptState.Abandoned, (await db.AgentTaskDeliveryAttempts.SingleAsync(item => item.ID == first.DeliveryAttemptId, cancellationToken: TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task ConfirmationTimeoutCreatesOneBackoffDecisionAndAutomaticReplacementConsumesOneSlot()
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Confirmation timeout");
        var transport = RecoveryTransport(74);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, ReliableJoinRequest(meeting.MeetingCode, "Worker", "create"), transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport));
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        var mention = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        time.Now = mention.ConfirmationDeadline!.Value;
        var pending = await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport));
        Assert.Equal("retry_pending", pending.ErrorCode);
        var due = Assert.IsType<AgentRecoveryErrorData>(pending.Data).NextRetryAt!.Value;
        Assert.Equal(due, Assert.IsType<AgentRecoveryErrorData>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data).NextRetryAt);
        time.Now = due;
        var replacement = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        Assert.NotEqual(mention.DeliveryAttemptId, replacement.DeliveryAttemptId);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AutomaticRetryCount);
    }
}
