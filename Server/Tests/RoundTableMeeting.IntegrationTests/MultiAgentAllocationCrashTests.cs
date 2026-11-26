using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DerivedAllocationSurvivesCrashBeforeOrAfterCommitWithoutCompletingTheEvent(bool afterCommit)
    {
        await using var host = new RecoveryProcessHost(faultInjection: true,
            configuration: new Dictionary<string, string> { ["RealtimeChat__EventLeaseSeconds"] = "2" });
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Allocation crash");
        using var oldHttp = host.Client();
        await using var oldSdk = await CreateSdkAsync(oldHttp);
        var key = new string('A', 43);
        var joined = (await ProcessCallAsync(oldSdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = key })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker start");
        var root = await CrashMentionAsync(oldSdk, id);
        var rootId = root.GetProperty("taskId").GetGuid();
        var attemptId = root.GetProperty("deliveryAttemptId").GetGuid();
        await ProcessCallAsync(oldSdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId = rootId, deliveryAttemptId = attemptId });
        host.ArmCrash("allocation", afterCommit);
        var reply = (await ProcessCallAsync(oldSdk, "reply_to_mention", new { connectionSessionId = id, taskId = rootId,
            deliveryAttemptId = attemptId, replyIdempotencyKey = root.GetProperty("replyIdempotencyKey").GetGuid(), content = "@Worker continue" })).GetProperty("data");
        var replyId = reply.GetProperty("message").GetProperty("messageId").GetGuid();
        try { await host.WaitForCrashPointAsync(); }
        finally { await host.CrashAsync(); }
        Guid deliveryId;
        Guid? committedChildId;
        await using (var cut = host.Database())
        {
            Assert.Equal(afterCommit ? 2 : 1, await cut.AgentReplyTasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(afterCommit ? 2 : 1, (await cut.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AllocatedThroughSequence);
            committedChildId = await cut.AgentReplyTasks.Where(task => task.ParentTaskId == rootId).Select(task => (Guid?)task.ID).SingleOrDefaultAsync(cancellationToken: TestContext.Current.CancellationToken);
            var eventId = (await cut.MessageAppendedEvents.SingleAsync(item => item.MessageId == replyId, cancellationToken: TestContext.Current.CancellationToken)).ID;
            var delivery = await cut.MessageEventDeliveries.SingleAsync(item => item.EventId == eventId && item.ConsumerName == "agent-mention-task-v1", cancellationToken: TestContext.Current.CancellationToken);
            deliveryId = delivery.ID;
            Assert.Equal(MessageEventDeliveryState.Leased, delivery.State);
            Assert.Null(delivery.CompletedAtUtc);
        }
        await host.StartAsync();
        using var currentHttp = host.Client();
        await using var current = await CreateSdkAsync(currentHttp);
        await ProcessCallAsync(current, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() });
        var child = await CrashMentionAsync(current, id);
        var childId = child.GetProperty("taskId").GetGuid();
        if (afterCommit) Assert.Equal(committedChildId, childId);
        Assert.Equal(rootId, child.GetProperty("collaboration").GetProperty("parentTaskId").GetGuid());
        Assert.Equal(root.GetProperty("triggerMessageId").GetGuid(), child.GetProperty("collaboration").GetProperty("chainId").GetGuid());
        Assert.Equal(replyId, child.GetProperty("triggerMessageId").GetGuid());
        await using var final = host.Database();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (!await final.MessageEventDeliveries.AnyAsync(item => item.ID == deliveryId && item.State == MessageEventDeliveryState.Completed, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "Recovered event was not completed.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.Equal(2, await final.AgentReplyTasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await final.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, (await final.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AllocatedThroughSequence);
        Assert.Equal(0, await final.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
