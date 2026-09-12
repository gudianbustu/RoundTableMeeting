using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Repository.Sqlite.Tests;

public sealed partial class MultiAgentCollaborationPersistenceTests
{
    [Theory]
    [InlineData(MessageEventDeliveryState.Pending)]
    [InlineData(MessageEventDeliveryState.Leased)]
    [InlineData(MessageEventDeliveryState.Suspended)]
    [InlineData(MessageEventDeliveryState.Completed)]
    public async Task UpgradePreservesLegacyAgentAndHumanEventDeliveryFacts(MessageEventDeliveryState state)
    {
        await using var db = Open();
        var seed = await SeedOld(db);
        var reply = MeetingMessage.CreateParticipant(seed.Meeting.ID, 2, seed.Agent.ID, MessageAuthorKind.Agent,
            "A", "@A historical reply", Guid.NewGuid(), Now.AddSeconds(1));
        var mention = MessageMention.Create(seed.Meeting.ID, reply.ID, seed.Agent.ID, "A", 0, 2);
        var humanEvent = MessageAppendedEvent.Create(seed.Meeting.ID, seed.Message.ID, 1, Now);
        var agentEvent = MessageAppendedEvent.Create(seed.Meeting.ID, reply.ID, 2, Now.AddSeconds(1));
        db.AddRange(reply, mention, humanEvent, agentEvent);
        foreach (var source in new[] { humanEvent, agentEvent })
        {
            var delivery = MessageEventDelivery.Create(source.ID, "agent-mention-task-v1", Now.AddSeconds(1));
            if (state != MessageEventDeliveryState.Pending)
            {
                var lease = Guid.NewGuid();
                delivery.Lease(lease, Now.AddMinutes(1));
                if (state == MessageEventDeliveryState.Suspended) Assert.True(delivery.Fail(lease, Now.AddMinutes(2), true));
                if (state == MessageEventDeliveryState.Completed) Assert.True(delivery.Complete(lease, Now.AddSeconds(2)));
            }
            db.Add(delivery);
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentReplyTasks SET State = 'Replied', ReplyMessageId = {reply.ID}, RepliedAtUtc = {Now.AddSeconds(1)}");
        var events = await SnapshotLegacyTableAsync(db, "MessageAppendedEvents");
        var deliveries = await SnapshotLegacyTableAsync(db, "MessageEventDeliveries");
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        Assert.Equal(events, await SnapshotLegacyTableAsync(db, "MessageAppendedEvents"));
        Assert.Equal(deliveries, await SnapshotLegacyTableAsync(db, "MessageEventDeliveries"));
        Assert.All(await db.MessageEventDeliveries.ToArrayAsync(), delivery => Assert.Equal(state, delivery.State));
        Assert.Equal(2, (await db.AgentCollaborationBoundaries.SingleAsync()).AgentTriggerStartSequence);
        Assert.Equal(seed.Message.ID, (await db.AgentReplyTasks.SingleAsync()).ChainId);
        Assert.False(await db.AgentReplyTasks.AnyAsync(task => task.TriggerMessageId == reply.ID));
        Assert.Equal(0, await ForeignKeyErrors(db));
    }
}
