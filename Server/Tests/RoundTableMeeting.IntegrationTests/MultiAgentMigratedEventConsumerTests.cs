using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using MessageAuthorKind = RoundTableMeeting.Domain.Enums.MessageAuthorKind;
using RoundTableMeeting.RealtimeChat;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MultiAgentMigratedEventConsumerTests
{
    [Theory]
    [InlineData(MessageEventDeliveryState.Pending)]
    [InlineData(MessageEventDeliveryState.Leased)]
    [InlineData(MessageEventDeliveryState.Suspended)]
    [InlineData(MessageEventDeliveryState.Completed)]
    public async Task MigratedOldAgentEventCannotSwallowPendingHumanMention(MessageEventDeliveryState state)
    {
        using var original = new TestApplicationFactory();
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var user = UserAccount.Create("migration-owner", "Owner");
        user.InitializePasswordHash("fixture-hash");
        var meeting = Meeting.Create("Migrated consumer", null, user.ID, now);
        var human = MeetingParticipant.CreateHuman(meeting.ID, user.ID, "Owner", now);
        var alpha = MeetingParticipant.CreateAgent(meeting.ID, "Alpha", now);
        var beta = MeetingParticipant.CreateAgent(meeting.ID, "Beta", now);
        var root = MeetingMessage.CreateParticipant(meeting.ID, 1, human.ID, MessageAuthorKind.Human, "Owner", "@Alpha", Guid.NewGuid(), now);
        var pending = MeetingMessage.CreateParticipant(meeting.ID, 2, human.ID, MessageAuthorKind.Human, "Owner", "@Beta", Guid.NewGuid(), now);
        var reply = MeetingMessage.CreateParticipant(meeting.ID, 3, alpha.ID, MessageAuthorKind.Agent, "Alpha", "@Beta", Guid.NewGuid(), now);
        var rootMention = MessageMention.Create(meeting.ID, root.ID, alpha.ID, "Alpha", 0, 6);
        var oldEvent = MessageAppendedEvent.Create(meeting.ID, reply.ID, 3, now);
        var delivery = MessageEventDelivery.Create(oldEvent.ID, "agent-mention-task-v1", now);
        if (state != MessageEventDeliveryState.Pending)
        {
            var lease = Guid.NewGuid();
            delivery.Lease(lease, now.AddMinutes(1));
            if (state == MessageEventDeliveryState.Suspended) delivery.Fail(lease, now.AddMinutes(2), true);
            if (state == MessageEventDeliveryState.Completed) delivery.Complete(lease, now.AddSeconds(1));
        }
        await using (var old = new RoundTableMeetingDbContext(new DbContextOptionsBuilder<RoundTableMeetingDbContext>()
            .UseSqlite($"Data Source={original.DatabasePath};Foreign Keys=True;Pooling=False").Options))
        {
            await old.Database.MigrateAsync("20260911063339_AddUserAccountAdmin");
            old.AddRange(user, meeting, human, alpha, beta, root, pending, reply, rootMention, oldEvent, delivery,
                MessageMention.Create(meeting.ID, pending.ID, beta.ID, "Beta", 0, 5),
                MessageMention.Create(meeting.ID, reply.ID, beta.ID, "Beta", 0, 5),
                AgentContextCursor.Create(meeting.ID, beta.ID, 0, now));
            await old.SaveChangesAsync();
            await old.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO AgentReplyTasks (ID, MeetingId, AgentParticipantId, TriggerMessageId, TriggerSequence, TriggerMentionId,
                 SourceAuthorKind, SourceParticipantId, CandidateFromSequenceExclusive, CandidateToSequenceInclusive, ReplyIdempotencyKey,
                 State, CreatedAtUtc, AutomaticRetryCount, RetryCycle, RetryExhausted, ReplyMessageId, RepliedAtUtc)
                VALUES ({Guid.NewGuid()}, {meeting.ID}, {alpha.ID}, {root.ID}, 1, {rootMention.ID}, 'Human', {human.ID}, 0, 1,
                 {Guid.NewGuid()}, 'Replied', {now}, 0, 0, 0, {reply.ID}, {now})
                """);
        }
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Keep the production startup migration; explicitly drive delivery order in this test.
            foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(MessageEventDispatcherService)).ToArray()) services.Remove(descriptor);
        }));
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(3, (await db.AgentCollaborationBoundaries.SingleAsync()).AgentTriggerStartSequence);
        Assert.Equal(state, (await db.MessageEventDeliveries.SingleAsync()).State);
        var consumer = scope.ServiceProvider.GetRequiredService<AgentMentionTaskConsumer>();
        var oldEnvelope = new MessageAppendedEnvelope(oldEvent.ID, reply.ID, meeting.ID, 3, now);
        await consumer.ConsumeAsync(oldEnvelope);
        Assert.Equal(0, (await db.AgentContextCursors.SingleAsync()).AllocatedThroughSequence);
        var later = MeetingMessage.CreateParticipant(meeting.ID, 4, human.ID, MessageAuthorKind.Human, "Owner", "@Beta later", Guid.NewGuid(), now.AddSeconds(2));
        db.AddRange(later, MessageMention.Create(meeting.ID, later.ID, beta.ID, "Beta", 0, 5));
        await db.SaveChangesAsync();
        await consumer.ConsumeAsync(new(Guid.NewGuid(), later.ID, meeting.ID, 4, later.CreatedAtUtc));
        await consumer.ConsumeAsync(oldEnvelope);
        await consumer.ConsumeAsync(new(Guid.NewGuid(), pending.ID, meeting.ID, 2, pending.CreatedAtUtc));
        var tasks = await db.AgentReplyTasks.Where(task => task.AgentParticipantId == beta.ID).OrderBy(task => task.TriggerSequence).ToArrayAsync();
        Assert.Equal(new long[] { 2, 4 }, tasks.Select(task => task.TriggerSequence));
        Assert.Equal(new long[] { 0, 2 }, tasks.Select(task => task.CandidateFromSequenceExclusive));
        Assert.Equal(new[] { pending.ID, later.ID }, tasks.Select(task => task.ChainId));
        Assert.All(tasks, task => Assert.Null(task.ParentTaskId));
        Assert.Equal(4, (await db.AgentContextCursors.SingleAsync()).AllocatedThroughSequence);
        Assert.Equal(state, (await db.MessageEventDeliveries.SingleAsync()).State);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
    }
}
