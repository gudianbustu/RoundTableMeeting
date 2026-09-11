using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ReliableReplyFailureBetweenMessageAppendAndTaskMarkRollsBackEveryWrite()
    {
        using var original = new TestApplicationFactory();
        var fault = new AppendFailureSwitch();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IResolvedMessageAppendCore>();
            services.AddSingleton(fault);
            services.AddScoped<ResolvedMessageAppendCore>();
            services.AddScoped<IResolvedMessageAppendCore, FailingAgentAppendCore>();
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reliable append rollback");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var joined = await ActivationCallAsync(sdk, "join_meeting", new
        {
            meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43)
        });
        var data = joined.GetProperty("data");
        var id = data.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", new
        { connectionSessionId = id, kind = "initial", initialDeliveryId = data.GetProperty("initialDeliveryId").GetGuid() })).GetProperty("succeeded").GetBoolean());
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first task");
        var task = (await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id })).GetProperty("data");
        var taskId = task.GetProperty("taskId").GetGuid();
        var attemptId = task.GetProperty("deliveryAttemptId").GetGuid();
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", new
        { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId })).GetProperty("succeeded").GetBoolean());
        var replyArgs = new { connectionSessionId = id, taskId, deliveryAttemptId = attemptId,
            replyIdempotencyKey = task.GetProperty("replyIdempotencyKey").GetGuid(), content = "@Alice result" };
        var failed = await ActivationCallAsync(sdk, "reply_to_mention", replyArgs);
        Assert.Equal("agent_operation_unavailable", failed.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("private-database", failed.GetRawText());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(1, await db.MeetingMessages.CountAsync());
            Assert.Equal(1, await db.MessageMentions.CountAsync());
            Assert.Equal(1, await db.MessageAppendedEvents.CountAsync());
            Assert.Equal(2, await db.MessageEventDeliveries.CountAsync());
            var persisted = await db.AgentReplyTasks.SingleAsync();
            Assert.Equal(AgentReplyTaskState.Processing, persisted.State);
            Assert.Null(persisted.ReplyMessageId);
            Assert.Equal(attemptId, persisted.CurrentAttemptId);
            Assert.Equal(AgentTaskDeliveryAttemptState.Confirmed, (await db.AgentTaskDeliveryAttempts.SingleAsync()).State);
        }
        fault.Fail = false;
        var reply = await ActivationCallAsync(sdk, "reply_to_mention", replyArgs);
        Assert.True(reply.GetProperty("succeeded").GetBoolean());
        Assert.Equal(2, reply.GetProperty("data").GetProperty("message").GetProperty("sequence").GetInt64());
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(2, await final.MeetingMessages.CountAsync());
        Assert.Equal(2, await final.MessageMentions.CountAsync());
        Assert.Equal(2, await final.MessageAppendedEvents.CountAsync());
        Assert.Equal(4, await final.MessageEventDeliveries.CountAsync());
        Assert.Equal(AgentReplyTaskState.Replied, (await final.AgentReplyTasks.SingleAsync()).State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TakeoverWithoutPendingTasksPreservesEmptyOrCompletedAllocationBoundary(bool confirmFirst, bool completeFirstTask)
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Takeover allocation boundary");
        var transport = RecoveryTransport(243);
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services,
            ReliableJoinRequest(meeting.MeetingCode, "Worker", "create"), transport)).Data);
        Assert.Equal(0, joined.InitialContext.SnapshotSequence);
        Assert.Empty(joined.InitialContext.Messages);
        var id = joined.Connection.ConnectionSessionId;
        if (confirmFirst)
            Assert.True((await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport))).Succeeded);
        if (completeFirstTask)
        {
            await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker prior task");
            var first = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services, s => s.WaitReliableAsync(new(id), transport))).Data);
            await CompleteContinuityTaskAsync(factory.Services, id, first, transport);
        }
        for (var i = 0; i < 3; i++) await PostHumanMessageAsync(browser, meeting.MeetingId, "Ordinary history " + i);
        var nextTransport = RecoveryTransport(244);
        var taken = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services,
            ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover"), nextTransport)).Data);
        var nextId = taken.Connection.ConnectionSessionId;
        var confirmation = Assert.IsType<AgentConfirmContextResultDto>((await ReliableForTestAsync(factory.Services,
            s => s.ConfirmReliableAsync(new(nextId, "initial", taken.InitialDeliveryId), nextTransport))).Data);
        Assert.Equal(completeFirstTask ? 1 : 0, confirmation.DeliveredThroughSequence);
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker next task");
        var next = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services,
            s => s.WaitReliableAsync(new(nextId), nextTransport))).Data);
        Assert.Equal(completeFirstTask ? 2 : 1, next.Context.CandidateFirstSequence);
        Assert.Equal(completeFirstTask ? 6 : 4, next.Context.CandidateLastSequence);
        Assert.Equal(completeFirstTask ? 5 : 4, next.Context.Messages.Count);
        await CompleteContinuityTaskAsync(factory.Services, nextId, next, nextTransport);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.All(await db.AgentReplyTasks.ToArrayAsync(), t => Assert.Equal(AgentReplyTaskState.Replied, t.State));
        Assert.Equal(completeFirstTask ? 2 : 1, await db.AgentReplyTasks.CountAsync());
        Assert.Equal(completeFirstTask ? 6 : 4, (await db.AgentContextCursors.SingleAsync()).DeliveredThroughSequence);
    }
}
