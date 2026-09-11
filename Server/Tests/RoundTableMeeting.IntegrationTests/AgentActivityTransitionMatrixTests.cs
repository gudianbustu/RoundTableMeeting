using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ActivityRevisionCoversTimeoutReplacementReplyAndImmutableReplays()
    {
        using var test = await ReliableRaceFixture.CreateAsync(confirmTask: false);
        var registry = test.Services.GetRequiredService<IAgentConnectionRegistry>();
        Assert.True(registry.TryGetRegistration(test.Transport, out var registration));
        var activity = await ReadActivityForTestAsync(test.Services, test.MeetingId, registration.ParticipantId);
        async Task Observe(bool changed, string reason)
        {
            var next = await ReadActivityForTestAsync(test.Services, test.MeetingId, registration.ParticipantId);
            Assert.Equal(activity.Revision + (changed ? 1 : 0), next.Revision);
            Assert.Equal(reason, next.Reason);
            if (!changed) Assert.Equal(activity, next);
            activity = next;
            test.Clock.Now = test.Clock.Now.AddTicks(1);
        }
        using var browser = test.Factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        await PostHumanMessageAsync(browser, test.MeetingId, "@Worker later queue entry");
        await EventuallyAsync(test.Factory, async db => await db.AgentReplyTasks.CountAsync() == 2);
        await Observe(false, "awaiting_context_confirmation");
        Assert.True((await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport))).Succeeded);
        await Observe(false, "awaiting_context_confirmation");
        test.Clock.Now = test.Mention.ConfirmationDeadline!.Value;
        await test.Run(async s =>
        {
            Assert.True(await s.ReconcileConnectionAsync(registration));
            return AgentOperationResult.Success(new { }, "Reconciled");
        });
        await Observe(true, "retry_pending");
        await test.Run(async s =>
        {
            Assert.False(await s.ReconcileConnectionAsync(registration));
            return AgentOperationResult.Success(new { }, "Unchanged");
        });
        await Observe(false, "retry_pending");
        Assert.Equal("retry_pending", (await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport))).ErrorCode);
        await Observe(false, "retry_pending");
        test.Clock.Now = activity.NextRetryAt!.Value;
        var replacement = Assert.IsType<AgentMentionResultDto>((await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport))).Data);
        await Observe(true, "awaiting_context_confirmation");
        var confirmation = new AgentConfirmContextRequest(test.ConnectionId, "task", TaskId: replacement.TaskId,
            DeliveryAttemptId: replacement.DeliveryAttemptId);
        Assert.True((await test.Run(s => s.ConfirmReliableAsync(confirmation, test.Transport))).Succeeded);
        await Observe(true, "processing");
        Assert.True((await test.Run(s => s.ConfirmReliableAsync(confirmation, test.Transport))).Succeeded);
        Assert.True((await test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport))).Succeeded);
        Assert.True((await test.Run(s => s.StatusReliableAsync(new(test.ConnectionId), test.Transport))).Succeeded);
        await Observe(false, "processing");
        var failure = new AgentFailureRequest(test.ConnectionId, replacement.TaskId, replacement.DeliveryAttemptId, "agent_permanent_failure");
        Assert.True((await test.Run(s => s.ReportFailureReliableAsync(failure, test.Transport))).Succeeded);
        await Observe(true, "retry_exhausted");
        Assert.True((await test.Run(s => s.ReportFailureReliableAsync(failure, test.Transport))).Succeeded);
        await Observe(false, "retry_exhausted");
        var retry = new AgentRetryRequest(test.ConnectionId, replacement.TaskId, Guid.NewGuid());
        Assert.True((await test.Run(s => s.RetryReliableAsync(retry, test.Transport))).Succeeded);
        await Observe(true, "retry_pending");
        Assert.True((await test.Run(s => s.RetryReliableAsync(retry, test.Transport))).Succeeded);
        await Observe(false, "retry_pending");
        var next = Assert.IsType<AgentMentionResultDto>((await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport))).Data);
        await Observe(true, "awaiting_context_confirmation");
        Assert.True((await test.Run(s => s.ConfirmReliableAsync(new(test.ConnectionId, "task", TaskId: next.TaskId,
            DeliveryAttemptId: next.DeliveryAttemptId), test.Transport))).Succeeded);
        await Observe(true, "processing");
        // Prior-cycle receipts must not rewrite the currently processing task or its revision.
        Assert.True((await test.Run(s => s.ReportFailureReliableAsync(failure, test.Transport))).Succeeded);
        Assert.True((await test.Run(s => s.RetryReliableAsync(retry, test.Transport))).Succeeded);
        await Observe(false, "processing");
        var reply = new AgentReplyRequest(test.ConnectionId, next.TaskId, next.DeliveryAttemptId, next.ReplyIdempotencyKey, "First completed reply");
        Assert.True((await test.Run(s => s.ReplyReliableAsync(reply, test.Transport))).Succeeded);
        await Observe(true, "waiting");
        Assert.True((await test.Run(s => s.ReplyReliableAsync(reply, test.Transport))).Succeeded);
        Assert.True((await test.Run(s => s.ConfirmReliableAsync(new(test.ConnectionId, "task", TaskId: next.TaskId,
            DeliveryAttemptId: next.DeliveryAttemptId), test.Transport))).Succeeded);
        await Observe(false, "waiting");
        var second = Assert.IsType<AgentMentionResultDto>((await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport))).Data);
        Assert.NotEqual(next.TaskId, second.TaskId);
        await Observe(true, "awaiting_context_confirmation");
        Assert.Equal(second.TaskId, activity.ActiveTaskId);
        await using var scope = test.Services.CreateAsyncScope();
        var cursor = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentContextCursors.SingleAsync();
        Assert.Equal(activity.Revision, cursor.ActivityRevision);
        Assert.Equal(activity.ChangedAt, cursor.ActivityChangedAtUtc);
    }
}
