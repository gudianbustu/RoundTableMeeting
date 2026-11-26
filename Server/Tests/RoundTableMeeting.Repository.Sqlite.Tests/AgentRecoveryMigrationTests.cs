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
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var connection = await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var cursor = await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var initial = await db.AgentInitialContextDeliveries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        Assert.Empty(await db.AgentRecoveryOperations.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AmbiguousOldIdentityAbortsBeforeChangingSchemaOrOriginalRows()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentConnectionSessions SELECT {Guid.NewGuid()}, MeetingId, ParticipantId, Transport, 'Disconnected', DeclaredContextCharacterLimit, InitialSnapshotSequence, InitialContextState, InitialContextConfirmedAtUtc, 'duplicate-binding', ConnectedAtUtc, LastActivityAtUtc, {Now}, CreatedAtUtc FROM AgentConnectionSessions", cancellationToken: TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains(seed.AgentId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM AgentConnectionSessions").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        await AssertOldSchemaAsync(db);
    }

    [Fact]
    public async Task FailureAfterAllRebuildsRollsBackColumnsPointersNewTablesAndMigrationHistory()
    {
        using var fixture = new Fixture();
        await using (var old = fixture.Open()) await SeedOldAsync(old);
        await using (var failing = fixture.Open(injectFailure: true))
            await Assert.ThrowsAsync<SqliteException>(() => failing.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken));
        await using var read = fixture.Open();
        await AssertOldSchemaAsync(read);
        Assert.Equal(1, await read.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM AgentConnectionSessions").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await read.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        await read.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(await read.AgentInitialContextDeliveries.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompositeCurrentPointerRejectsWrongGenerationAndInitialSnapshotRejectsNullRange()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AgentContextCursors SET ConnectionGeneration = 2", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AgentInitialContextDeliveries SET DeliveredFirstSequence = NULL", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT ConnectionGeneration AS Value FROM AgentContextCursors").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OwnershipGuardsRejectCrossAgentAttemptsReceiptsAndReassignment()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var connection = await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var other = MeetingParticipant.CreateAgent(connection.MeetingId, "Other", Now);
        var otherConnection = AgentConnectionSession.Create(connection.MeetingId, other.ID, 250, "other-binding", Now);
        var message = await db.MeetingMessages.SingleAsync(item => item.Sequence == 2, cancellationToken: TestContext.Current.CancellationToken);
        var mention = MessageMention.Create(connection.MeetingId, message.ID, seed.AgentId, "Worker", 0, 7);
        var task = AgentReplyTask.Create(Guid.NewGuid(), connection.MeetingId, seed.AgentId, message.ID, 2,
            mention.ID, MessageAuthorKind.Human, message.AuthorParticipantId, 1, Guid.NewGuid(), Now);
        db.AddRange(other, otherConnection, mention, task);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var wrongAttempt = AgentTaskDeliveryAttempt.Create(task.ID, otherConnection.ID, connection.MeetingId,
            2, 2, 250, 10, false, false, 0, Now);
        db.Add(wrongAttempt);
        var attemptError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Agent attempt owner mismatch", attemptError.InnerException!.Message);
        db.ChangeTracker.Clear();
        var attempt = AgentTaskDeliveryAttempt.Create(task.ID, connection.ID, connection.MeetingId,
            2, 2, 250, 10, false, false, 0, Now);
        db.Add(attempt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentTaskDeliveryAttempts SET ConnectionSessionId = {otherConnection.ID} WHERE ID = {attempt.ID}", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET AgentParticipantId = {other.ID} WHERE ID = {task.ID}", cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentConnectionSessions SET ParticipantId = {other.ID} WHERE ID = {connection.ID}", cancellationToken: TestContext.Current.CancellationToken));
        db.Add(AgentRecoveryOperation.ForRetry(connection.MeetingId, other.ID, task.ID,
            Guid.NewGuid(), new string('A', 64), 1, Now));
        var receiptError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Agent retry receipt owner mismatch", receiptError.InnerException!.Message);
        db.ChangeTracker.Clear();
        var receipt = AgentRecoveryOperation.ForRetry(connection.MeetingId, seed.AgentId, task.ID,
            Guid.NewGuid(), new string('A', 64), 1, Now);
        db.Add(receipt);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRecoveryOperations SET ParticipantId = {other.ID} WHERE ID = {receipt.ID}", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LegacyDatabaseCanDowngradeAndUpgradeWithOwnershipTriggers()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(Previous, cancellationToken: TestContext.Current.CancellationToken);
        await AssertOldSchemaAsync(db);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'agent_%_owner_%'").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(seed.ConnectionId, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).CurrentConnectionId);
        Assert.Equal(6, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'agent_%_owner_%'").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActivationAndRenewalRequireCurrentFenceAndCannotReviveAnExpiredLease()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions SET State = 'Activating', ReliabilityMode = 'explicit_ack_v1', RecoveryKeyHash = {new string('A', 64)}
            """, cancellationToken: TestContext.Current.CancellationToken);
        var connection = await db.AgentConnectionSessions.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fence = new AgentConnectionFence(connection.MeetingId, connection.ParticipantId, connection.ID, 1, 1, connection.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        Assert.False(await repository.ActivateAsync(fence with { BindingVersion = 2 }, Now, 120, TestContext.Current.CancellationToken));
        Assert.False(await repository.ActivateAsync(fence with { ConnectionGeneration = 2 }, Now, 120, TestContext.Current.CancellationToken));
        Assert.True(await repository.ActivateAsync(fence, Now, 120, TestContext.Current.CancellationToken));
        Assert.False(await repository.ActivateAsync(fence, Now.AddSeconds(1), 120, TestContext.Current.CancellationToken));
        Assert.True(await repository.RenewAsync(fence, Now.AddSeconds(10), Now.AddSeconds(400), TestContext.Current.CancellationToken));
        Assert.True(await repository.RenewAsync(fence, Now.AddSeconds(20), Now.AddSeconds(140), TestContext.Current.CancellationToken));
        Assert.Equal(Now.AddSeconds(400), (await db.AgentConnectionSessions.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).LeaseExpiresAtUtc);
        Assert.False(await repository.RenewAsync(fence with { TransportBinding = "stale" }, Now.AddSeconds(30), Now.AddSeconds(800), TestContext.Current.CancellationToken));
        Assert.False(await repository.RenewAsync(fence, Now.AddSeconds(400), Now.AddSeconds(800), TestContext.Current.CancellationToken));
        Assert.False(await repository.DisconnectAsync(fence with { BindingVersion = 2 }, "transport_closed", Now.AddSeconds(400), TestContext.Current.CancellationToken));
        Assert.True(await repository.DisconnectAsync(fence, "lease_expired", Now.AddSeconds(400), TestContext.Current.CancellationToken));
        Assert.False(await repository.RenewAsync(fence, Now.AddSeconds(401), Now.AddSeconds(800), TestContext.Current.CancellationToken));
        Assert.False(await repository.ActivateAsync(fence, Now.AddSeconds(401), 120, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConditionalActivationRejectsRemovedOwnershipAndDeletedMeeting()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var connection = await db.AgentConnectionSessions.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fence = new AgentConnectionFence(connection.MeetingId, connection.ParticipantId, connection.ID, 1, 1, connection.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentConnectionSessions SET State = 'Activating'", cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentContextCursors SET CurrentConnectionId = NULL, ConnectionGeneration = 0", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(await repository.ActivateAsync(fence, Now, 120, TestContext.Current.CancellationToken));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentContextCursors SET CurrentConnectionId = {connection.ID}, ConnectionGeneration = 1", cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlRawAsync("UPDATE Meetings SET LifecycleState = 'Deleting'", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(await repository.ActivateAsync(fence, Now, 120, TestContext.Current.CancellationToken));
        Assert.Equal(AgentConnectionState.Activating, (await db.AgentConnectionSessions.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.True(await repository.DisconnectAsync(fence, "meeting_deleted", Now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreparedInitialContextRemainsUnconfirmedAndDowngradeUsesTheNewConfirmationFact()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.ExecuteSqlRawAsync("UPDATE AgentConnectionSessions SET InitialContextState = 'Prepared', InitialContextConfirmedAtUtc = NULL", cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var initial = await db.AgentInitialContextDeliveries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(InitialContextState.Prepared, initial.State);
        Assert.Null(initial.ConfirmedAtUtc);
        Assert.True(initial.Confirm(Now.AddSeconds(5)));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(Previous, cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        Assert.Equal(Now.AddSeconds(5), (await db.AgentInitialContextDeliveries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ConfirmedAtUtc);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM pragma_table_info('AgentConnectionSessions') WHERE name IN ('InitialSnapshotSequence','InitialContextState','InitialContextConfirmedAtUtc')").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DowngradeWithMissingInitialRecordAbortsWithoutDroppingConnectionOrSchema()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await db.AgentInitialContextDeliveries.ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync(Previous, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("20260909083700_AddAgentRecoveryState", (await db.Database.GetAppliedMigrationsAsync(cancellationToken: TestContext.Current.CancellationToken)).Last());
        Assert.Equal(6, await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS Value FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'agent_%_owner_%'").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TakeoverForwardReferenceIsAtomicAndReleasesTheActiveSlotBeforeReplacementInsert()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var old = await db.AgentConnectionSessions.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fence = new AgentConnectionFence(old.MeetingId, old.ParticipantId, old.ID, 1, 1, old.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        await using (var failed = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await repository.SupersedeAsync(fence, Guid.NewGuid(), Now, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SqliteException>(() => failed.CommitAsync(TestContext.Current.CancellationToken));
            await failed.RollbackAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(AgentConnectionState.Connected, (await repository.FindConnectionAsync(old.ID, TestContext.Current.CancellationToken))!.State);
        var next = AgentConnectionSession.CreateActivating(old.MeetingId, old.ParticipantId, 512, "takeover-binding", 2,
            "explicit_ack_v1", new string('A', 64), Now);
        var operationId = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await repository.SupersedeAsync(fence, next.ID, Now, TestContext.Current.CancellationToken));
            var cursor = await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            cursor.AssignConnection(next, Now);
            db.Add(next);
            db.Add(AgentInitialContextDelivery.Create(next.MeetingId, next.ParticipantId, next.ID,
                new(0, null, null, null, null, 512, 0, false, false, 0, "agent-context-v1"), Now));
            db.Add(AgentRecoveryOperation.ForJoin(old.MeetingId, old.ParticipantId, "WORKER", operationId,
                new string('B', 64), next.ID, 2, 1, next.McpTransportBinding, Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(next.ID, (await repository.FindConnectionAsync(old.ID, TestContext.Current.CancellationToken))!.SupersededByConnectionId);
        Assert.Equal(next.ID, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).CurrentConnectionId);
        Assert.Equal("explicit_ack_v1", (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RequiredReliabilityMode);
        Assert.Equal(next.ID, (await repository.FindJoinOperationAsync(old.MeetingId, "WORKER", operationId, TestContext.Current.CancellationToken))!.ResultConnectionId);
        Assert.Null(await repository.FindJoinOperationAsync(Guid.NewGuid(), "WORKER", operationId, TestContext.Current.CancellationToken));
        Assert.False(await repository.ActivateAsync(fence, Now, 120, TestContext.Current.CancellationToken));
        Assert.False(await repository.DisconnectAsync(fence, "transport_closed", Now, TestContext.Current.CancellationToken));
        Assert.True(await repository.ActivateAsync(new(old.MeetingId, old.ParticipantId, next.ID, 2, 1, next.McpTransportBinding), Now, 120, TestContext.Current.CancellationToken));
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResumeKeepsIdentityBudgetGenerationAndRejectsLateOldBindingWrites()
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        await SeedOldAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentConnectionSessions SET ReliabilityMode = 'explicit_ack_v1', RecoveryKeyHash = {new string('A', 64)}, LeaseExpiresAtUtc = {Now.AddSeconds(120)}
            """, cancellationToken: TestContext.Current.CancellationToken);
        var old = await db.AgentConnectionSessions.AsNoTracking().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var fence = new AgentConnectionFence(old.MeetingId, old.ParticipantId, old.ID, 1, 1, old.McpTransportBinding);
        var repository = new AgentRecoveryRepository(db);
        var operationId = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(await repository.RebindAsync(fence, "resumed-binding", Now.AddSeconds(1), TestContext.Current.CancellationToken));
            Assert.False(await repository.RebindAsync(fence, "stale-binding", Now.AddSeconds(1), TestContext.Current.CancellationToken));
            db.Add(AgentRecoveryOperation.ForResume(old.MeetingId, old.ParticipantId, old.ID, operationId,
                new string('B', 64), 1, 2, "resumed-binding", Now.AddSeconds(1)));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }
        var current = (await repository.FindConnectionAsync(old.ID, TestContext.Current.CancellationToken))!;
        Assert.Equal(1, current.ConnectionGeneration);
        Assert.Equal(2, current.BindingVersion);
        Assert.Equal(old.DeclaredContextCharacterLimit, current.DeclaredContextCharacterLimit);
        Assert.Equal(AgentConnectionState.Activating, current.State);
        Assert.Null(current.LeaseExpiresAtUtc);
        Assert.Equal(2, (await repository.FindResumeOperationAsync(old.MeetingId, old.ID, operationId, TestContext.Current.CancellationToken))!.ResultBindingVersion);
        Assert.Null(await repository.FindResumeOperationAsync(old.MeetingId, Guid.NewGuid(), operationId, TestContext.Current.CancellationToken));
        Assert.False(await repository.DisconnectAsync(fence, "transport_closed", Now.AddSeconds(2), TestContext.Current.CancellationToken));
        Assert.False(await repository.RenewAsync(fence, Now.AddSeconds(2), Now.AddSeconds(200), TestContext.Current.CancellationToken));
        Assert.False(await repository.ActivateAsync(fence, Now.AddSeconds(2), 120, TestContext.Current.CancellationToken));
        Assert.True(await repository.ActivateAsync(fence with { BindingVersion = 2, TransportBinding = "resumed-binding" }, Now.AddSeconds(2), 120, TestContext.Current.CancellationToken));
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
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var original = await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        for (var index = 0; index < 104; index++)
        {
            var agent = MeetingParticipant.CreateAgent(original.MeetingId, "Batch" + index, Now);
            var connection = AgentConnectionSession.CreateActivating(original.MeetingId, agent.ID, 250, "batch-binding-" + index,
                1, "legacy_v1", null, Now);
            db.AddRange(agent, connection);
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentRecoveryRepository(db);
        var seen = new HashSet<Guid>();
        Guid? afterId = null;
        while (true)
        {
            var batch = await repository.ListUnsettledBatchAsync(afterId, 17, TestContext.Current.CancellationToken);
            if (batch.Count == 0) break;
            Assert.InRange(batch.Count, 1, 17);
            foreach (var connection in batch) Assert.True(seen.Add(connection.ID));
            var ids = batch.Select(connection => connection.ID).ToArray();
            await db.AgentConnectionSessions.Where(connection => ids.Contains(connection.ID)).ExecuteUpdateAsync(update => update
                .SetProperty(connection => connection.State, AgentConnectionState.Disconnected)
                .SetProperty(connection => connection.DisconnectedAtUtc, Now), cancellationToken: TestContext.Current.CancellationToken);
            afterId = batch[^1].ID;
        }
        Assert.Equal(105, seen.Count);
        Assert.Empty(await repository.ListUnsettledConnectionsAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("context_invalid")]
    [InlineData("unrecognized_legacy_failure")]
    public async Task LegacyFailedMigrationPreservesCauseAndRequiresExplicitRetry(string cause)
    {
        using var fixture = new Fixture();
        await using var db = fixture.Open();
        var seed = await SeedOldAsync(db);
        var message = await LegacyMessageSeed.Query(db).SingleAsync(item => item.Sequence == 2, cancellationToken: TestContext.Current.CancellationToken);
        var mention = MessageMention.Create(message.MeetingId, message.ID, seed.AgentId, "Worker", 0, 7);
        db.Add(mention);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var taskId = Guid.NewGuid();
        var key = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentReplyTasks (ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,
                SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive, CandidateToSequenceInclusive,
                ReplyIdempotencyKey, State, FailureCode, FailedAtUtc, CreatedAtUtc)
            VALUES ({taskId}, {message.MeetingId}, {seed.AgentId}, {message.ID}, 2, {mention.ID}, 'Human', {message.AuthorParticipantId},
                1, 2, {key}, 'Failed', {cause}, {Now}, {Now})
            """, cancellationToken: TestContext.Current.CancellationToken);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO UserAccounts (ID, Username, NormalizedUsername, Nickname, PasswordHash) VALUES ({user.ID}, {user.Username}, {user.NormalizedUsername}, {user.Nickname}, {user.PasswordHash})");
        db.Add(meeting);
        await db.SaveChangesAsync();
        await LegacyMeetingParticipantSeed.InsertAsync(db, human);
        await LegacyMeetingParticipantSeed.InsertAsync(db, agent);
        await LegacyMessageSeed.InsertAsync(db, first);
        await LegacyMessageSeed.InsertAsync(db, last);
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
