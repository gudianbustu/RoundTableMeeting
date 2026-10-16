using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class AgentRecoveryMigrationTests
{
    public static IEnumerable<object?[]> LegacyStates()
    {
        (string Task, string? Attempt)[] states =
        [
            ("Queued", null), ("Delivered", "Prepared"), ("Delivered", "Delivered"),
            ("Processing", "Delivered"), ("Replied", "Delivered"),
            ("Failed", null), ("Failed", "Prepared"), ("Failed", "Delivered"), ("Failed", "Abandoned"),
            ("Cancelled", null), ("Cancelled", "Delivered"), ("Cancelled", "Abandoned"),
        ];
        foreach (var state in states)
        foreach (var connected in new[] { false, true })
        foreach (var confirmed in new[] { false, true })
            yield return [state.Task, state.Attempt, connected, confirmed];
    }

    [Theory]
    [MemberData(nameof(LegacyStates))]
    public async Task LegacyTaskMatrixPreservesFactsAndBackfillsOnlyRecoveryMetadata(
        string taskState, string? attemptState, bool connected, bool confirmed)
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions SET State = {(connected ? "Connected" : "Disconnected")},
                DisconnectedAtUtc = {(connected ? (DateTimeOffset?)null : Now)},
                InitialContextState = {(confirmed ? "Confirmed" : "Prepared")},
                InitialContextConfirmedAtUtc = {(confirmed ? Now : (DateTimeOffset?)null)}
            """);
        var taskId = await SeedLegacyTaskAsync(db, seed.AgentId, taskState);
        Guid? currentId = null;
        if (attemptState is not null)
        {
            // Preserve earlier abandoned history while selecting the uniquely latest attempt.
            await SeedLegacyAttemptAsync(db, taskId, seed.ConnectionId, "Abandoned", Now.AddSeconds(-1));
            currentId = await SeedLegacyAttemptAsync(db, taskId, seed.ConnectionId, attemptState, Now);
        }
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var connection = await db.AgentConnectionSessions.SingleAsync();
        var cursor = await db.AgentContextCursors.SingleAsync();
        var initial = await db.AgentInitialContextDeliveries.SingleAsync();
        var task = await db.AgentReplyTasks.SingleAsync();
        Assert.Equal(seed.ConnectionId, connection.ID);
        Assert.Equal(connected ? AgentConnectionState.Connected : AgentConnectionState.Disconnected, connection.State);
        Assert.Equal("legacy_v1", connection.ReliabilityMode);
        Assert.Null(connection.RecoveryKeyHash);
        Assert.Null(connection.LeaseExpiresAtUtc);
        Assert.Equal(1, connection.BindingVersion);
        Assert.Equal(seed.ConnectionId, cursor.CurrentConnectionId);
        Assert.Equal(2, cursor.FirstJoinSnapshotSequence);
        Assert.Equal(2, cursor.AllocatedThroughSequence);
        Assert.Equal(2, cursor.DeliveredThroughSequence);
        Assert.Equal(confirmed ? InitialContextState.Confirmed : InitialContextState.Prepared, initial.State);
        Assert.Equal(confirmed ? Now : (DateTimeOffset?)null, initial.ConfirmedAtUtc);
        Assert.Equal(taskState, task.State.ToString());
        Assert.Equal(taskId, task.ID);
        Assert.Equal(taskId, task.ReplyIdempotencyKey);
        Assert.Equal(1, task.CandidateFromSequenceExclusive);
        Assert.Equal(2, task.CandidateToSequenceInclusive);
        Assert.Equal(currentId, task.CurrentAttemptId);
        Assert.Equal(0, task.RetryCycle);
        Assert.Equal(0, task.AutomaticRetryCount);
        Assert.Equal(taskState == "Failed", task.RetryExhausted);
        Assert.Null(task.NextRetryAtUtc);
        Assert.Equal(taskState == "Replied", task.ReplyMessageId.HasValue);
        Assert.Equal(taskState == "Failed" ? Now : (DateTimeOffset?)null, task.FailedAtUtc);
        Assert.Equal(taskState == "Failed" ? "context_invalid" : null, task.FailureCode);
        Assert.Equal(taskState == "Cancelled" ? Now : (DateTimeOffset?)null, task.CancelledAtUtc);
        Assert.Equal(taskState == "Replied" ? Now : (DateTimeOffset?)null, task.RepliedAtUtc);
        var attempts = await db.AgentTaskDeliveryAttempts.OrderBy(item => item.ID).ToArrayAsync();
        Assert.Equal(attemptState is null ? 0 : 2, attempts.Length);
        foreach (var attempt in attempts)
        {
            Assert.Equal(attempt.ID == currentId ? attemptState : "Abandoned", attempt.State.ToString());
            Assert.Null(attempt.ConfirmedAtUtc);
            Assert.Null(attempt.ConfirmationDeadlineUtc);
            Assert.Null(attempt.FailureDecision);
            Assert.Null(attempt.ReplacementAttemptId);
            Assert.Equal("agent-context-v1", attempt.ContextFormatVersion);
            Assert.Equal(1, attempt.ConnectionGeneration);
            Assert.Equal(1, attempt.BindingVersion);
            Assert.Equal(0, attempt.RetryCycle);
            Assert.Equal(2, attempt.CandidateFirstSequence);
            Assert.Equal(2, attempt.CandidateLastSequence);
            Assert.Equal(250, attempt.DeclaredContextCharacterLimit);
            Assert.Equal(10, attempt.DeliveredCharacterCount);
            Assert.Equal(attempt.ID == currentId && attemptState == "Delivered" ? 2L : null, attempt.DeliveredFirstSequence);
        }
        Assert.Equal(taskState == "Replied" ? 3 : 2, await db.MeetingMessages.CountAsync());
        Assert.Empty(await db.AgentRecoveryOperations.ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [Theory]
    [InlineData("tied_attempts")]
    [InlineData("missing_delivered_attempt")]
    [InlineData("missing_processing_attempt")]
    [InlineData("missing_replied_attempt")]
    [InlineData("cross_agent_attempt")]
    public async Task InvalidLegacyAttemptMatrixAbortsAndLeavesOriginalSchemaReadable(string corruption)
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        var state = corruption switch
        {
            "missing_processing_attempt" => "Processing",
            "missing_replied_attempt" => "Replied",
            _ => "Delivered",
        };
        var taskId = await SeedLegacyTaskAsync(db, seed.AgentId, state);
        Guid diagnosticId = taskId;
        if (corruption == "tied_attempts")
        {
            await SeedLegacyAttemptAsync(db, taskId, seed.ConnectionId, "Delivered", Now);
            await SeedLegacyAttemptAsync(db, taskId, seed.ConnectionId, "Prepared", Now);
        }
        if (corruption == "cross_agent_attempt")
        {
            var other = MeetingParticipant.CreateAgent((await db.Meetings.SingleAsync()).ID, "Other", Now);
            await LegacyMeetingParticipantSeed.InsertAsync(db, other);
            var connectionId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AgentConnectionSessions SELECT {connectionId}, MeetingId, {other.ID}, Transport,
                    State, DeclaredContextCharacterLimit, InitialSnapshotSequence, InitialContextState,
                    InitialContextConfirmedAtUtc, 'other-legacy-binding', ConnectedAtUtc, LastActivityAtUtc,
                    DisconnectedAtUtc, CreatedAtUtc FROM AgentConnectionSessions WHERE ID = {seed.ConnectionId}
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AgentContextCursors SELECT {Guid.NewGuid()}, MeetingId, {other.ID},
                    DeliveredThroughSequence, AllocatedThroughSequence, UpdatedAtUtc FROM AgentContextCursors
                """);
            diagnosticId = await SeedLegacyAttemptAsync(db, taskId, connectionId, "Delivered", Now);
        }
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains(diagnosticId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        await AssertOldSchemaAsync(db);
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM AgentReplyTasks").SingleAsync());
        Assert.Equal(state == "Replied" ? 3 : 2, await db.MeetingMessages.CountAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    private static async Task<Guid> SeedLegacyTaskAsync(RoundTableMeetingDbContext db, Guid agentId, string state)
    {
        var trigger = await LegacyMessageSeed.Query(db).SingleAsync(item => item.Sequence == 2);
        var mention = MessageMention.Create(trigger.MeetingId, trigger.ID, agentId, "Worker", 0, 7);
        Guid? replyId = null;
        db.Add(mention);
        if (state == "Replied")
        {
            var reply = MeetingMessage.CreateParticipant(trigger.MeetingId, 3, agentId, MessageAuthorKind.Agent,
                "Worker", "Legacy reply", Guid.NewGuid(), Now);
            replyId = reply.ID;
            await LegacyMessageSeed.InsertAsync(db, reply);
        }
        await db.SaveChangesAsync();
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentReplyTasks (ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,
                SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive, CandidateToSequenceInclusive,
                ReplyIdempotencyKey, State, ReplyMessageId, DeliveredAtUtc, ProcessingAtUtc, RepliedAtUtc,
                FailureCode, FailedAtUtc, CancelledAtUtc, CreatedAtUtc)
            VALUES ({id}, {trigger.MeetingId}, {agentId}, {trigger.ID}, 2, {mention.ID}, 'Human', {trigger.AuthorParticipantId},
                1, 2, {id}, {state}, {replyId}, {(state != "Queued" ? Now : (DateTimeOffset?)null)},
                {(state is "Processing" or "Replied" ? Now : (DateTimeOffset?)null)},
                {(state == "Replied" ? Now : (DateTimeOffset?)null)}, {(state == "Failed" ? "context_invalid" : null)},
                {(state == "Failed" ? Now : (DateTimeOffset?)null)}, {(state == "Cancelled" ? Now : (DateTimeOffset?)null)}, {Now})
            """);
        return id;
    }

    private static async Task<Guid> SeedLegacyAttemptAsync(RoundTableMeetingDbContext db, Guid taskId,
        Guid connectionId, string state, DateTimeOffset created)
    {
        var id = Guid.NewGuid();
        var meetingId = (await db.Meetings.SingleAsync()).ID;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentTaskDeliveryAttempts (ID, TaskId, ConnectionSessionId, MeetingId, CandidateFirstSequence,
                CandidateLastSequence, DeclaredContextCharacterLimit, DeliveredCharacterCount, Truncated, OverLimit,
                TruncatedMessageCount, State, CreatedAtUtc, DeliveredFirstSequence, DeliveredLastSequence, DeliveredAtUtc)
            VALUES ({id}, {taskId}, {connectionId}, {meetingId}, 2, 2, 250, 10, 0, 0, 0, {state}, {created},
                {(state == "Delivered" ? 2L : (long?)null)}, {(state == "Delivered" ? 2L : (long?)null)},
                {(state == "Delivered" ? created : (DateTimeOffset?)null)})
            """);
        return id;
    }
}
