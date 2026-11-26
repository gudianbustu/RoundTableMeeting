using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;
using MessageAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class AgentMentionTaskConsumerTests
{
    [Fact]
    public async Task NewAgentReplyBackfillsOldHumanMentionWithoutReplayingOldAgentMessages()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, true, boundarySequence: 3);
        var root = await AddMessageAsync(factory, seed, 1, MessageAuthorKind.Human, "@Reviewer prepare",
            new MentionInput(seed.SecondAgentId!.Value, "Reviewer", 0, 9));
        await ConsumeAsync(factory, root.Envelope);
        var pending = await AddMessageAsync(factory, seed, 2, MessageAuthorKind.Human, "@Analyst pending",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        await AddMessageAsync(factory, seed, 3, MessageAuthorKind.Agent, "@Analyst old reply",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        var reply = await AddMessageAsync(factory, seed, 4, MessageAuthorKind.Agent, "@Analyst new reply",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        Guid parentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var parent = await db.AgentReplyTasks.SingleAsync(task => task.MeetingId == seed.MeetingId, cancellationToken: TestContext.Current.CancellationToken);
            parentId = parent.ID;
            parent.MarkDelivered(parent.CreatedAtUtc); parent.MarkProcessing(parent.CreatedAtUtc);
            parent.MarkReplied(reply.MessageId, parent.CreatedAtUtc);
            db.Update(parent);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await ConsumeAsync(factory, reply.Envelope);
        await ConsumeAsync(factory, pending.Envelope);
        await using var check = factory.Services.CreateAsyncScope();
        var final = check.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await final.AgentReplyTasks.Where(task => task.MeetingId == seed.MeetingId
            && task.AgentParticipantId == seed.FirstAgentId).OrderBy(task => task.TriggerSequence).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 2, 4 }, tasks.Select(task => task.TriggerSequence));
        Assert.Equal(new long[] { 0, 2 }, tasks.Select(task => task.CandidateFromSequenceExclusive));
        Assert.Equal(pending.MessageId, tasks[0].ChainId);
        Assert.Null(tasks[0].ParentTaskId);
        Assert.Equal(root.MessageId, tasks[1].ChainId);
        Assert.Equal(parentId, tasks[1].ParentTaskId);
    }

    [Fact]
    public async Task OldAgentEventDoesNotConsumePendingHumanMentionAndLaterEventBackfillsIt()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, false, boundarySequence: 2);
        var human = await AddMessageAsync(factory, seed, 1, MessageAuthorKind.Human, "@Analyst pending before upgrade",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        var oldAgent = await AddMessageAsync(factory, seed, 2, MessageAuthorKind.Agent, "@Analyst old reply",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        await ConsumeAsync(factory, oldAgent.Envelope);
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 0);
        var current = await AddMessageAsync(factory, seed, 3, MessageAuthorKind.Human, "@Analyst after upgrade",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        await ConsumeAsync(factory, current.Envelope);
        await ConsumeAsync(factory, oldAgent.Envelope);
        await ConsumeAsync(factory, human.Envelope);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await db.AgentReplyTasks.Where(task => task.MeetingId == seed.MeetingId).OrderBy(task => task.TriggerSequence).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 1, 3 }, tasks.Select(task => task.TriggerSequence));
        Assert.Equal(new long[] { 0, 1 }, tasks.Select(task => task.CandidateFromSequenceExclusive));
        Assert.Equal(new[] { human.MessageId, current.MessageId }, tasks.Select(task => task.ChainId));
        Assert.All(tasks, task => Assert.Null(task.ParentTaskId));
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 3);
    }

    [Fact]
    public async Task RestoringMissingBoundaryAllowsOriginalHumanEventToAllocateAllTargetsOnce()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, true, boundarySequence: 1);
        var message = await AddMessageAsync(factory, seed, 1, MessageAuthorKind.Human, "@Analyst @Reviewer",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8),
            new MentionInput(seed.SecondAgentId!.Value, "Reviewer", 9, 9));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            await db.AgentCollaborationBoundaries.Where(x => x.MeetingId == seed.MeetingId).ExecuteDeleteAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConsumeAsync(factory, message.Envelope));
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 0);
        await AssertCursorAsync(factory, seed.MeetingId, seed.SecondAgentId.Value, 0);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Empty(await db.AgentReplyTasks.Where(x => x.MeetingId == seed.MeetingId).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
            db.Add(AgentCollaborationBoundary.Create(seed.MeetingId, 1));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await ConsumeAsync(factory, message.Envelope);
        await ConsumeAsync(factory, message.Envelope);
        await using var check = factory.Services.CreateAsyncScope();
        var final = check.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var tasks = await final.AgentReplyTasks.Where(x => x.MeetingId == seed.MeetingId).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, tasks.Length);
        Assert.All(tasks, task => Assert.Equal(message.MessageId, task.ChainId));
        await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 1);
        await AssertCursorAsync(factory, seed.MeetingId, seed.SecondAgentId.Value, 1);
    }
}
