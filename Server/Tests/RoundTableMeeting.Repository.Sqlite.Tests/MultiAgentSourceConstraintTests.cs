using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Theory]
    [InlineData("missing-parent")]
    [InlineData("wrong-parent")]
    [InlineData("same-meeting-wrong-chain")]
    [InlineData("foreign-meeting-chain")]
    [InlineData("wrong-author")]
    [InlineData("wrong-mention")]
    [InlineData("pretend-human")]
    public async Task DatabaseRejectsForgedDerivedSourceButAcceptsTheUnmodifiedTask(string defect)
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
        var otherMeeting = Meeting.Create("Other meeting", null, seed.Meeting.CreatorUserId, Now);
        var otherHuman = MeetingParticipant.CreateHuman(otherMeeting.ID, seed.Meeting.CreatorUserId, "Owner", Now);
        var foreignRoot = MeetingMessage.CreateParticipant(otherMeeting.ID, 1, otherHuman.ID, MessageAuthorKind.Human,
            "Owner", "foreign", Guid.NewGuid(), Now);
        db.AddRange(otherMeeting, otherHuman, foreignRoot);
        await db.SaveChangesAsync();

        var child = AgentReplyTask.CreateDerived(Guid.NewGuid(), seed.Agent.ID, reply, mention, parent, 1, Guid.NewGuid(), Now);
        var entry = db.Add(child);
        switch (defect)
        {
            case "missing-parent": entry.Property(x => x.ParentTaskId).CurrentValue = null; break;
            case "wrong-parent": entry.Property(x => x.ParentTaskId).CurrentValue = Guid.NewGuid(); break;
            case "same-meeting-wrong-chain": entry.Property(x => x.ChainId).CurrentValue = reply.ID; break;
            case "foreign-meeting-chain": entry.Property(x => x.ChainId).CurrentValue = foreignRoot.ID; break;
            case "wrong-author": entry.Property(x => x.SourceParticipantId).CurrentValue = seed.Human.ID; break;
            case "wrong-mention": entry.Property(x => x.TriggerMentionId).CurrentValue = seed.Mention.ID; break;
            case "pretend-human": entry.Property(x => x.SourceAuthorKind).CurrentValue = MessageAuthorKind.Human; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(19, Assert.IsType<SqliteException>(failure.InnerException).SqliteErrorCode);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.AgentReplyTasks.CountAsync());
        var valid = AgentReplyTask.CreateDerived(Guid.NewGuid(), seed.Agent.ID, reply, mention, parent, 1, Guid.NewGuid(), Now);
        db.Add(valid);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.AgentReplyTasks.CountAsync());
        Assert.Equal(0, await ForeignKeyErrors(db));
    }
}
