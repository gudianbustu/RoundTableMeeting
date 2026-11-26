using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Theory]
    [InlineData("Queued")]
    [InlineData("Delivered")]
    [InlineData("Processing")]
    [InlineData("Failed")]
    [InlineData("Replied")]
    [InlineData("Cancelled")]
    public async Task UpgradePreservesEveryLegacyTaskColumnAcrossAllSixStates(string state)
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        Guid? replyId = null;
        if (state == "Replied")
        {
            var reply = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, seed.Agent.ID, MessageAuthorKind.Agent,
                "A", "historical reply", Guid.NewGuid(), Now.AddSeconds(1));
            await LegacyMessageSeed.InsertAsync(db, reply);
            replyId = reply.ID;
        }
        DateTimeOffset? delivered = state is "Delivered" or "Processing" or "Replied" ? Now : null;
        DateTimeOffset? processing = state is "Processing" or "Replied" ? Now : null;
        DateTimeOffset? failed = state == "Failed" ? Now : null;
        DateTimeOffset? replied = state == "Replied" ? Now.AddSeconds(1) : null;
        DateTimeOffset? cancelled = state == "Cancelled" ? Now : null;
        string? failureCode = state == "Failed" ? "historical_failure" : null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AgentReplyTasks SET State = {state}, DeliveredAtUtc = {delivered}, ProcessingAtUtc = {processing},
             FailedAtUtc = {failed}, FailureCode = {failureCode}, RepliedAtUtc = {replied}, ReplyMessageId = {replyId}, CancelledAtUtc = {cancelled}
            """, cancellationToken: TestContext.Current.CancellationToken);
        var before = await ReadLegacyColumnsAsync(db);
        await db.Database.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(before, await ReadLegacyColumnsAsync(db));
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(seed.Message.ID, task.ChainId);
        Assert.Null(task.ParentTaskId);
        Assert.Equal(state, task.State.ToString());
        Assert.Equal(state == "Replied" ? 2 : 1, (await db.AgentCollaborationBoundaries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AgentTriggerStartSequence);
        Assert.Equal(0, await ForeignKeyErrors(db));
    }

    private static async Task<string[]> ReadLegacyColumnsAsync(RoundTableMeetingDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT * FROM AgentReplyTasks";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var columns = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (name is "ChainId" or "ParentTaskId") continue;
            columns[name] = reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
        }
        Assert.False(await reader.ReadAsync());
        return columns.Select(pair => pair.Key + "=" + pair.Value).ToArray();
    }
}
