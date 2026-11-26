using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Exceptions;
using RoundTableMeeting.Repository.Sqlite.Transactions;
using RoundTableMeeting.Repository.Sqlite.UnitOfWork;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Fact]
    public async Task ReplyOwnershipUniqueConflictRollsBackWhileNullRepliesCoexist()
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var message = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, seed.Human.ID, MessageAuthorKind.Human,
            "Owner", "@A next", Guid.NewGuid(), Now);
        var mention = MessageMention.Create(seed.Meeting.ID, message.ID, seed.Agent.ID, "A", 0, 2);
        var second = AgentReplyTask.Create(Guid.NewGuid(), seed.Meeting.ID, seed.Agent.ID, message.ID, 2,
            mention.ID, MessageAuthorKind.Human, seed.Human.ID, 1, Guid.NewGuid(), Now);
        db.AddRange(message, mention, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await db.AgentReplyTasks.CountAsync(task => task.ReplyMessageId == null, cancellationToken: TestContext.Current.CancellationToken));
        var reply = MeetingMessage.CreateParticipant(seed.Meeting.ID, 3, seed.Agent.ID, MessageAuthorKind.Agent,
            "A", "reply", Guid.NewGuid(), Now);
        var first = await db.AgentReplyTasks.SingleAsync(task => task.ID == seed.TaskId, cancellationToken: TestContext.Current.CancellationToken);
        first.MarkDelivered(Now); first.MarkProcessing(Now); first.MarkReplied(reply.ID, Now);
        db.Add(reply);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var unitOfWork = new UnitOfWorkImpl(db);
        var runner = new MeetingTransactionRunner(db, unitOfWork);
        var error = await Assert.ThrowsAsync<AgentCollaborationConflictException>(() => runner.RunAsync(async token =>
        {
            var conflicting = await db.AgentReplyTasks.SingleAsync(task => task.ID == second.ID, token);
            conflicting.MarkDelivered(Now); conflicting.MarkProcessing(Now); conflicting.MarkReplied(reply.ID, Now);
            unitOfWork.RegisterEdit(conflicting);
            return true;
        }, TestContext.Current.CancellationToken));
        var databaseError = Assert.IsType<SqliteException>(Assert.IsType<DbUpdateException>(error.InnerException).InnerException);
        Assert.Equal(2067, databaseError.SqliteExtendedErrorCode);
        Assert.Contains("AgentReplyTasks.ReplyMessageId, AgentReplyTasks.MeetingId", databaseError.Message);
        Assert.Equal(1, await db.AgentReplyTasks.CountAsync(task => task.ReplyMessageId == reply.ID, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(AgentReplyTaskState.Queued, (await db.AgentReplyTasks.SingleAsync(task => task.ID == second.ID, cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Equal(0, await ForeignKeyErrors(db));
        var definition = await db.Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE name = 'IX_AgentReplyTasks_ReplyMessageId_MeetingId'").SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("CREATE UNIQUE INDEX", definition);
        Assert.Contains("\"ReplyMessageId\", \"MeetingId\"", definition);
        Assert.Contains("WHERE ReplyMessageId IS NOT NULL", definition);
    }
}
