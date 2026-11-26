using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Fact]
    public async Task UpgradePreservesMultiTargetTasksAndConfirmedFailureReceipts()
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        var peer = MeetingParticipant.CreateAgent(seed.Meeting.ID, "B", Now);
        var mention = MessageMention.Create(seed.Meeting.ID, seed.Message.ID, peer.ID, "B", 3, 2);
        await LegacyMeetingParticipantSeed.InsertAsync(db, peer);
        db.Add(mention);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE MeetingMessages SET Content = {"@A @B"} WHERE ID = {seed.Message.ID}", cancellationToken: TestContext.Current.CancellationToken);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var peerTaskId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentReplyTasks (ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,
             SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive, CandidateToSequenceInclusive, ReplyIdempotencyKey,
             State, CreatedAtUtc, AutomaticRetryCount, RetryCycle, RetryExhausted)
            VALUES ({peerTaskId}, {seed.Meeting.ID}, {peer.ID}, {seed.Message.ID}, 1, {mention.ID}, 'Human', {seed.Human.ID},
             0, 1, {Guid.NewGuid()}, 'Queued', {Now}, 0, 0, 0)
            """, cancellationToken: TestContext.Current.CancellationToken);
        var connection = AgentConnectionSession.CreateActivating(seed.Meeting.ID, seed.Agent.ID, 12000,
            "legacy-fixture-binding", 1, "explicit_ack_v1", new string('B', 64), Now);
        Assert.True(connection.Activate(1, Now, 120));
        db.Add(connection);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var attempt = AgentTaskDeliveryAttempt.Create(seed.TaskId, connection.ID, seed.Meeting.ID, 1, 1, 12000, 5, false, false, 0, Now);
        attempt.MarkDelivered(1, 1, Now);
        db.Add(attempt);
        db.Add(AgentRecoveryOperation.ForRetry(seed.Meeting.ID, seed.Agent.ID, seed.TaskId, Guid.NewGuid(), new string('A', 64), 2, Now));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var failure = System.Text.Json.JsonSerializer.Serialize(new AgentReportedFailureDecision(2, true, AgentTaskStopReason.AttemptsExhausted, null));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentTaskDeliveryAttempts SET State = 'Confirmed', ConfirmationDeadlineUtc = {Now.AddMinutes(1)},
             ConfirmedAtUtc = {Now}, InvalidatedAtUtc = {Now.AddSeconds(1)}, InvalidationReason = 'agent_transient_failure',
             RetryCycle = 2, ReportedFailureCode = 'agent_transient_failure', FailureDecision = {failure}
            WHERE ID = {attempt.ID}
            """, cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentReplyTasks SET CurrentAttemptId = {attempt.ID}, State = 'Failed', DeliveredAtUtc = {Now},
             ProcessingAtUtc = {Now}, FailedAtUtc = {Now.AddSeconds(1)}, FailureCode = 'agent_transient_failure',
             RetryCycle = 2, AutomaticRetryCount = 3, RetryExhausted = 1, StopReason = 'AttemptsExhausted'
            WHERE ID = {seed.TaskId}
            """, cancellationToken: TestContext.Current.CancellationToken);
        var tables = new[] { "AgentReplyTasks", "AgentTaskDeliveryAttempts", "AgentRecoveryOperations", "AgentConnectionSessions", "MessageMentions" };
        var before = new Dictionary<string, string[]>();
        foreach (var table in tables) before[table] = await SnapshotLegacyTableAsync(db, table);
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var table in tables) Assert.Equal(before[table], await SnapshotLegacyTableAsync(db, table));
        var tasks = await db.AgentReplyTasks.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new AgentReportedFailureDecision(2, true, AgentTaskStopReason.AttemptsExhausted, null),
            (await db.AgentTaskDeliveryAttempts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).FailureDecision);
        Assert.Equal(2, tasks.Length);
        Assert.All(tasks, task => { Assert.Equal(seed.Message.ID, task.ChainId); Assert.Null(task.ParentTaskId); });
        Assert.Equal(2, tasks.Select(task => task.AgentParticipantId).Distinct().Count());
        Assert.Equal(0, await ForeignKeyErrors(db));
    }

    private static async Task<string[]> SnapshotLegacyTableAsync(RoundTableMeetingDbContext db, string table)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY ID";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var columns = new SortedDictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (table == "AgentReplyTasks" && name is "ChainId" or "ParentTaskId") continue;
                columns[name] = reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            }
            rows.Add(System.Text.Json.JsonSerializer.Serialize(columns));
        }
        return rows.ToArray();
    }
}
