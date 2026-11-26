using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RealProcessCrashBeforeReplyResponseRollsBackOrReplaysTheWholeReply(bool afterCommit, bool collaborate)
    {
        await using var host = new RecoveryProcessHost(faultInjection: true);
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Transaction crash reply");
        using var oldHttp = host.Client();
        await using var oldSdk = await CreateSdkAsync(oldHttp);
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(oldSdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker Reply transaction question");
        var mention = await CrashMentionAsync(oldSdk, id);
        var taskId = mention.GetProperty("taskId").GetGuid();
        var attemptId = mention.GetProperty("deliveryAttemptId").GetGuid();
        var replyKey = mention.GetProperty("replyIdempotencyKey").GetGuid();
        await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId });
        var replyContent = collaborate ? "@Worker Transaction reply" : "@Alice Transaction reply";
        host.ArmCrash("reply", afterCommit);
        var pending = ActivationCallAsync(oldSdk, "reply_to_mention", new { connectionSessionId = id, taskId,
            deliveryAttemptId = attemptId, replyIdempotencyKey = replyKey, content = replyContent });
        try
        {
            await host.WaitForCrashPointAsync();
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            await host.CrashAsync();
            try { await pending; } catch (Exception) { }
        }
        Guid? committedMessage = null;
        await using (var db = host.Database())
        {
            var task = await db.AgentReplyTasks.SingleAsync(task => task.ID == taskId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(afterCommit ? AgentReplyTaskState.Replied : AgentReplyTaskState.Processing, task.State);
            committedMessage = task.ReplyMessageId;
            Assert.Equal(afterCommit, committedMessage.HasValue);
            Assert.Equal(afterCommit ? 2 : 1, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(afterCommit ? 2 : 1, await db.MessageAppendedEvents.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(afterCommit ? 2 : 1, await db.MessageMentions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(attemptId, task.CurrentAttemptId);
        }
        await host.StartAsync();
        using var newHttp = host.Client();
        await using var newSdk = await CreateSdkAsync(newHttp);
        await ProcessCallAsync(newSdk, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() });
        if (!afterCommit)
        {
            var next = await CrashMentionAsync(newSdk, id);
            Assert.Equal(taskId, next.GetProperty("taskId").GetGuid());
            Assert.Equal(replyKey, next.GetProperty("replyIdempotencyKey").GetGuid());
            var nextAttempt = next.GetProperty("deliveryAttemptId").GetGuid();
            Assert.NotEqual(attemptId, nextAttempt);
            attemptId = nextAttempt;
            await ProcessCallAsync(newSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId });
        }
        var result = (await ProcessCallAsync(newSdk, "reply_to_mention", new { connectionSessionId = id, taskId,
            deliveryAttemptId = attemptId, replyIdempotencyKey = replyKey, content = replyContent })).GetProperty("data");
        Assert.Equal(afterCommit, result.GetProperty("replayed").GetBoolean());
        if (afterCommit) Assert.Equal(committedMessage, result.GetProperty("message").GetProperty("messageId").GetGuid());
        await using var final = host.Database();
        Assert.Equal(2, await final.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await final.MessageAppendedEvents.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await final.MessageMentions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(AgentReplyTaskState.Replied, (await final.AgentReplyTasks.SingleAsync(task => task.ID == taskId, cancellationToken: TestContext.Current.CancellationToken)).State);
        if (collaborate)
        {
            var child = await CrashMentionAsync(newSdk, id);
            var childId = child.GetProperty("taskId").GetGuid();
            var childAttempt = child.GetProperty("deliveryAttemptId").GetGuid();
            Assert.Equal(taskId, child.GetProperty("collaboration").GetProperty("parentTaskId").GetGuid());
            Assert.Equal(mention.GetProperty("triggerMessageId").GetGuid(), child.GetProperty("collaboration").GetProperty("chainId").GetGuid());
            Assert.Equal(result.GetProperty("message").GetProperty("messageId").GetGuid(), child.GetProperty("triggerMessageId").GetGuid());
            Assert.Equal(2, await final.AgentReplyTasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            await ProcessCallAsync(newSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId = childId, deliveryAttemptId = childAttempt });
            await ProcessCallAsync(newSdk, "reply_to_mention", new { connectionSessionId = id, taskId = childId,
                deliveryAttemptId = childAttempt, replyIdempotencyKey = child.GetProperty("replyIdempotencyKey").GetGuid(), content = "Downstream finished" });
            Assert.Equal(3, await final.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(3, await final.MessageAppendedEvents.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, await final.AgentReplyTasks.CountAsync(task => task.State == AgentReplyTaskState.Replied, cancellationToken: TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, await final.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProcessCrashAtDeletionBarrierPreservesActiveOrCleansAllRecoveryResources(bool afterCommit)
    {
        await using var host = new RecoveryProcessHost(faultInjection: true);
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        const string name = "Transaction crash deletion";
        var meeting = await CreateMeetingAsync(browser, name);
        using var oldHttp = host.Client();
        await using var oldSdk = await CreateSdkAsync(oldHttp);
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(oldSdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data");
        var oldId = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = oldId, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker Deletion crash question");
        var first = await CrashMentionAsync(oldSdk, oldId);
        var taskId = first.GetProperty("taskId").GetGuid();
        using var takeoverHttp = host.Client();
        await using var takeoverSdk = await CreateSdkAsync(takeoverHttp);
        var taken = (await ProcessCallAsync(takeoverSdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", joinMode = "takeover",
            operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data");
        var id = taken.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(takeoverSdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = taken.GetProperty("initialDeliveryId").GetGuid() });
        var next = await CrashMentionAsync(takeoverSdk, id);
        var attemptId = next.GetProperty("deliveryAttemptId").GetGuid();
        await ProcessCallAsync(takeoverSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId });
        await ProcessCallAsync(takeoverSdk, "report_task_failure", new { connectionSessionId = id, taskId, deliveryAttemptId = attemptId, failureCode = "agent_permanent_failure" });
        await using (var ready = host.Database())
        {
            Assert.Equal(2, await ready.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, await ready.AgentInitialContextDeliveries.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(2, await ready.AgentTaskDeliveryAttempts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.True((await ready.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RetryExhausted);
            Assert.Equal(id, (await ready.AgentConnectionSessions.SingleAsync(c => c.ID == oldId, cancellationToken: TestContext.Current.CancellationToken)).SupersededByConnectionId);
            Assert.Equal(attemptId, (await ready.AgentTaskDeliveryAttempts.SingleAsync(a => a.ID == first.GetProperty("deliveryAttemptId").GetGuid(), cancellationToken: TestContext.Current.CancellationToken)).ReplacementAttemptId);
        }
        host.ArmCrash("deletion", afterCommit);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meeting.MeetingId}/deletion")
            { Content = JsonContent.Create(new { confirmationName = name }) };
        request.Headers.Add("Origin", "http://localhost:5173");
        var pending = browser.SendAsync(request, TestContext.Current.CancellationToken);
        try
        {
            await host.WaitForCrashPointAsync();
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            await host.CrashAsync();
            try { using var ignored = await pending; } catch (HttpRequestException) { }
        }
        await using (var cut = host.Database())
        {
            Assert.Equal(afterCommit ? "Deleting" : "Active", (await cut.Meetings.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).LifecycleState.ToString());
            Assert.Equal(2, await cut.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        await host.StartAsync();
        using var recoveredHttp = host.Client();
        await using var recoveredSdk = await CreateSdkAsync(recoveredHttp);
        var result = await ActivationCallAsync(recoveredSdk, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() });
        if (!afterCommit)
        {
            Assert.True(result.GetProperty("succeeded").GetBoolean());
            await using var recovered = host.Database();
            Assert.Equal(2, await recovered.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.True((await recovered.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RetryExhausted);
            return;
        }
        Assert.False(result.GetProperty("succeeded").GetBoolean());
        var deniedJoin = await ActivationCallAsync(recoveredSdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", joinMode = "takeover",
            operationId = Guid.NewGuid(), recoveryKey = key });
        Assert.Equal("meeting_not_found", deniedJoin.GetProperty("errorCode").GetString());
        await using var final = host.Database();
        Assert.Empty(await final.Meetings.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.AgentConnectionSessions.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.AgentContextCursors.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.AgentInitialContextDeliveries.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.AgentRecoveryOperations.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.AgentReplyTasks.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.AgentTaskDeliveryAttempts.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.MeetingMessages.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.MessageAppendedEvents.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await final.MessageEventDeliveries.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await final.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<JsonElement> CrashMentionAsync(McpClient sdk, Guid connectionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = connectionId, waitTimeoutSeconds = 5 });
            if (result.GetProperty("succeeded").GetBoolean())
            {
                var data = result.GetProperty("data");
                if (data.GetProperty("kind").GetString() == "mention") return data;
            }
            else Assert.Equal("retry_pending", result.GetProperty("errorCode").GetString());
            await Task.Delay(50);
        }
        throw new TimeoutException("The recovered queue head was not delivered.");
    }

    [Theory]
    [InlineData("join", false)]
    [InlineData("join", true)]
    [InlineData("activation", false)]
    [InlineData("activation", true)]
    public async Task RealProcessCrashAtJoinTransactionBoundaryRetainsExactlyTheCommittedIdentity(string boundary, bool afterCommit)
    {
        await using var host = new RecoveryProcessHost(faultInjection: true);
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Transaction crash join");
        using var oldHttp = host.Client();
        await using var oldSdk = await CreateSdkAsync(oldHttp);
        var key = new string('A', 43);
        var request = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key };
        host.ArmCrash(boundary, afterCommit);
        var pending = ActivationCallAsync(oldSdk, "join_meeting", request);
        try
        {
            await host.WaitForCrashPointAsync();
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            await host.CrashAsync();
            try { await pending; } catch (Exception) { /* Lost transport outcome is inspected through SQLite below. */ }
        }
        var committed = boundary != "join" || afterCommit;
        Guid? committedId = null;
        await using (var db = host.Database())
        {
            Assert.Equal(committed ? 1 : 0, await db.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(committed ? 1 : 0, await db.AgentContextCursors.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(committed ? 1 : 0, await db.AgentInitialContextDeliveries.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(committed ? 1 : 0, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(committed ? 1 : 0, await db.MeetingParticipants.CountAsync(p => p.ParticipantType == ParticipantType.Agent, cancellationToken: TestContext.Current.CancellationToken));
            if (committed)
            {
                var connection = await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
                committedId = connection.ID;
                Assert.Equal(boundary == "activation" && afterCommit ? AgentConnectionState.Connected : AgentConnectionState.Activating, connection.State);
            }
        }
        await host.StartAsync();
        using var newHttp = host.Client();
        await using var newSdk = await CreateSdkAsync(newHttp);
        var replay = (await ProcessCallAsync(newSdk, "join_meeting", request)).GetProperty("data");
        if (committed)
        {
            Assert.Equal("recovery_required", replay.GetProperty("kind").GetString());
            Assert.Equal(committedId, replay.GetProperty("connectionSessionId").GetGuid());
            var resumed = (await ProcessCallAsync(newSdk, "resume_agent", new { connectionSessionId = committedId,
                recoveryKey = key, operationId = Guid.NewGuid() })).GetProperty("data");
            Assert.Equal(committedId, resumed.GetProperty("connection").GetProperty("connectionSessionId").GetGuid());
            Assert.Equal(2, resumed.GetProperty("connection").GetProperty("bindingVersion").GetInt64());
        }
        else Assert.Equal("joined", replay.GetProperty("kind").GetString());
        await using var final = host.Database();
        Assert.Equal(1, await final.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, (await final.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ConnectionGeneration);
        Assert.Equal(0, await final.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
