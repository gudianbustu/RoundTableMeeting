using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReliableToolsRejectForeignConnectionsTasksAndAttemptsWithoutChangingEitherAgent(bool otherMeeting)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        using var browser = test.Factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meetingId = test.MeetingId;
        var meetingCode = test.MeetingCode;
        if (otherMeeting)
        {
            var created = await CreateMeetingAsync(browser, "Other ownership boundary");
            meetingId = created.MeetingId;
            meetingCode = created.MeetingCode;
        }
        using var http = test.Factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var joined = (await ActivationCallAsync(sdk, "join_meeting", new { meetingCode, displayName = "Other",
            contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(),
            recoveryKey = new string('A', 43) })).GetProperty("data");
        var ownId = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = ownId, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() })).GetProperty("succeeded").GetBoolean());
        await PostHumanMessageAsync(browser, meetingId, "@Other own task");
        var own = (await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = ownId })).GetProperty("data");
        var ownTask = own.GetProperty("taskId").GetGuid();
        var ownAttempt = own.GetProperty("deliveryAttemptId").GetGuid();
        var ownKey = own.GetProperty("replyIdempotencyKey").GetGuid();
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = ownId, kind = "task",
            taskId = ownTask, deliveryAttemptId = ownAttempt })).GetProperty("succeeded").GetBoolean());
        async Task<string> Facts()
        {
            await using var scope = test.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            return JsonSerializer.Serialize(new
            {
                Connections = await db.AgentConnectionSessions.OrderBy(c => c.ID).Select(c => new
                    { c.ID, c.State, c.BindingVersion, c.ConnectionGeneration, c.LeaseExpiresAtUtc }).ToArrayAsync(),
                Tasks = await db.AgentReplyTasks.OrderBy(t => t.ID).Select(t => new
                    { t.ID, t.State, t.CurrentAttemptId, t.ReplyMessageId, t.RetryCycle, t.AutomaticRetryCount }).ToArrayAsync(),
                Cursors = await db.AgentContextCursors.OrderBy(c => c.ID).Select(c => new
                    { c.ID, c.CurrentConnectionId, c.DeliveredThroughSequence, c.ActivityRevision }).ToArrayAsync(),
                Messages = await db.MeetingMessages.CountAsync(), Receipts = await db.AgentRecoveryOperations.CountAsync()
            });
        }
        var before = await Facts();
        async Task Reject(string name, object args, string error)
        {
            var response = await ActivationCallAsync(sdk, name, args);
            Assert.False(response.GetProperty("succeeded").GetBoolean());
            Assert.Equal(error, response.GetProperty("errorCode").GetString());
            var data = response.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Null)
            {
                Assert.False(data.TryGetProperty("taskSummary", out var summary) && summary.ValueKind != JsonValueKind.Null);
                Assert.False(data.TryGetProperty("initialContext", out _));
                Assert.False(data.TryGetProperty("message", out _));
            }
            Assert.Equal(before, await Facts());
        }
        var foreignTask = test.Mention.TaskId;
        var foreignAttempt = test.Mention.DeliveryAttemptId;
        foreach (var connectionId in new[] { test.ConnectionId, Guid.NewGuid() })
        {
            await Reject("get_agent_status", new { connectionSessionId = connectionId, taskId = foreignTask }, "connection_invalid");
            await Reject("wait_for_mention", new { connectionSessionId = connectionId, waitTimeoutSeconds = 1 }, "connection_invalid");
            await Reject("renew_agent_lease", new { connectionSessionId = connectionId }, "connection_invalid");
            await Reject("confirm_context", new { connectionSessionId = connectionId, kind = "task", taskId = foreignTask, deliveryAttemptId = foreignAttempt }, "connection_invalid");
            await Reject("reply_to_mention", new { connectionSessionId = connectionId, taskId = foreignTask, deliveryAttemptId = foreignAttempt,
                replyIdempotencyKey = test.Mention.ReplyIdempotencyKey, content = "Rejected foreign reply" }, "connection_invalid");
            await Reject("report_task_failure", new { connectionSessionId = connectionId, taskId = foreignTask, deliveryAttemptId = foreignAttempt,
                failureCode = "agent_permanent_failure" }, "connection_invalid");
            await Reject("retry_task", new { connectionSessionId = connectionId, taskId = foreignTask, operationId = Guid.NewGuid() }, "connection_invalid");
        }
        await Reject("get_agent_status", new { connectionSessionId = ownId, taskId = foreignTask }, "task_not_available");
        await Reject("confirm_context", new { connectionSessionId = ownId, kind = "task", taskId = foreignTask, deliveryAttemptId = foreignAttempt }, "task_not_available");
        await Reject("reply_to_mention", new { connectionSessionId = ownId, taskId = foreignTask, deliveryAttemptId = foreignAttempt,
            replyIdempotencyKey = test.Mention.ReplyIdempotencyKey, content = "Rejected foreign task" }, "task_not_available");
        await Reject("report_task_failure", new { connectionSessionId = ownId, taskId = foreignTask, deliveryAttemptId = foreignAttempt,
            failureCode = "agent_permanent_failure" }, "task_not_available");
        await Reject("retry_task", new { connectionSessionId = ownId, taskId = foreignTask, operationId = Guid.NewGuid() }, "task_not_available");
        await Reject("confirm_context", new { connectionSessionId = ownId, kind = "task", taskId = ownTask, deliveryAttemptId = foreignAttempt }, "delivery_attempt_invalid");
        await Reject("reply_to_mention", new { connectionSessionId = ownId, taskId = ownTask, deliveryAttemptId = foreignAttempt,
            replyIdempotencyKey = ownKey, content = "Rejected foreign attempt" }, "delivery_attempt_invalid");
        await Reject("report_task_failure", new { connectionSessionId = ownId, taskId = ownTask, deliveryAttemptId = foreignAttempt,
            failureCode = "agent_permanent_failure" }, "delivery_attempt_invalid");
        Assert.True((await ActivationCallAsync(sdk, "reply_to_mention", new { connectionSessionId = ownId, taskId = ownTask,
            deliveryAttemptId = ownAttempt, replyIdempotencyKey = ownKey, content = "Own authorized reply" })).GetProperty("succeeded").GetBoolean());
        Assert.True((await test.Run(s => s.ReplyReliableAsync(test.Reply, test.Transport))).Succeeded);
        await using var finalScope = test.Services.CreateAsyncScope();
        Assert.All(await finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken),
            t => Assert.Equal(AgentReplyTaskState.Replied, t.State));
    }
}
