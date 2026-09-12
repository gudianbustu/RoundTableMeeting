using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Services;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ActualConnectionOpenRequiresForeignKeys(bool asynchronous, bool enabled)
    {
        await using var db = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys={enabled};Pooling=False")
            .AddInterceptors(new SqliteConnectionInterceptor()).Options);
        if (!enabled)
        {
            if (asynchronous) await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.OpenConnectionAsync());
            else Assert.Throws<InvalidOperationException>(() => db.Database.OpenConnection());
            return;
        }
        if (asynchronous) await db.Database.OpenConnectionAsync();
        else db.Database.OpenConnection();
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT foreign_keys AS Value FROM pragma_foreign_keys").SingleAsync());
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(9007199254740992L)]
    public async Task DatabaseRejectsInvalidBoundaryWithoutDomainFactory(long sequence)
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        await db.Database.MigrateAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO AgentCollaborationBoundaries (ID, MeetingId, AgentTriggerStartSequence) VALUES ({Guid.NewGuid()}, {seed.Meeting.ID}, 0)"));
        Assert.Equal(1, (await db.AgentCollaborationBoundaries.SingleAsync()).AgentTriggerStartSequence);
        await db.AgentCollaborationBoundaries.ExecuteDeleteAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO AgentCollaborationBoundaries (ID, MeetingId, AgentTriggerStartSequence) VALUES ({Guid.NewGuid()}, {seed.Meeting.ID}, {sequence})"));
        Assert.Empty(await db.AgentCollaborationBoundaries.ToArrayAsync());
    }

    [Theory]
    [InlineData(2L, false)]
    [InlineData(1L, true)]
    public async Task AgentTriggerMustBeStrictlyAfterPersistedBoundary(long boundary, bool allowed)
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        await db.Database.MigrateAsync();
        await db.AgentCollaborationBoundaries.ExecuteDeleteAsync();
        db.Add(AgentCollaborationBoundary.Create(seed.Meeting.ID, boundary));
        await db.SaveChangesAsync();
        var parent = await db.AgentReplyTasks.SingleAsync();
        var reply = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, seed.Agent.ID, MessageAuthorKind.Agent,
            "A", "@A", Guid.NewGuid(), Now);
        var mention = MessageMention.Create(seed.Meeting.ID, reply.ID, seed.Agent.ID, "A", 0, 2);
        parent.MarkDelivered(Now); parent.MarkProcessing(Now); parent.MarkReplied(reply.ID, Now);
        db.AddRange(reply, mention);
        await db.SaveChangesAsync();
        db.Add(AgentReplyTask.CreateDerived(Guid.NewGuid(), seed.Agent.ID, reply, mention, parent, 1, Guid.NewGuid(), Now));
        if (allowed) await db.SaveChangesAsync();
        else await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(allowed ? 2 : 1, await db.AgentReplyTasks.CountAsync());
    }

    [Theory]
    [InlineData("UPDATE AgentReplyTasks SET SourceAuthorKind = 'Agent'")]
    [InlineData("UPDATE AgentReplyTasks SET SourceParticipantId = AgentParticipantId")]
    [InlineData("UPDATE AgentReplyTasks SET TriggerSequence = 2")]
    [InlineData("UPDATE AgentReplyTasks SET ParentTaskId = ID")]
    [InlineData("UPDATE AgentReplyTasks SET ChainId = ID")]
    public async Task PersistedRootIdentityCannotBeMutated(string mutation)
    {
        await using var db = Open();
        await SeedOld(db);
        await db.Database.MigrateAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(mutation));
        var root = await db.AgentReplyTasks.SingleAsync();
        Assert.Equal(MessageAuthorKind.Human, root.SourceAuthorKind);
        Assert.Equal(root.TriggerMessageId, root.ChainId);
        Assert.Null(root.ParentTaskId);
        Assert.Equal(1, root.TriggerSequence);
        Assert.Equal(0, await ForeignKeyErrors(db));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task MaintenanceResumesOnlySuspendedDeliveryInActiveMeeting(bool deleting, bool suspended)
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        await db.Database.MigrateAsync();
        var appended = MessageAppendedEvent.Create(seed.Meeting.ID, seed.Message.ID, 1, Now);
        var delivery = MessageEventDelivery.Create(appended.ID, "maintenance-test", Now);
        if (suspended)
        {
            var lease = Guid.NewGuid();
            delivery.Lease(lease, Now.AddMinutes(1));
            Assert.True(delivery.Fail(lease, Now, true));
        }
        db.AddRange(appended, delivery);
        await db.SaveChangesAsync();
        if (deleting) await db.Database.ExecuteSqlRawAsync("UPDATE Meetings SET LifecycleState = 'Deleting'");
        db.ChangeTracker.Clear();
        var recovery = new MessageEventRecoveryService(db, TimeProvider.System);
        Assert.Equal(!deleting && suspended, await recovery.ResumeAsync(delivery.ID));
        db.ChangeTracker.Clear();
        var saved = await db.MessageEventDeliveries.SingleAsync();
        Assert.Equal(delivery.ID, saved.ID);
        Assert.Equal(appended.ID, saved.EventId);
        Assert.Equal(suspended ? 1 : 0, saved.AttemptCount);
        Assert.Equal(deleting && suspended ? MessageEventDeliveryState.Suspended : MessageEventDeliveryState.Pending, saved.State);
        Assert.False(await recovery.ResumeAsync(delivery.ID));
        Assert.False(await recovery.ResumeAsync(Guid.NewGuid()));
        Assert.Single(await db.MeetingMessages.ToArrayAsync());
    }
}
