using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Migrations;
using RoundTableMeeting.Repository.Sqlite.Repositories;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class AgentRecoveryMigrationTests
{
    private const string Previous = "20260904073122_AddAgentCollaboration";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-09T00:00:00Z");

    [Fact]
    public async Task UpgradeBackfillsOwnershipAndExactUnicodeContextWithoutInventingConfirmation()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var connection = await db.AgentConnectionSessions.SingleAsync();
        var cursor = await db.AgentContextCursors.SingleAsync();
        var initial = await db.AgentInitialContextDeliveries.SingleAsync();
        Assert.Equal(seed.ConnectionId, connection.ID);
        Assert.Equal(connection.ID, cursor.CurrentConnectionId);
        Assert.Equal(1, cursor.ConnectionGeneration);
        Assert.Equal(connection.ConnectionGeneration, cursor.ConnectionGeneration);
        Assert.Equal("legacy_v1", cursor.RequiredReliabilityMode);
        Assert.Equal(2, cursor.FirstJoinSnapshotSequence);
        Assert.Equal(2, cursor.DeliveredThroughSequence);
        Assert.Equal(2, cursor.AllocatedThroughSequence);
        Assert.Equal(Now, cursor.ActivityChangedAtUtc);
        Assert.Equal(InitialContextState.Confirmed, initial.State);
        Assert.Equal(Now, initial.ConfirmedAtUtc);
        Assert.Equal(2, initial.DeliveredFirstSequence);
        Assert.Equal(2, initial.DeliveredLastSequence);
        Assert.Equal(1, initial.TruncatedMessageCount);
        Assert.Equal(seed.LastBlockLength, initial.DeliveredCharacterCount);
        Assert.Empty(await db.AgentRecoveryOperations.ToArrayAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [Fact]
    public async Task AmbiguousOldIdentityAbortsBeforeChangingSchemaOrOriginalRows()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentConnectionSessions SELECT {Guid.NewGuid()}, MeetingId, ParticipantId, Transport, 'Disconnected', DeclaredContextCharacterLimit, InitialSnapshotSequence, InitialContextState, InitialContextConfirmedAtUtc, 'duplicate-binding', ConnectedAtUtc, LastActivityAtUtc, {Now}, CreatedAtUtc FROM AgentConnectionSessions");
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains(seed.AgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM AgentConnectionSessions").SingleAsync());
        await AssertOldSchemaAsync(db);
    }

    [Fact]
    public async Task FailureAfterAllRebuildsRollsBackColumnsPointersNewTablesAndMigrationHistory()
    {
        using var fixture = new Fixture();
        await using (var old = fixture.Open()) await SeedOldAsync(old);
        await using (var failing = fixture.Open(injectFailure: true))
            await Assert.ThrowsAsync<SqliteException>(() => failing.Database.MigrateAsync());
        await using var read = fixture.Open();
        await AssertOldSchemaAsync(read);
        Assert.Equal(1, await read.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM AgentConnectionSessions").SingleAsync());
        Assert.Equal(2, await read.MeetingMessages.CountAsync());
        await read.Database.MigrateAsync();
        Assert.Single(await read.AgentInitialContextDeliveries.ToArrayAsync());
    }

    [Fact]
    public async Task CompositeCurrentPointerRejectsWrongGenerationAndInitialSnapshotRejectsNullRange()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AgentContextCursors SET ConnectionGeneration = 2"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AgentInitialContextDeliveries SET DeliveredFirstSequence = NULL"));
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT ConnectionGeneration AS Value FROM AgentContextCursors").SingleAsync());
    }

    [Fact]
    public async Task OwnershipGuardsRejectCrossAgentAttemptsReceiptsAndReassignment()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        var connection = await db.AgentConnectionSessions.SingleAsync();
        var other = MeetingParticipant.CreateAgent(connection.MeetingId, "Other", Now);
        var otherConnection = AgentConnectionSession.Create(connection.MeetingId, other.ID, 250, "other-binding", Now);
        var message = await db.MeetingMessages.SingleAsync(item => item.Sequence == 2);
        var mention = MessageMention.Create(connection.MeetingId, message.ID, seed.AgentId, "Worker", 0, 7);
        var task = AgentReplyTask.Create(Guid.NewGuid(), connection.MeetingId, seed.AgentId, message.ID, 2,
            mention.ID, MessageAuthorKind.Human, message.AuthorParticipantId, 1, Guid.NewGuid(), Now);
        db.AddRange(other, otherConnection, mention, task);
        await db.SaveChangesAsync();
        var wrongAttempt = AgentTaskDeliveryAttempt.Create(task.ID, otherConnection.ID, connection.MeetingId,
            2, 2, 250, 10, false, false, 0, Now);
        db.Add(wrongAttempt);
        var attemptError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("Agent attempt owner mismatch", attemptError.InnerException!.Message);
        db.ChangeTracker.Clear();
        var attempt = AgentTaskDeliveryAttempt.Create(task.ID, connection.ID, connection.MeetingId,
            2, 2, 250, 10, false, false, 0, Now);
        db.Add(attempt);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AgentTaskDeliveryAttempts SET ConnectionSessionId = {otherConnection.ID} WHERE ID = {attempt.ID}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AgentReplyTasks SET AgentParticipantId = {other.ID} WHERE ID = {task.ID}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AgentConnectionSessions SET ParticipantId = {other.ID} WHERE ID = {connection.ID}"));
        db.Add(AgentRecoveryOperation.ForRetry(connection.MeetingId, other.ID, task.ID,
            Guid.NewGuid(), new string('A', 64), 1, Now));
        var receiptError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("Agent retry receipt owner mismatch", receiptError.InnerException!.Message);
        db.ChangeTracker.Clear();
        var receipt = AgentRecoveryOperation.ForRetry(connection.MeetingId, seed.AgentId, task.ID,
            Guid.NewGuid(), new string('A', 64), 1, Now);
        db.Add(receipt);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AgentRecoveryOperations SET ParticipantId = {other.ID} WHERE ID = {receipt.ID}"));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [Fact]
    public async Task LegacyDatabaseCanDowngradeAndUpgradeWithOwnershipTriggers()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync(Previous);
        await AssertOldSchemaAsync(db);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'agent_%_owner_%'").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(seed.ConnectionId, (await db.AgentContextCursors.SingleAsync()).CurrentConnectionId);
        Assert.Equal(6, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'agent_%_owner_%'").SingleAsync());
    }

    [Fact]
    public async Task ActivationAndRenewalRequireCurrentFenceAndCannotReviveAnExpiredLease()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions SET State = 'Activating', ReliabilityMode = 'explicit_ack_v1', RecoveryKeyHash = {new string('A', 64)}
            """);
        var connection = await db.AgentConnectionSessions.AsNoTracking().SingleAsync();
        var fence = new AgentConnectionFence(connection.MeetingId, connection.ParticipantId, connection.ID, 1, 1, connection.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        Assert.False(await repository.ActivateAsync(fence with { BindingVersion = 2 }, Now, 120));
        Assert.False(await repository.ActivateAsync(fence with { ConnectionGeneration = 2 }, Now, 120));
        Assert.True(await repository.ActivateAsync(fence, Now, 120));
        Assert.False(await repository.ActivateAsync(fence, Now.AddSeconds(1), 120));
        Assert.True(await repository.RenewAsync(fence, Now.AddSeconds(10), Now.AddSeconds(400)));
        Assert.True(await repository.RenewAsync(fence, Now.AddSeconds(20), Now.AddSeconds(140)));
        Assert.Equal(Now.AddSeconds(400), (await db.AgentConnectionSessions.AsNoTracking().SingleAsync()).LeaseExpiresAtUtc);
        Assert.False(await repository.RenewAsync(fence with { TransportBinding = "stale" }, Now.AddSeconds(30), Now.AddSeconds(800)));
        Assert.False(await repository.RenewAsync(fence, Now.AddSeconds(400), Now.AddSeconds(800)));
        Assert.False(await repository.DisconnectAsync(fence with { BindingVersion = 2 }, "transport_closed", Now.AddSeconds(400)));
        Assert.True(await repository.DisconnectAsync(fence, "lease_expired", Now.AddSeconds(400)));
        Assert.False(await repository.RenewAsync(fence, Now.AddSeconds(401), Now.AddSeconds(800)));
        Assert.False(await repository.ActivateAsync(fence, Now.AddSeconds(401), 120));
    }

    [Fact]
    public async Task ConditionalActivationRejectsRemovedOwnershipAndDeletedMeeting()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        var connection = await db.AgentConnectionSessions.AsNoTracking().SingleAsync();
        var fence = new AgentConnectionFence(connection.MeetingId, connection.ParticipantId, connection.ID, 1, 1, connection.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentConnectionSessions SET State = 'Activating'");
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentContextCursors SET CurrentConnectionId = NULL, ConnectionGeneration = 0");
        Assert.False(await repository.ActivateAsync(fence, Now, 120));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentContextCursors SET CurrentConnectionId = {connection.ID}, ConnectionGeneration = 1");
        await db.Database.ExecuteSqlRawAsync("UPDATE Meetings SET LifecycleState = 'Deleting'");
        Assert.False(await repository.ActivateAsync(fence, Now, 120));
        Assert.Equal(AgentConnectionState.Activating, (await db.AgentConnectionSessions.AsNoTracking().SingleAsync()).State);
        Assert.True(await repository.DisconnectAsync(fence, "meeting_deleted", Now));
    }

    [Fact]
    public async Task PreparedInitialContextRemainsUnconfirmedAndDowngradeUsesTheNewConfirmationFact()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentConnectionSessions SET InitialContextState = 'Prepared', InitialContextConfirmedAtUtc = NULL");
        await db.Database.MigrateAsync();
        var initial = await db.AgentInitialContextDeliveries.SingleAsync();
        Assert.Equal(InitialContextState.Prepared, initial.State);
        Assert.Null(initial.ConfirmedAtUtc);
        Assert.True(initial.Confirm(Now.AddSeconds(5)));
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync(Previous);
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(Now.AddSeconds(5), (await db.AgentInitialContextDeliveries.SingleAsync()).ConfirmedAtUtc);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM pragma_table_info('AgentConnectionSessions') WHERE name IN ('InitialSnapshotSequence','InitialContextState','InitialContextConfirmedAtUtc')").SingleAsync());
    }

    [Fact]
    public async Task DowngradeWithMissingInitialRecordAbortsWithoutDroppingConnectionOrSchema()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        await db.AgentInitialContextDeliveries.ExecuteDeleteAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync(Previous));
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync());
        Assert.Equal("20260909083700_AddAgentRecoveryState", (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(6, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'agent_%_owner_%'").SingleAsync());
    }

    [Fact]
    public async Task TakeoverForwardReferenceIsAtomicAndReleasesTheActiveSlotBeforeReplacementInsert()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        var old = await db.AgentConnectionSessions.AsNoTracking().SingleAsync();
        var fence = new AgentConnectionFence(old.MeetingId, old.ParticipantId, old.ID, 1, 1, old.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        await using (var failed = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await repository.SupersedeAsync(fence, Guid.NewGuid(), Now));
            await Assert.ThrowsAsync<SqliteException>(() => failed.CommitAsync());
            await failed.RollbackAsync();
        }
        Assert.Equal(AgentConnectionState.Connected, (await repository.FindConnectionAsync(old.ID))!.State);
        var next = AgentConnectionSession.CreateActivating(old.MeetingId, old.ParticipantId, 512, "takeover-binding", 2,
            "explicit_ack_v1", new string('A', 64), Now);
        var operationId = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await repository.SupersedeAsync(fence, next.ID, Now));
            var cursor = await db.AgentContextCursors.SingleAsync();
            cursor.AssignConnection(next, Now);
            db.Add(next);
            db.Add(AgentInitialContextDelivery.Create(next.MeetingId, next.ParticipantId, next.ID,
                new(0, null, null, null, null, 512, 0, false, false, 0, "agent-context-v1"), Now));
            db.Add(AgentRecoveryOperation.ForJoin(old.MeetingId, old.ParticipantId, "WORKER", operationId,
                new string('B', 64), next.ID, 2, 1, next.McpTransportBinding, Now));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(next.ID, (await repository.FindConnectionAsync(old.ID))!.SupersededByConnectionId);
        Assert.Equal(next.ID, (await db.AgentContextCursors.SingleAsync()).CurrentConnectionId);
        Assert.Equal("explicit_ack_v1", (await db.AgentContextCursors.SingleAsync()).RequiredReliabilityMode);
        Assert.Equal(next.ID, (await repository.FindJoinOperationAsync(old.MeetingId, "WORKER", operationId))!.ResultConnectionId);
        Assert.Null(await repository.FindJoinOperationAsync(Guid.NewGuid(), "WORKER", operationId));
        Assert.False(await repository.ActivateAsync(fence, Now, 120));
        Assert.False(await repository.DisconnectAsync(fence, "transport_closed", Now));
        Assert.True(await repository.ActivateAsync(new(old.MeetingId, old.ParticipantId, next.ID, 2, 1, next.McpTransportBinding), Now, 120));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }

    [Fact]
    public async Task ResumeKeepsIdentityBudgetGenerationAndRejectsLateOldBindingWrites()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions SET ReliabilityMode = 'explicit_ack_v1', RecoveryKeyHash = {new string('A', 64)}, LeaseExpiresAtUtc = {Now.AddSeconds(120)}
            """);
        var old = await db.AgentConnectionSessions.AsNoTracking().SingleAsync();
        var fence = new AgentConnectionFence(old.MeetingId, old.ParticipantId, old.ID, 1, 1, old.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        var operationId = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await repository.RebindAsync(fence, "resumed-binding", Now.AddSeconds(1)));
            Assert.False(await repository.RebindAsync(fence, "stale-binding", Now.AddSeconds(1)));
            db.Add(AgentRecoveryOperation.ForResume(old.MeetingId, old.ParticipantId, old.ID, operationId,
                new string('B', 64), 1, 2, "resumed-binding", Now.AddSeconds(1)));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var current = (await repository.FindConnectionAsync(old.ID))!;
        Assert.Equal(1, current.ConnectionGeneration);
        Assert.Equal(2, current.BindingVersion);
        Assert.Equal(old.DeclaredContextCharacterLimit, current.DeclaredContextCharacterLimit);
        Assert.Equal(AgentConnectionState.Activating, current.State);
        Assert.Null(current.LeaseExpiresAtUtc);
        Assert.Equal(2, (await repository.FindResumeOperationAsync(old.MeetingId, old.ID, operationId))!.ResultBindingVersion);
        Assert.Null(await repository.FindResumeOperationAsync(old.MeetingId, Guid.NewGuid(), operationId));
        Assert.False(await repository.DisconnectAsync(fence, "transport_closed", Now.AddSeconds(2)));
        Assert.False(await repository.RenewAsync(fence, Now.AddSeconds(2), Now.AddSeconds(200)));
        Assert.False(await repository.ActivateAsync(fence, Now.AddSeconds(2), 120));
        Assert.True(await repository.ActivateAsync(fence with { BindingVersion = 2, TransportBinding = "resumed-binding" }, Now.AddSeconds(2), 120));
    }

    private static async Task AssertOldSchemaAsync(RoundTableMeetingDbContext db)
    {
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_table_info('AgentConnectionSessions') WHERE name = 'ConnectionGeneration'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM sqlite_master WHERE name IN ('AgentInitialContextDeliveries', 'AgentRecoveryOperations')").SingleAsync());
        Assert.Equal(Previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Fact]
    public async Task RecoveryScanPagesDoNotSkipConnectionsWhenEarlierBatchesAreDisconnected()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync();
        var original = await db.AgentConnectionSessions.SingleAsync();
        for (var index = 0; index < 104; index++)
        {
            var agent = MeetingParticipant.CreateAgent(original.MeetingId, "Batch" + index, Now);
            var connection = AgentConnectionSession.CreateActivating(original.MeetingId, agent.ID, 250, "batch-binding-" + index,
                1, "legacy_v1", null, Now);
            db.AddRange(agent, connection);
        }
        await db.SaveChangesAsync();
        var repository = new AgentRecoveryRepository(db);
        var seen = new HashSet<Guid>();
        Guid? afterId = null;
        while (true)
        {
            var batch = await repository.ListUnsettledBatchAsync(afterId, 17);
            if (batch.Count == 0) break;
            Assert.InRange(batch.Count, 1, 17);
            foreach (var connection in batch) Assert.True(seen.Add(connection.ID));
            var ids = batch.Select(connection => connection.ID).ToArray();
            await db.AgentConnectionSessions.Where(connection => ids.Contains(connection.ID)).ExecuteUpdateAsync(update => update
                .SetProperty(connection => connection.State, AgentConnectionState.Disconnected)
                .SetProperty(connection => connection.DisconnectedAtUtc, Now));
            afterId = batch[^1].ID;
        }
        Assert.Equal(105, seen.Count);
        Assert.Empty(await repository.ListUnsettledConnectionsAsync());
    }

    [Theory]
    [InlineData("context_invalid")]
    [InlineData("unrecognized_legacy_failure")]
    public async Task LegacyFailedMigrationPreservesCauseAndRequiresExplicitRetry(string cause)
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        var message = await db.MeetingMessages.SingleAsync(item => item.Sequence == 2);
        var mention = MessageMention.Create(message.MeetingId, message.ID, seed.AgentId, "Worker", 0, 7);
        db.Add(mention);
        await db.SaveChangesAsync();
        var taskId = Guid.NewGuid();
        var key = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentReplyTasks (ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,
                SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive, CandidateToSequenceInclusive,
                ReplyIdempotencyKey, State, FailureCode, FailedAtUtc, CreatedAtUtc)
            VALUES ({taskId}, {message.MeetingId}, {seed.AgentId}, {message.ID}, 2, {mention.ID}, 'Human', {message.AuthorParticipantId},
                1, 2, {key}, 'Failed', {cause}, {Now}, {Now})
            """);
        await db.Database.MigrateAsync();
        var task = await db.AgentReplyTasks.SingleAsync();
        Assert.Equal(taskId, task.ID);
        Assert.Equal(key, task.ReplyIdempotencyKey);
        Assert.Equal(cause, task.FailureCode);
        Assert.Equal(Now, task.FailedAtUtc);
        Assert.True(task.RetryExhausted);
        Assert.Equal(AgentTaskStopReason.PermanentFailure, task.StopReason);
        Assert.Null(task.NextRetryAtUtc);
        Assert.Null(task.CurrentAttemptId);
        Assert.False(task.InitializeLegacyFailureDecision());
        task.BeginExplicitRetry(Now.AddSeconds(1));
        Assert.Equal(1, task.RetryCycle);
        Assert.Equal(0, task.AutomaticRetryCount);
        Assert.False(task.RetryExhausted);
    }

    private static async Task<(Guid ConnectionId, Guid AgentId, int LastBlockLength)> SeedOldAsync(RoundTableMeetingDbContext db)
    {
        await db.Database.MigrateAsync(Previous);
        var user = UserAccount.Create("owner", "Owner");
        user.InitializePasswordHash("test-hash");
        var meeting = Meeting.Create("Migration", null, user.ID, Now);
        var human = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Alice😀", Now);
        var agent = MeetingParticipant.CreateAgent(meeting.ID, "Worker", Now);
        var first = MeetingMessage.CreateParticipant(meeting.ID, 1, human.ID, MessageAuthorKind.Human, human.DisplayName,
            "Earlier history " + new string('x', 300), Guid.NewGuid(), Now);
        var last = MeetingMessage.CreateParticipant(meeting.ID, 2, human.ID, MessageAuthorKind.Human, human.DisplayName,
            "中文😀\\value\r\nnext", Guid.NewGuid(), Now);
        db.AddRange(user, meeting, human, agent, first, last);
        await db.SaveChangesAsync();
        var connectionId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentConnectionSessions
            (ID,MeetingId,ParticipantId,Transport,State,DeclaredContextCharacterLimit,InitialSnapshotSequence,InitialContextState,
             InitialContextConfirmedAtUtc,McpTransportBinding,ConnectedAtUtc,LastActivityAtUtc,CreatedAtUtc)
            VALUES ({connectionId},{meeting.ID},{agent.ID},'mcp-streamable-http','Connected',250,2,'Confirmed',
             {Now},'legacy-binding',{Now},{Now},{Now});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentContextCursors (ID,MeetingId,ParticipantId,DeliveredThroughSequence,AllocatedThroughSequence,UpdatedAtUtc)
            VALUES ({Guid.NewGuid()},{meeting.ID},{agent.ID},2,2,{Now});
            """);
        var expected = $"messageId={last.ID:D}\nsequence=2\nauthorKind=human\nauthorDisplayName={Escape(last.AuthorDisplayName)}\ncontent={Escape(last.Content)}\n";
        return (connectionId, agent.ID, expected.Length);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "RoundTableMeetingRecoveryTests", Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(directory);
        public RoundTableMeetingDbContext Open(bool injectFailure = false)
        {
            var options = new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "recovery.db")};Foreign Keys=True;Pooling=False");
            if (injectFailure) options.ReplaceService<IMigrationsAssembly, FaultedMigrationsAssembly>();
            return new(options.Options);
        }
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

#pragma warning disable EF1001
    public sealed class FaultedMigrationsAssembly(ICurrentDbContext currentContext, IDbContextOptions options,
        IMigrationsIdGenerator generator, IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
        : MigrationsAssembly(currentContext, options, generator, logger)
    {
        public override Migration CreateMigration(TypeInfo migrationClass, string activeProvider) =>
            migrationClass.AsType() == typeof(AddAgentRecoveryState)
                ? new FailAfterRecoveryMigration { ActiveProvider = activeProvider }
                : base.CreateMigration(migrationClass, activeProvider);
    }
#pragma warning restore EF1001

    private sealed class FailAfterRecoveryMigration : AddAgentRecoveryState
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            base.Up(migrationBuilder);
            migrationBuilder.Sql("INSERT INTO intentionally_missing_recovery_table VALUES (1)");
        }
    }
}
