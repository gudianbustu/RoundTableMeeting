using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Repositories;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContextInvariantFailureBlocksButTemporaryReadFailureRollsBack(bool replaying, bool transient)
    {
        using var original = new TestApplicationFactory();
        var fault = new ContextReadFault();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMessageRepository>();
            services.AddScoped<MessageRepository>();
            services.AddScoped<IMessageRepository, FaultingContextRepository>();
            services.AddSingleton(fault);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Context failure classification");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var joined = (await ActivationCallAsync(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() })).GetProperty("succeeded").GetBoolean());
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first task");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker second task");
        await EventuallyAsync(factory, async db => await db.AgentReplyTasks.CountAsync() == 2);
        Guid? attemptId = null;
        if (replaying)
            attemptId = (await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id })).GetProperty("data").GetProperty("deliveryAttemptId").GetGuid();
        fault.Mode = transient ? "temporary" : "missing";
        var failure = await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id });
        Assert.Equal(transient ? "agent_operation_unavailable" : "retry_exhausted", failure.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("private-context", failure.GetRawText());
        Guid taskId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var tasks = await db.AgentReplyTasks.OrderBy(t => t.TriggerSequence).ToArrayAsync();
            taskId = tasks[0].ID;
            Assert.Equal(transient ? replaying ? AgentReplyTaskState.Delivered : AgentReplyTaskState.Queued : AgentReplyTaskState.Failed, tasks[0].State);
            Assert.Equal(AgentReplyTaskState.Queued, tasks[1].State);
            Assert.Equal(attemptId, tasks[0].CurrentAttemptId);
            Assert.Equal(replaying ? 1 : 0, await db.AgentTaskDeliveryAttempts.CountAsync());
            Assert.Equal(0, tasks[0].AutomaticRetryCount);
            Assert.Equal(0, tasks[0].RetryCycle);
            Assert.Equal(!transient, tasks[0].RetryExhausted);
            Assert.Equal(transient ? null : "context_unavailable", tasks[0].FailureCode);
            Assert.Equal(0, (await db.AgentContextCursors.SingleAsync()).DeliveredThroughSequence);
            Assert.Equal(2, await db.MeetingMessages.CountAsync());
            if (replaying)
                Assert.Equal(!transient, (await db.AgentTaskDeliveryAttempts.SingleAsync()).InvalidatedAtUtc.HasValue);
        }
        fault.Mode = null;
        if (!transient)
            Assert.True((await ActivationCallAsync(sdk, "retry_task", new { connectionSessionId = id, taskId,
                operationId = Guid.NewGuid() })).GetProperty("succeeded").GetBoolean());
        var recovered = (await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id })).GetProperty("data");
        Assert.Equal(taskId, recovered.GetProperty("taskId").GetGuid());
        var nextAttempt = recovered.GetProperty("deliveryAttemptId").GetGuid();
        if (replaying && transient) Assert.Equal(attemptId, nextAttempt);
        else Assert.NotEqual(attemptId, nextAttempt);
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId,
            deliveryAttemptId = nextAttempt })).GetProperty("succeeded").GetBoolean());
        Assert.True((await ActivationCallAsync(sdk, "reply_to_mention", new { connectionSessionId = id, taskId, deliveryAttemptId = nextAttempt,
            replyIdempotencyKey = recovered.GetProperty("replyIdempotencyKey").GetGuid(), content = "Recovered context" })).GetProperty("succeeded").GetBoolean());
    }

    private sealed class ContextReadFault { public string? Mode { get; set; } }

    private sealed class FaultingContextRepository(MessageRepository core, ContextReadFault fault) : IMessageRepository
    {
        public Task<IReadOnlyList<MessageMention>> ListMentionsForMessagesAsync(Guid meetingId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
            => core.ListMentionsForMessagesAsync(meetingId, messageIds, cancellationToken);

        public async Task<IReadOnlyList<MeetingMessage>> ListAfterSequenceThroughAsync(Guid meetingId, long after, long through, CancellationToken token = default)
        {
            if (fault.Mode == "temporary") throw new InvalidOperationException("injected-private-context-database-failure");
            var rows = await core.ListAfterSequenceThroughAsync(meetingId, after, through, token);
            return fault.Mode == "missing" ? rows.Skip(1).ToArray() : rows;
        }
        public Task<MeetingMessage?> FindBySubmissionAsync(Guid meetingId, Guid participantId, Guid submissionId, CancellationToken token = default) => core.FindBySubmissionAsync(meetingId, participantId, submissionId, token);
        public Task<MeetingMessage?> FindSystemBySubmissionAsync(Guid meetingId, Guid submissionId, CancellationToken token = default) => core.FindSystemBySubmissionAsync(meetingId, submissionId, token);
        public Task<long> GetNextSequenceAsync(Guid meetingId, CancellationToken token = default) => core.GetNextSequenceAsync(meetingId, token);
        public Task<MessageSequenceSummary> GetSequenceSummaryAsync(Guid meetingId, CancellationToken token = default) => core.GetSequenceSummaryAsync(meetingId, token);
        public Task<IReadOnlyList<MeetingMessage>> ListThroughSequenceAsync(Guid meetingId, long through, CancellationToken token = default) => core.ListThroughSequenceAsync(meetingId, through, token);
        public Task<IReadOnlyList<MeetingMessage>> ListAsync(Guid meetingId, long? before, long? after, long? through, int limit, CancellationToken token = default) => core.ListAsync(meetingId, before, after, through, limit, token);
        public Task<bool> HasBeforeAsync(Guid meetingId, long sequence, CancellationToken token = default) => core.HasBeforeAsync(meetingId, sequence, token);
        public Task<bool> HasAfterAsync(Guid meetingId, long sequence, long? through, CancellationToken token = default) => core.HasAfterAsync(meetingId, sequence, through, token);
        public Task<MeetingMessage?> FindByIdAsync(Guid meetingId, Guid id, CancellationToken token = default) => core.FindByIdAsync(meetingId, id, token);
        public Task<IReadOnlyList<MessageMention>> ListMentionsAsync(Guid meetingId, Guid messageId, CancellationToken token = default) => core.ListMentionsAsync(meetingId, messageId, token);
    }
}
