using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("Queued", null)]
    [InlineData("Delivered", "Prepared")]
    [InlineData("Delivered", "Delivered")]
    [InlineData("Processing", "Delivered")]
    [InlineData("Failed", null)]
    [InlineData("Failed", "Prepared")]
    [InlineData("Failed", "Delivered")]
    [InlineData("Failed", "Abandoned")]
    [InlineData("Replied", "Delivered")]
    [InlineData("Cancelled", null)]
    [InlineData("Cancelled", "Delivered")]
    [InlineData("Cancelled", "Abandoned")]
    public async Task ProductionHostMigratesLegacyTaskThenTakeoverPreservesOrCompletesIt(string state, string? attemptState)
    {
        // Seed the actual old schema before starting the normal host. Startup performs
        // the production migration and reconciliation before exposing the MCP endpoint.
        using var factory = new TestApplicationFactory();
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var connectionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var attemptId = attemptState is null ? (Guid?)null : Guid.NewGuid();
        var replyKey = Guid.NewGuid();
        Guid? replyId = null;
        Meeting meeting;
        MeetingParticipant agent;
        await using (var db = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite($"Data Source={factory.DatabasePath};Foreign Keys=True;Pooling=False").Options))
        {
            await db.Database.MigrateAsync("20260904073122_AddAgentCollaboration");
            var user = UserAccount.Create("old-owner", "Old owner");
            user.InitializePasswordHash("test-hash");
            meeting = Meeting.Create("Old schema takeover", null, user.ID, now);
            var human = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Old owner", now);
            agent = MeetingParticipant.CreateAgent(meeting.ID, "Worker", now);
            var trigger = MeetingMessage.CreateParticipant(meeting.ID, 1, human.ID, MessageAuthorKind.Human,
                human.DisplayName, "@Worker old task", Guid.NewGuid(), now);
            var mention = MessageMention.Create(meeting.ID, trigger.ID, agent.ID, "Worker", 0, 7);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO UserAccounts (ID, Username, NormalizedUsername, Nickname, PasswordHash) VALUES ({user.ID}, {user.Username}, {user.NormalizedUsername}, {user.Nickname}, {user.PasswordHash})");
        db.AddRange(meeting, human, agent, trigger, mention);
            if (state == "Replied")
            {
                var reply = MeetingMessage.CreateParticipant(meeting.ID, 2, agent.ID, MessageAuthorKind.Agent,
                    "Worker", "Old reply", replyKey, now);
                replyId = reply.ID;
                db.Add(reply);
            }
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AgentConnectionSessions
                (ID,MeetingId,ParticipantId,Transport,State,DeclaredContextCharacterLimit,InitialSnapshotSequence,
                 InitialContextState,InitialContextConfirmedAtUtc,McpTransportBinding,ConnectedAtUtc,LastActivityAtUtc,CreatedAtUtc)
                VALUES ({connectionId},{meeting.ID},{agent.ID},'mcp-streamable-http','Connected',12000,0,
                 'Confirmed',{now},'old-migration-binding',{now},{now},{now})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AgentContextCursors (ID,MeetingId,ParticipantId,DeliveredThroughSequence,AllocatedThroughSequence,UpdatedAtUtc)
                VALUES ({Guid.NewGuid()},{meeting.ID},{agent.ID},1,1,{now})
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AgentReplyTasks (ID,MeetingId,AgentParticipantId,TriggerMessageId,TriggerSequence,TriggerMentionId,
                    SourceAuthorKind,SourceParticipantId,CandidateFromSequenceExclusive,CandidateToSequenceInclusive,
                    ReplyIdempotencyKey,State,ReplyMessageId,DeliveredAtUtc,ProcessingAtUtc,RepliedAtUtc,FailureCode,FailedAtUtc,CancelledAtUtc,CreatedAtUtc)
                VALUES ({taskId},{meeting.ID},{agent.ID},{trigger.ID},1,{mention.ID},'Human',{human.ID},0,1,
                    {replyKey},{state},{replyId},{(state != "Queued" ? now : (DateTimeOffset?)null)},
                    {(state is "Processing" or "Replied" ? now : (DateTimeOffset?)null)},
                    {(state == "Replied" ? now : (DateTimeOffset?)null)},
                    {(state == "Failed" ? "context_invalid" : null)},
                    {(state == "Failed" ? now : (DateTimeOffset?)null)},
                    {(state == "Cancelled" ? now : (DateTimeOffset?)null)},{now})
                """);
            if (attemptId is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO AgentTaskDeliveryAttempts (ID,TaskId,ConnectionSessionId,MeetingId,CandidateFirstSequence,
                        CandidateLastSequence,DeclaredContextCharacterLimit,DeliveredCharacterCount,Truncated,OverLimit,
                        TruncatedMessageCount,State,CreatedAtUtc,DeliveredFirstSequence,DeliveredLastSequence,DeliveredAtUtc)
                    VALUES ({attemptId},{taskId},{connectionId},{meeting.ID},1,1,12000,100,0,0,0,{attemptState},{now},
                        {(attemptState == "Delivered" ? 1L : (long?)null)},
                        {(attemptState == "Delivered" ? 1L : (long?)null)},
                        {(attemptState == "Delivered" ? now : (DateTimeOffset?)null)})
                    """);
        }
        using var http = CreateAnonymousClient(factory);
        await using var sdk = await CreateSdkAsync(http);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(AgentConnectionState.Disconnected, (await db.AgentConnectionSessions.SingleAsync()).State);
            Assert.Equal("legacy_v1", (await db.AgentContextCursors.SingleAsync()).RequiredReliabilityMode);
            Assert.Equal(attemptId, (await db.AgentReplyTasks.SingleAsync()).CurrentAttemptId);
        }
        var joined = await ActivationCallAsync(sdk, "join_meeting", new
        {
            meetingCode = meeting.MeetingCode.ToString("D"), displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43)
        });
        Assert.True(joined.GetProperty("succeeded").GetBoolean(), joined.GetProperty("errorCode").GetString());
        var joinedData = joined.GetProperty("data");
        var id = joinedData.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.NotEqual(connectionId, id);
        Assert.Equal(agent.ID, joinedData.GetProperty("agent").GetProperty("participantId").GetGuid());
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = joinedData.GetProperty("initialDeliveryId").GetGuid() })).GetProperty("succeeded").GetBoolean());
        if (state is "Replied" or "Cancelled")
        {
            var idle = await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 });
            Assert.Equal("idle", idle.GetProperty("data").GetProperty("kind").GetString());
        }
        else
        {
            // Startup classifies old in-flight tasks as recoverable; the original old
            // Failed head remains blocked until this explicit retry.
            if (state == "Failed")
                Assert.True((await ActivationCallAsync(sdk, "retry_task", new { connectionSessionId = id, taskId,
                    operationId = Guid.NewGuid() })).GetProperty("succeeded").GetBoolean());
            var wait = await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 });
            if (!wait.GetProperty("succeeded").GetBoolean())
            {
                Assert.Equal("retry_pending", wait.GetProperty("errorCode").GetString());
                var due = wait.GetProperty("data").GetProperty("nextRetryAt").GetDateTimeOffset();
                var delay = due - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
                wait = await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 });
            }
            Assert.True(wait.GetProperty("succeeded").GetBoolean());
            var task = wait.GetProperty("data");
            var newAttemptId = task.GetProperty("deliveryAttemptId").GetGuid();
            Assert.Equal(taskId, task.GetProperty("taskId").GetGuid());
            Assert.Equal(replyKey, task.GetProperty("replyIdempotencyKey").GetGuid());
            Assert.NotEqual(attemptId, newAttemptId);
            Assert.Equal(1, task.GetProperty("context").GetProperty("candidateFirstSequence").GetInt64());
            Assert.Equal(1, task.GetProperty("context").GetProperty("candidateLastSequence").GetInt64());
            Assert.True((await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "task",
                taskId, deliveryAttemptId = newAttemptId })).GetProperty("succeeded").GetBoolean());
            Assert.True((await ActivationCallAsync(sdk, "reply_to_mention", new { connectionSessionId = id, taskId,
                deliveryAttemptId = newAttemptId, replyIdempotencyKey = replyKey, content = "Recovered upgraded task" })).GetProperty("succeeded").GetBoolean());
        }
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var persisted = await final.AgentReplyTasks.SingleAsync();
        Assert.Equal(state == "Cancelled" ? AgentReplyTaskState.Cancelled : AgentReplyTaskState.Replied, persisted.State);
        Assert.Equal(state == "Cancelled" ? 1 : 2, await final.MeetingMessages.CountAsync());
        if (replyId is not null) Assert.Equal(replyId, persisted.ReplyMessageId);
        Assert.Equal(0, await final.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }
}
