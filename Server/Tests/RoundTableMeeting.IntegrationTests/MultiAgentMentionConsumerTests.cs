using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
using MessageAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class AgentMentionTaskConsumerTests
{
    [Fact]
    public async Task AgentReplyFansOutToSelfAndPeerWithStableSourceAndIdempotentReplays()
    {
        using var factory = new TestApplicationFactory();
        factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, true);
        var rootMessage = await AddMessageAsync(factory, seed, 1, MessageAuthorKind.Human, "@Reviewer",
            new MentionInput(seed.SecondAgentId!.Value, "Reviewer", 0, 9));
        await ConsumeAsync(factory, rootMessage.Envelope);
        var reply = await AddMessageAsync(factory, seed, 2, MessageAuthorKind.Agent, "@Analyst @Reviewer @Analyst",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8), new MentionInput(seed.SecondAgentId.Value, "Reviewer", 9, 9),
            new MentionInput(seed.FirstAgentId, "Analyst", 19, 8));
        Guid parentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var parent = await db.AgentReplyTasks.SingleAsync(task => task.MeetingId == seed.MeetingId);
            parentId = parent.ID;
            parent.MarkDelivered(parent.CreatedAtUtc); parent.MarkProcessing(parent.CreatedAtUtc);
            parent.MarkReplied(reply.MessageId, parent.CreatedAtUtc);
            db.Update(parent);
            await db.SaveChangesAsync();
        }
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => ConsumeAsync(factory, reply.Envelope)));
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var children = await context.AgentReplyTasks.Where(task => task.MeetingId == seed.MeetingId
            && task.TriggerMessageId == reply.MessageId).ToArrayAsync();
        Assert.Equal(2, children.Length);
        Assert.All(children, task => { Assert.Equal(parentId, task.ParentTaskId); Assert.Equal(rootMessage.MessageId, task.ChainId); });
        Assert.Equal(reply.MentionIds[0], children.Single(task => task.AgentParticipantId == seed.FirstAgentId).TriggerMentionId);
        Assert.Equal(0, children.Single(task => task.AgentParticipantId == seed.FirstAgentId).CandidateFromSequenceExclusive);
        Assert.Equal(1, children.Single(task => task.AgentParticipantId == seed.SecondAgentId).CandidateFromSequenceExclusive);
    }

    [Theory]
    [InlineData(MessageAuthorKind.Human)]
    [InlineData(MessageAuthorKind.Agent)]
    public async Task MissingBoundaryFailsWithoutAdvancingAnyTarget(MessageAuthorKind authorKind)
    {
        using var factory = new TestApplicationFactory();
        factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, true);
        var message = await AddMessageAsync(factory, seed, 1, authorKind, "@Analyst @Reviewer",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8), new MentionInput(seed.SecondAgentId!.Value, "Reviewer", 9, 9));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            await db.AgentCollaborationBoundaries.Where(item => item.MeetingId == seed.MeetingId).ExecuteDeleteAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(factory, message.Envelope));
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 0);
        await AssertCursorAsync(factory, seed.MeetingId, seed.SecondAgentId.Value, 0);
    }

    [Fact]
    public async Task CorruptEarlierTargetRollsBackWholeFanoutWhileIndependentTargetCanCatchUp()
    {
        using var factory = new TestApplicationFactory();
        factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, true);
        var root = await AddMessageAsync(factory, seed, 1, MessageAuthorKind.Human, "@Reviewer",
            new MentionInput(seed.SecondAgentId!.Value, "Reviewer", 0, 9));
        await ConsumeAsync(factory, root.Envelope);
        var orphan = await AddMessageAsync(factory, seed, 2, MessageAuthorKind.Agent, "@Reviewer orphan",
            new MentionInput(seed.SecondAgentId.Value, "Reviewer", 0, 9));
        var fanout = await AddMessageAsync(factory, seed, 3, MessageAuthorKind.Human, "@Analyst @Reviewer",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8), new MentionInput(seed.SecondAgentId.Value, "Reviewer", 9, 9));
        Dictionary<Guid, long> revisions;
        await using (var before = factory.Services.CreateAsyncScope())
        {
            var db = before.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            revisions = await db.AgentContextCursors.ToDictionaryAsync(item => item.ID, item => item.ActivityRevision);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(factory, fanout.Envelope));
        await using (var rolledBack = factory.Services.CreateAsyncScope())
        {
            var db = rolledBack.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(1, await db.AgentReplyTasks.CountAsync());
            Assert.False(await db.AgentReplyTasks.AnyAsync(task => task.TriggerMessageId == fanout.MessageId));
            foreach (var cursor in await db.AgentContextCursors.ToArrayAsync())
                Assert.Equal(revisions[cursor.ID], cursor.ActivityRevision);
        }
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 0);
        await AssertCursorAsync(factory, seed.MeetingId, seed.SecondAgentId.Value, 1);
        var independent = await AddMessageAsync(factory, seed, 4, MessageAuthorKind.Human, "@Analyst",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        await ConsumeAsync(factory, independent.Envelope);
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 4);
        await AssertCursorAsync(factory, seed.MeetingId, seed.SecondAgentId.Value, 1);
        Guid parentId;
        await using (var repair = factory.Services.CreateAsyncScope())
        {
            var db = repair.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var parent = await db.AgentReplyTasks.SingleAsync(task => task.TriggerMessageId == root.MessageId);
            parentId = parent.ID;
            parent.MarkDelivered(parent.CreatedAtUtc);
            parent.MarkProcessing(parent.CreatedAtUtc);
            parent.MarkReplied(orphan.MessageId, parent.CreatedAtUtc);
            db.Update(parent);
            await db.SaveChangesAsync();
        }
        await ConsumeAsync(factory, fanout.Envelope);
        await ConsumeAsync(factory, fanout.Envelope);
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 4);
        await AssertCursorAsync(factory, seed.MeetingId, seed.SecondAgentId.Value, 3);
        await using var check = factory.Services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await context.AgentReplyTasks.Where(task => task.MeetingId == seed.MeetingId).ToArrayAsync();
        Assert.Equal(5, tasks.Length);
        var derived = Assert.Single(tasks, task => task.TriggerMessageId == orphan.MessageId);
        Assert.Equal(parentId, derived.ParentTaskId);
        Assert.Equal(root.MessageId, derived.ChainId);
        Assert.Equal(1, derived.CandidateFromSequenceExclusive);
        Assert.Equal(2, tasks.Count(task => task.TriggerMessageId == fanout.MessageId));
        Assert.Equal(2, tasks.Single(task => task.TriggerMessageId == fanout.MessageId && task.AgentParticipantId == seed.SecondAgentId).CandidateFromSequenceExclusive);
    }

    [Fact]
    public async Task PagedBackfillCrossesOrdinaryMessagesWithoutDroppingHumanMentions()
    {
        using var factory = new TestApplicationFactory();
        factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, false);
        await AddMessageAsync(factory, seed, 1, MessageAuthorKind.Human, "@Analyst", new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            db.AddRange(Enumerable.Range(2, 260).Select(sequence => MeetingMessage.CreateSystem(seed.MeetingId, sequence,
                "System", "history", Guid.NewGuid(), DateTimeOffset.UtcNow)));
            await db.SaveChangesAsync();
        }
        var last = await AddMessageAsync(factory, seed, 262, MessageAuthorKind.Human, "@Analyst", new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        await ConsumeAsync(factory, last.Envelope);
        await using var check = factory.Services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await context.AgentReplyTasks.Where(task => task.MeetingId == seed.MeetingId).OrderBy(task => task.TriggerSequence).ToArrayAsync();
        Assert.Equal(new long[] { 1, 262 }, tasks.Select(task => task.TriggerSequence));
        Assert.Equal(new long[] { 0, 1 }, tasks.Select(task => task.CandidateFromSequenceExclusive));
    }
}
