using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task ConfirmedDerivedTaskKeepsSourceAcrossResumeOrTakeoverAndServerCrash(bool takeover, bool crash, bool exhaust)
    {
        await using var host = new RecoveryProcessHost();
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Derived recovery");
        using var oldHttp = host.Client();
        await using var original = await CreateSdkAsync(oldHttp);
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(original, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data");
        var oldConnection = joined.GetProperty("connection");
        var oldId = oldConnection.GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(original, "confirm_context", new { connectionSessionId = oldId, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker start chain");
        var root = await CrashMentionAsync(original, oldId);
        var rootId = root.GetProperty("taskId").GetGuid();
        var rootAttempt = root.GetProperty("deliveryAttemptId").GetGuid();
        await ProcessCallAsync(original, "confirm_context", new { connectionSessionId = oldId, kind = "task",
            taskId = rootId, deliveryAttemptId = rootAttempt });
        await ProcessCallAsync(original, "reply_to_mention", new { connectionSessionId = oldId, taskId = rootId,
            deliveryAttemptId = rootAttempt, replyIdempotencyKey = root.GetProperty("replyIdempotencyKey").GetGuid(), content = "@Worker continue" });
        var child = await CrashMentionAsync(original, oldId);
        var taskId = child.GetProperty("taskId").GetGuid();
        var oldAttempt = child.GetProperty("deliveryAttemptId").GetGuid();
        var replyKey = child.GetProperty("replyIdempotencyKey").GetGuid();
        var source = child.GetProperty("collaboration").GetRawText();
        await ProcessCallAsync(original, "confirm_context", new { connectionSessionId = oldId, kind = "task", taskId, deliveryAttemptId = oldAttempt });
        if (exhaust)
        {
            var failedAttempt = oldAttempt;
            for (var failure = 0; failure <= 3; failure++)
            {
                var decision = (await ProcessCallAsync(original, "report_task_failure", new { connectionSessionId = oldId,
                    taskId, deliveryAttemptId = failedAttempt, failureCode = "agent_transient_failure" })).GetProperty("data");
                Assert.Equal(failure == 3, decision.GetProperty("retryExhausted").GetBoolean());
                if (failure == 3) break;
                var delay = decision.GetProperty("nextRetryAt").GetDateTimeOffset() - DateTimeOffset.UtcNow;
                Assert.True(delay < TimeSpan.FromSeconds(30));
                if (delay > TimeSpan.Zero) await Task.Delay(delay, TestContext.Current.CancellationToken);
                var retry = await CrashMentionAsync(original, oldId);
                Assert.Equal(source, retry.GetProperty("collaboration").GetRawText());
                failedAttempt = retry.GetProperty("deliveryAttemptId").GetGuid();
                await ProcessCallAsync(original, "confirm_context", new { connectionSessionId = oldId,
                    kind = "task", taskId, deliveryAttemptId = failedAttempt });
            }
            await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker a new root must not overtake failed head");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            await using var allocation = host.Database();
            while (await allocation.AgentReplyTasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken) < 3 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(3, await allocation.AgentReplyTasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        if (crash) { await host.CrashAsync(); await host.StartAsync(); }
        else await original.DisposeAsync();

        using var newHttp = host.Client();
        await using var current = await CreateSdkAsync(newHttp);
        var restored = takeover
            ? (await ProcessCallAsync(current, "join_meeting", new { meetingCode = meeting.MeetingCode, displayName = "Worker",
                contextCharacterLimit = 5000, reliabilityMode = "explicit_ack_v1", joinMode = "takeover",
                operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data")
            : (await ProcessCallAsync(current, "resume_agent", new { connectionSessionId = oldId, recoveryKey = key,
                operationId = Guid.NewGuid() })).GetProperty("data");
        var connection = restored.GetProperty("connection");
        var id = connection.GetProperty("connectionSessionId").GetGuid();
        Assert.Equal(!takeover, id == oldId);
        Assert.Equal(takeover ? 5000 : 12000, connection.GetProperty("declaredContextCharacterLimit").GetInt32());
        Assert.Equal(oldConnection.GetProperty("connectionGeneration").GetInt64() + (takeover ? 1 : 0),
            connection.GetProperty("connectionGeneration").GetInt64());
        if (!takeover) Assert.Equal(oldConnection.GetProperty("bindingVersion").GetInt64() + 1, connection.GetProperty("bindingVersion").GetInt64());
        if (takeover) await ProcessCallAsync(current, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = restored.GetProperty("initialDeliveryId").GetGuid() });
        if (exhaust)
        {
            await using (var check = host.Database())
            {
                var blockedTask = await check.AgentReplyTasks.SingleAsync(task => task.ID == taskId, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(blockedTask.RetryExhausted);
                Assert.Equal(3, blockedTask.AutomaticRetryCount);
                Assert.Equal(0, blockedTask.RetryCycle);
                Assert.Equal(rootId, blockedTask.ParentTaskId);
            }
            var blocked = SdkPayload(await current.CallToolAsync("wait_for_mention", new Dictionary<string, object?>
                { ["connectionSessionId"] = id, ["waitTimeoutSeconds"] = 1 }, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal("retry_exhausted", blocked.GetProperty("errorCode").GetString());
            await ProcessCallAsync(current, "retry_task", new { connectionSessionId = id, taskId, operationId = Guid.NewGuid() });
        }
        var next = await CrashMentionAsync(current, id);
        var attempt = next.GetProperty("deliveryAttemptId").GetGuid();
        Assert.Equal(taskId, next.GetProperty("taskId").GetGuid());
        Assert.Equal(replyKey, next.GetProperty("replyIdempotencyKey").GetGuid());
        Assert.Equal(source, next.GetProperty("collaboration").GetRawText());
        Assert.NotEqual(oldAttempt, attempt);
        var stale = SdkPayload(await current.CallToolAsync("reply_to_mention", new Dictionary<string, object?>
            { ["connectionSessionId"] = id, ["taskId"] = taskId, ["deliveryAttemptId"] = oldAttempt,
                ["replyIdempotencyKey"] = replyKey, ["content"] = "stale response" }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(stale.GetProperty("succeeded").GetBoolean());
        Assert.Equal("delivery_attempt_stale", stale.GetProperty("errorCode").GetString());
        await ProcessCallAsync(current, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attempt });
        await ProcessCallAsync(current, "reply_to_mention", new { connectionSessionId = id, taskId, deliveryAttemptId = attempt,
            replyIdempotencyKey = replyKey, content = "recovered response" });
        await using var db = host.Database();
        var persisted = await db.AgentReplyTasks.SingleAsync(task => task.ID == taskId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(rootId, persisted.ParentTaskId);
        Assert.Equal(root.GetProperty("triggerMessageId").GetGuid(), persisted.ChainId);
        Assert.Equal(AgentReplyTaskState.Replied, persisted.State);
        Assert.Equal(exhaust ? 3 : 2, await db.AgentReplyTasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(exhaust ? 4 : 3, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        if (exhaust)
        {
            Assert.Equal(1, persisted.RetryCycle);
            Assert.Equal(0, persisted.AutomaticRetryCount);
            var tail = await db.AgentReplyTasks.SingleAsync(task => task.ID != taskId && task.ID != rootId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(AgentReplyTaskState.Queued, tail.State);
            Assert.Null(tail.ParentTaskId);
            Assert.NotEqual(persisted.ChainId, tail.ChainId);
            var tailDelivery = await CrashMentionAsync(current, id);
            Assert.Equal(tail.ID, tailDelivery.GetProperty("taskId").GetGuid());
            var tailAttempt = tailDelivery.GetProperty("deliveryAttemptId").GetGuid();
            await ProcessCallAsync(current, "confirm_context", new { connectionSessionId = id, kind = "task",
                taskId = tail.ID, deliveryAttemptId = tailAttempt });
            await ProcessCallAsync(current, "reply_to_mention", new { connectionSessionId = id, taskId = tail.ID,
                deliveryAttemptId = tailAttempt, replyIdempotencyKey = tail.ReplyIdempotencyKey, content = "next root completed" });
            await db.Entry(tail).ReloadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AgentReplyTaskState.Replied, tail.State);
            Assert.Equal(5, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
