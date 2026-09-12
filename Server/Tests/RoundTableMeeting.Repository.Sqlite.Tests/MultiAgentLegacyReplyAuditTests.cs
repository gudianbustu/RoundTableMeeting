using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Fact]
    public async Task AmbiguousLegacyReplyAbortsMigrationAndLeavesBothOldTasksReadable()
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        var second = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, seed.Human.ID, MessageAuthorKind.Human,
            "Owner", "@A second", Guid.NewGuid(), Now);
        var mention = MessageMention.Create(seed.Meeting.ID, second.ID, seed.Agent.ID, "A", 0, 2);
        var reply = MeetingMessage.CreateParticipant(seed.Meeting.ID, 3, seed.Agent.ID, MessageAuthorKind.Agent,
            "A", "private fixture reply", Guid.NewGuid(), Now);
        db.AddRange(second, mention, reply);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AgentReplyTasks (ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,
             SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive, CandidateToSequenceInclusive, ReplyIdempotencyKey,
             State, CreatedAtUtc, AutomaticRetryCount, RetryCycle, RetryExhausted)
            VALUES ({Guid.NewGuid()}, {seed.Meeting.ID}, {seed.Agent.ID}, {second.ID}, 2, {mention.ID},
             'Human', {seed.Human.ID}, 1, 2, {Guid.NewGuid()}, 'Queued', {Now}, 0, 0, 0)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET State = 'Replied', ReplyMessageId = {reply.ID}, RepliedAtUtc = {Now}");
        var failure = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains("Ambiguous collaboration reply", failure.Message);
        Assert.DoesNotContain(reply.Content, failure.Message);
        Assert.Equal(Previous, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(2, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM AgentReplyTasks WHERE State = 'Replied'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_table_info('AgentReplyTasks') WHERE name = 'ChainId'").SingleAsync());
        Assert.Equal(0, await ForeignKeyErrors(db));
    }
}
