using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;
using RoundTableMeeting.Repository.Sqlite.Services;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests : IDisposable
{
    private const string Previous = "20260911063339_AddUserAccountAdmin";
    private const string Current = "20260911080309_AddMultiAgentCollaboration";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-11T00:00:00Z");
    private readonly string path = Path.Combine(Path.GetTempPath(), $"multi-agent-{Guid.NewGuid():N}.db");
    private RoundTableMeetingDbContext Open() => new(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
        .UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False").Options);
    public void Dispose() { if (File.Exists(path)) File.Delete(path); }

    [Fact]
    public async Task MigrationBackfillsRootsAndAllMeetingBoundariesWithoutChangingTaskFacts()
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        var deleting = Meeting.Create("Deleting", null, seed.Meeting.CreatorUserId, Now);
        deleting.BeginDeleting();
        db.Add(deleting);
        await db.SaveChangesAsync();
        var before = await db.Database.SqlQueryRaw<string>("SELECT ReplyIdempotencyKey AS Value FROM AgentReplyTasks").SingleAsync();
        var triggers = await OwnerTriggers(db);
        await db.Database.MigrateAsync();
        var task = await db.AgentReplyTasks.SingleAsync();
        Assert.Equal(seed.TaskId, task.ID);
        Assert.Equal(seed.Message.ID, task.ChainId);
        Assert.Null(task.ParentTaskId);
        Assert.Equal(before, task.ReplyIdempotencyKey.ToString().ToUpperInvariant());
        Assert.Equal(AgentReplyTaskState.Queued, task.State);
        Assert.Equal(0, task.CandidateFromSequenceExclusive);
        Assert.Equal(1, task.TriggerSequence);
        Assert.Equal(1, (await db.AgentCollaborationBoundaries.SingleAsync(x => x.MeetingId == seed.Meeting.ID)).AgentTriggerStartSequence);
        Assert.Equal(0, (await db.AgentCollaborationBoundaries.SingleAsync(x => x.MeetingId == deleting.ID)).AgentTriggerStartSequence);
        Assert.Equal(triggers, await OwnerTriggers(db));
        Assert.Equal(0, await ForeignKeyErrors(db));
        await db.Database.MigrateAsync();
        Assert.Equal(2, await db.AgentCollaborationBoundaries.CountAsync());
    }

    [Theory]
    [InlineData("mention")]
    [InlineData("reply")]
    [InlineData("source")]
    [InlineData("reply-author")]
    [InlineData("reply-missing")]
    [InlineData("mention-message")]
    public async Task InvalidLegacyFactsAbortWholeMigration(string defect)
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        if (defect == "mention")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE MessageMentions SET TargetParticipantId = {seed.Human.ID}");
        else if (defect == "reply")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET ReplyMessageId = {seed.Message.ID}, State = 'Replied', RepliedAtUtc = {Now}");
        else if (defect == "reply-author")
        {
            var other = MeetingParticipant.CreateAgent(seed.Meeting.ID, "Other", Now);
            var reply = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, other.ID, MessageAuthorKind.Agent,
                "Other", "private fixture reply", Guid.NewGuid(), Now);
            db.AddRange(other, reply);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET ReplyMessageId = {reply.ID}, State = 'Replied', RepliedAtUtc = {Now}");
        }
        else if (defect == "reply-missing")
        {
            // Deliberately corrupt only this isolated fixture; migration runs with checks restored.
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET State = 'Replied', RepliedAtUtc = {Now}");
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = OFF");
        }
        else if (defect == "mention-message")
        {
            var other = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, seed.Human.ID, MessageAuthorKind.Human,
                "Owner", "@A", Guid.NewGuid(), Now);
            db.Add(other);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE MessageMentions SET MessageId = {other.ID}");
        }
        else
            await db.Database.ExecuteSqlRawAsync("UPDATE AgentReplyTasks SET SourceAuthorKind = 'Agent'");
        var before = await ReadLegacyColumnsAsync(db);
        var failure = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.DoesNotContain("private fixture reply", failure.Message);
        Assert.DoesNotContain(seed.Message.Content, failure.Message);
        Assert.Equal(before, await ReadLegacyColumnsAsync(db));
        Assert.Equal(Previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_table_info('AgentReplyTasks') WHERE name = 'ChainId'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM sqlite_master WHERE name = 'AgentCollaborationBoundaries'").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM AgentReplyTasks").SingleAsync());
    }

    [Fact]
    public async Task DerivedSourceIsImmutableAndCleanupRequiresDeletingTransaction()
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        await db.Database.MigrateAsync();
        var parent = await db.AgentReplyTasks.SingleAsync();
        var reply = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, seed.Agent.ID, MessageAuthorKind.Agent,
            "A", "@A", Guid.NewGuid(), Now);
        var mention = MessageMention.Create(seed.Meeting.ID, reply.ID, seed.Agent.ID, "A", 0, 2);
        parent.MarkDelivered(Now); parent.MarkProcessing(Now); parent.MarkReplied(reply.ID, Now);
        db.AddRange(reply, mention);
        await db.SaveChangesAsync();
        var child = AgentReplyTask.CreateDerived(Guid.NewGuid(), seed.Agent.ID, reply, mention, parent, 1, Guid.NewGuid(), Now);
        db.Add(child);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new AgentCollaborationRepository(db);
        Assert.Equal(parent.ID, (await repository.FindTaskByReplyMessageAsync(seed.Meeting.ID, reply.ID))!.ID);
        Assert.Null(await repository.FindTaskByReplyMessageAsync(Guid.NewGuid(), reply.ID));
        Assert.Null(await repository.FindBoundaryAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET ParentTaskId = NULL WHERE ID = {child.ID}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET ChainId = {reply.ID} WHERE ID = {child.ID}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM AgentReplyTasks WHERE ID = {parent.ID}"));
        var cleanup = new AgentCollaborationCleanupService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.DeleteForMeetingAsync(seed.Meeting.ID, Now));
        await using (var active = await db.Database.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.DeleteForMeetingAsync(seed.Meeting.ID, Now));
            await active.RollbackAsync();
        }
        await db.Database.ExecuteSqlRawAsync("UPDATE Meetings SET LifecycleState = 'Deleting'");
        await using (var rollback = await db.Database.BeginTransactionAsync())
        {
            await db.AgentReplyTasks.ExecuteUpdateAsync(update => update.SetProperty(x => x.ParentTaskId, (Guid?)null));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET ParentTaskId = {parent.ID} WHERE ID = {child.ID}"));
            await rollback.RollbackAsync();
        }
        Assert.Equal(parent.ID, (await db.AgentReplyTasks.AsNoTracking().SingleAsync(x => x.ID == child.ID)).ParentTaskId);
        await using (var deletion = await db.Database.BeginTransactionAsync())
        {
            await cleanup.DeleteForMeetingAsync(seed.Meeting.ID, Now);
            await cleanup.DeleteForMeetingAsync(seed.Meeting.ID, Now);
            await deletion.CommitAsync();
        }
        Assert.Empty(await db.AgentReplyTasks.ToArrayAsync());
        Assert.Empty(await db.AgentCollaborationBoundaries.ToArrayAsync());
        Assert.Equal(2, await db.MeetingMessages.CountAsync());
        Assert.Equal(0, await ForeignKeyErrors(db));
    }

    [Fact]
    public async Task MissingBoundaryRejectsHumanInsertAndBoundaryCannotBeChanged()
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        await db.Database.MigrateAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE AgentCollaborationBoundaries SET AgentTriggerStartSequence = 0"));
        await db.AgentReplyTasks.ExecuteDeleteAsync();
        await db.AgentCollaborationBoundaries.ExecuteDeleteAsync();
        var root = AgentReplyTask.Create(Guid.NewGuid(), seed.Meeting.ID, seed.Agent.ID, seed.Message.ID, 1,
            seed.Mention.ID, MessageAuthorKind.Human, seed.Human.ID, 0, Guid.NewGuid(), Now);
        db.Add(root);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Add(AgentCollaborationBoundary.Create(seed.Meeting.ID, 1));
        await db.SaveChangesAsync();
        db.Add(root);
        await db.SaveChangesAsync();
        Assert.Single(await db.AgentReplyTasks.ToArrayAsync());
        Assert.Equal(Current, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    private static Task<long> ForeignKeyErrors(RoundTableMeetingDbContext db)
        => db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync();
    private static Task<string[]> OwnerTriggers(RoundTableMeetingDbContext db)
        => db.Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE type = 'trigger' AND name LIKE 'agent_%_owner_%' ORDER BY name").ToArrayAsync();

    private static async Task<(Meeting Meeting, MeetingParticipant Human, MeetingParticipant Agent, MeetingMessage Message, MessageMention Mention, Guid TaskId)> SeedOld(RoundTableMeetingDbContext db)
    {
        await db.Database.MigrateAsync(Previous);
        var user = UserAccount.Create("owner", "Owner"); user.InitializePasswordHash("hash");
        var meeting = Meeting.Create("Collaboration", null, user.ID, Now);
        var human = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Owner", Now);
        var agent = MeetingParticipant.CreateAgent(meeting.ID, "A", Now);
        var message = MeetingMessage.CreateParticipant(meeting.ID, 1, human.ID, MessageAuthorKind.Human, "Owner", "@A", Guid.NewGuid(), Now);
        var mention = MessageMention.Create(meeting.ID, message.ID, agent.ID, "A", 0, 2);
        db.AddRange(user, meeting, human, agent, message, mention);
        await db.SaveChangesAsync();
        var taskId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentReplyTasks (ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,
             SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive, CandidateToSequenceInclusive, ReplyIdempotencyKey,
             State, CreatedAtUtc, AutomaticRetryCount, RetryCycle, RetryExhausted)
            VALUES ({taskId}, {meeting.ID}, {agent.ID}, {message.ID}, 1, {mention.ID}, 'Human', {human.ID}, 0, 1, {Guid.NewGuid()}, 'Queued', {Now}, 0, 0, 0)
            """);
        db.ChangeTracker.Clear();
        return (meeting, human, agent, message, mention, taskId);
    }
}
