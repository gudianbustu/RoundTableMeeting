using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Repository.Sqlite.Database;
using MessageAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class AgentMentionTaskConsumerTests
{
    [Fact]
    public async Task IdenticalHumanAndAgentBodiesKeepDistinctTaskIdentityAndRoots()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, true);
        var first = await AddMessageAsync(factory, seed, 1, MessageAuthorKind.Human, "@Reviewer same topic",
            new MentionInput(seed.SecondAgentId!.Value, "Reviewer", 0, 9));
        var second = await AddMessageAsync(factory, seed, 2, MessageAuthorKind.Human, "@Reviewer same topic",
            new MentionInput(seed.SecondAgentId.Value, "Reviewer", 0, 9));
        await ConsumeAsync(factory, second.Envelope);
        var firstReply = await AddMessageAsync(factory, seed, 3, MessageAuthorKind.Agent, "@Analyst same response",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        var secondReply = await AddMessageAsync(factory, seed, 4, MessageAuthorKind.Agent, "@Analyst same response",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        Guid[] parentIds;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var parents = await db.AgentReplyTasks.Where(task => task.MeetingId == seed.MeetingId).OrderBy(task => task.TriggerSequence).ToArrayAsync();
            Assert.Equal(new[] { first.MessageId, second.MessageId }, parents.Select(task => task.ChainId));
            Assert.Equal(new long[] { 0, 1 }, parents.Select(task => task.CandidateFromSequenceExclusive));
            Assert.All(parents, task => Assert.Null(task.ParentTaskId));
            parentIds = parents.Select(task => task.ID).ToArray();
            for (var i = 0; i < parents.Length; i++)
            {
                var parent = parents[i];
                parent.MarkDelivered(parent.CreatedAtUtc); parent.MarkProcessing(parent.CreatedAtUtc);
                parent.MarkReplied(i == 0 ? firstReply.MessageId : secondReply.MessageId, parent.CreatedAtUtc);
                db.Update(parent);
            }
            await db.SaveChangesAsync();
        }
        await ConsumeAsync(factory, secondReply.Envelope);
        await ConsumeAsync(factory, firstReply.Envelope);
        await ConsumeAsync(factory, first.Envelope);
        await using var check = factory.Services.CreateAsyncScope();
        var final = check.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var children = await final.AgentReplyTasks.Where(task => task.AgentParticipantId == seed.FirstAgentId).OrderBy(task => task.TriggerSequence).ToArrayAsync();
        Assert.Equal(new[] { firstReply.MessageId, secondReply.MessageId }, children.Select(task => task.TriggerMessageId));
        Assert.Equal(new[] { first.MessageId, second.MessageId }, children.Select(task => task.ChainId));
        Assert.Equal(parentIds.Select(id => (Guid?)id), children.Select(task => task.ParentTaskId));
        Assert.Equal(new long[] { 0, 3 }, children.Select(task => task.CandidateFromSequenceExclusive));
        Assert.Equal(4, await final.AgentReplyTasks.CountAsync());
        Assert.Equal(4, await final.AgentReplyTasks.Select(task => task.ReplyIdempotencyKey).Distinct().CountAsync());
    }

    [Fact]
    public async Task NonTargetMessagesDoNotAllocateOrAdvanceButStayInLaterCandidateHistory()
    {
        using var factory = new TestApplicationFactory();
        using var client = factory.CreateClient();
        var seed = await SeedMeetingAsync(factory, false);
        var system = await AddMessageAsync(factory, seed, 1, MessageAuthorKind.System, "@Analyst system",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        var humanTarget = await AddMessageAsync(factory, seed, 2, MessageAuthorKind.Agent, "@Alice reply",
            new MentionInput(seed.HumanId, "Alice", 0, 6));
        var ordinaryAgent = await AddMessageAsync(factory, seed, 3, MessageAuthorKind.Agent, "ordinary agent");
        var ordinaryHuman = await AddMessageAsync(factory, seed, 4, MessageAuthorKind.Human, "ordinary human");
        foreach (var message in new[] { system, humanTarget, ordinaryAgent, ordinaryHuman })
        {
            await ConsumeAsync(factory, message.Envelope);
            await AssertCursorAsync(factory, seed.MeetingId, seed.FirstAgentId, 0);
            await using var empty = factory.Services.CreateAsyncScope();
            Assert.False(await empty.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.AnyAsync());
        }
        var trigger = await AddMessageAsync(factory, seed, 5, MessageAuthorKind.Human, "@Analyst next",
            new MentionInput(seed.FirstAgentId, "Analyst", 0, 8));
        await ConsumeAsync(factory, trigger.Envelope);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var task = await db.AgentReplyTasks.SingleAsync();
        Assert.Equal(0, task.CandidateFromSequenceExclusive);
        Assert.Equal(5, task.CandidateToSequenceInclusive);
        Assert.Equal(trigger.MessageId, task.ChainId);
        Assert.True(await db.MessageMentions.AnyAsync(item => item.MessageId == humanTarget.MessageId && item.TargetParticipantId == seed.HumanId));
        var history = await db.MeetingMessages.Where(item => item.MeetingId == seed.MeetingId
            && item.Sequence > task.CandidateFromSequenceExclusive && item.Sequence <= task.CandidateToSequenceInclusive).OrderBy(item => item.Sequence).ToArrayAsync();
        Assert.Equal(new[] { system.MessageId, humanTarget.MessageId, ordinaryAgent.MessageId, ordinaryHuman.MessageId, trigger.MessageId }, history.Select(item => item.ID));
    }

}
