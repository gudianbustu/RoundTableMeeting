using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, 512)]
    [InlineData(false, 16000)]
    [InlineData(true, 512)]
    [InlineData(true, 16000)]
    public async Task TakeoverSnapshotAt25CannotSkipTasksAt15And20OrTheirOriginalCandidateRanges(bool previouslyConfirmed, int nextBudget)
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Takeover fixed windows");
        for (var sequence = 1; sequence <= 10; sequence++)
            await PostHumanMessageAsync(browser, meeting.MeetingId, "Initial history " + sequence);
        var oldTransport = RecoveryTransport(241);
        var old = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services,
            ReliableJoinRequest(meeting.MeetingCode, "Worker", "create") with { ContextCharacterLimit = nextBudget == 512 ? 16000 : 512 }, oldTransport)).Data);
        var oldId = old.Connection.ConnectionSessionId;
        Assert.Equal(10, old.InitialContext.SnapshotSequence);
        if (previouslyConfirmed)
            Assert.True((await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(oldId, "initial", old.InitialDeliveryId), oldTransport))).Succeeded);
        for (var sequence = 11; sequence <= 15; sequence++)
            await PostHumanMessageAsync(browser, meeting.MeetingId, (sequence == 15 ? "@Worker " : "") + "First range " + sequence);
        AgentMentionResultDto? oldAttempt = null;
        if (previouslyConfirmed)
        {
            oldAttempt = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(oldId), oldTransport))).Data);
            Assert.True((await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(oldId, "task",
                TaskId: oldAttempt.TaskId, DeliveryAttemptId: oldAttempt.DeliveryAttemptId), oldTransport))).Succeeded);
        }
        else Assert.Equal("initial_context_unconfirmed", (await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(oldId), oldTransport))).ErrorCode);
        for (var sequence = 16; sequence <= 25; sequence++)
            await PostHumanMessageAsync(browser, meeting.MeetingId, (sequence == 20 ? "@Worker " : "") + "Later history " + sequence);
        await EventuallyAsync(factory, async db => await db.AgentReplyTasks.CountAsync() == 2);
        Guid firstTaskId;
        Guid secondTaskId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var tasks = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentReplyTasks.OrderBy(t => t.TriggerSequence).ToArrayAsync();
            firstTaskId = tasks[0].ID;
            secondTaskId = tasks[1].ID;
            Assert.Equal(10, tasks[0].CandidateFromSequenceExclusive);
            Assert.Equal(15, tasks[0].CandidateToSequenceInclusive);
            Assert.Equal(15, tasks[1].CandidateFromSequenceExclusive);
            Assert.Equal(20, tasks[1].CandidateToSequenceInclusive);
        }
        var transport = RecoveryTransport(242);
        var taken = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services,
            ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover") with { ContextCharacterLimit = nextBudget }, transport)).Data);
        var id = taken.Connection.ConnectionSessionId;
        Assert.NotEqual(oldId, id);
        Assert.Equal(old.Agent.ParticipantId, taken.Agent.ParticipantId);
        Assert.Equal(25, taken.InitialContext.SnapshotSequence);
        Assert.Equal(nextBudget, taken.InitialContext.DeclaredCharacterLimit);
        Assert.Equal("initial", taken.ContextPurpose);
        var confirmation = Assert.IsType<AgentConfirmContextResultDto>((await ReliableForTestAsync(factory.Services,
            s => s.ConfirmReliableAsync(new(id, "initial", taken.InitialDeliveryId), transport))).Data);
        Assert.Equal(previouslyConfirmed ? 15 : 10, confirmation.DeliveredThroughSequence);
        time.Now = time.Now.AddSeconds(10);
        var first = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        Assert.Equal(firstTaskId, first.TaskId);
        Assert.Equal(11, first.Context.CandidateFirstSequence);
        Assert.Equal(15, first.Context.CandidateLastSequence);
        Assert.Equal(15, first.Context.DeliveredLastSequence);
        Assert.Equal(nextBudget, first.Context.DeclaredCharacterLimit);
        Assert.Equal(nextBudget == 512, first.Context.Truncated);
        Assert.Equal("task", first.ContextPurpose);
        if (oldAttempt is not null)
        {
            Assert.NotEqual(oldAttempt.DeliveryAttemptId, first.DeliveryAttemptId);
            Assert.Equal(oldAttempt.ReplyIdempotencyKey, first.ReplyIdempotencyKey);
            Assert.Equal("takeover", first.ReplayReason);
        }
        Assert.Equal("context_confirmation_required", (await ReliableForTestAsync(factory.Services,
            s => s.ReplyReliableAsync(new(id, first.TaskId, first.DeliveryAttemptId, first.ReplyIdempotencyKey, "First reply"), transport))).ErrorCode);
        await CompleteContinuityTaskAsync(factory.Services, id, first, transport);
        var second = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        Assert.Equal(secondTaskId, second.TaskId);
        Assert.Equal(16, second.Context.CandidateFirstSequence);
        Assert.Equal(20, second.Context.CandidateLastSequence);
        await CompleteContinuityTaskAsync(factory.Services, id, second, transport);
        // The two replies are sequences 26/27. Ordinary post-snapshot history 21..25
        // remains in the next candidate interval, including those Agent replies.
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker Third range after takeover");
        var third = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
        Assert.Equal(21, third.Context.CandidateFirstSequence);
        Assert.Equal(28, third.Context.CandidateLastSequence);
        if (nextBudget == 16000)
        {
            Assert.Equal(2, third.Context.Messages.Count(m => m.AuthorKind == "agent"));
            Assert.Equal(8, third.Context.Messages.Count);
        }
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var all = await final.AgentReplyTasks.OrderBy(t => t.TriggerSequence).ToArrayAsync();
        Assert.Equal(3, all.Length);
        Assert.Equal(AgentReplyTaskState.Replied, all[0].State);
        Assert.Equal(AgentReplyTaskState.Replied, all[1].State);
        Assert.Equal(AgentReplyTaskState.Delivered, all[2].State);
        Assert.Equal(20, (await final.AgentContextCursors.SingleAsync()).DeliveredThroughSequence);
        Assert.Equal(28, (await final.AgentContextCursors.SingleAsync()).AllocatedThroughSequence);
    }

    private static async Task CompleteContinuityTaskAsync(IServiceProvider services, Guid connectionId,
        AgentMentionResultDto task, RoundTableMeeting.Abstraction.Services.McpTransportSessionKey transport)
    {
        Assert.True((await ReliableForTestAsync(services, s => s.ConfirmReliableAsync(new(connectionId, "task",
            TaskId: task.TaskId, DeliveryAttemptId: task.DeliveryAttemptId), transport))).Succeeded);
        Assert.True((await ReliableForTestAsync(services, s => s.ReplyReliableAsync(new(connectionId, task.TaskId,
            task.DeliveryAttemptId, task.ReplyIdempotencyKey, "Continuity reply"), transport))).Succeeded);
    }
}
