using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("unlink", false)]
    [InlineData("cleanup", false)]
    [InlineData("cleanup", true)]
    public async Task RealProcessDeletionCleanupCrashPreservesBarrierAndRestoresParentReferences(string boundary, bool afterCommit)
    {
        await using var host = new RecoveryProcessHost(faultInjection: true);
        await host.StartAsync();
        using var browser = host.Client(cookies: true);
        await LoginAsync(browser, "alice", "test-password-alice");
        const string name = "Cleanup crash collaboration";
        var meeting = await CreateMeetingAsync(browser, name);
        var other = await CreateMeetingAsync(browser, "Surviving meeting");
        await PostHumanMessageAsync(browser, other.MeetingId, "Other meeting history");
        using var http = host.Client();
        await using var sdk = await CreateSdkAsync(http);
        var joined = (await ProcessCallAsync(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ProcessCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial",
            initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker start");
        var parent = await CrashMentionAsync(sdk, id);
        var taskId = parent.GetProperty("taskId").GetGuid();
        var attemptId = parent.GetProperty("deliveryAttemptId").GetGuid();
        await ProcessCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId });
        await ProcessCallAsync(sdk, "reply_to_mention", new { connectionSessionId = id, taskId, deliveryAttemptId = attemptId,
            replyIdempotencyKey = parent.GetProperty("replyIdempotencyKey").GetGuid(), content = "@Worker continue" });
        var child = await CrashMentionAsync(sdk, id);
        Assert.Equal(taskId, child.GetProperty("collaboration").GetProperty("parentTaskId").GetGuid());
        Guid[] eventIds;
        Guid receiptId;
        Guid ownerId;
        await using (var before = host.Database())
        {
            eventIds = await before.MessageAppendedEvents.Where(item => item.MeetingId == meeting.MeetingId).Select(item => item.ID).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
            var receipt = await before.MeetingCreationReceipts.SingleAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken);
            receiptId = receipt.ID;
            ownerId = receipt.CreatorUserId;
        }
        host.ArmCrash(boundary, afterCommit);
        using var deletion = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meeting.MeetingId}/deletion")
            { Content = JsonContent.Create(new { confirmationName = name }) };
        deletion.Headers.Add("Origin", "http://localhost:5173");
        using var response = await browser.SendAsync(deletion, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        try
        {
            await host.WaitForCrashPointAsync();
            using var read = await browser.GetAsync($"/api/meetings/{meeting.MeetingId}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Gone, read.StatusCode);
            using var lateWrite = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meeting.MeetingId}/messages")
                { Content = JsonContent.Create(new { clientSubmissionId = Guid.NewGuid(), content = "late message" }) };
            lateWrite.Headers.Add("Origin", "http://localhost:5173");
            using var rejectedWrite = await browser.SendAsync(lateWrite, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Gone, rejectedWrite.StatusCode);
            if (!afterCommit)
            {
                await using var pendingCleanup = host.Database();
                Assert.True(await pendingCleanup.Meetings.AnyAsync(item => item.ID == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.Equal(2, await pendingCleanup.AgentReplyTasks.CountAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
            }
            using var repeat = new HttpRequestMessage(HttpMethod.Post, $"/api/meetings/{meeting.MeetingId}/deletion")
                { Content = JsonContent.Create(new { confirmationName = name }) };
            repeat.Headers.Add("Origin", "http://localhost:5173");
            using var repeated = await browser.SendAsync(repeat, TestContext.Current.CancellationToken);
            Assert.Equal(afterCommit ? HttpStatusCode.NoContent : HttpStatusCode.Accepted, repeated.StatusCode);
        }
        finally { await host.CrashAsync(); }
        await using (var cut = host.Database())
        {
            if (afterCommit) Assert.False(await cut.Meetings.AnyAsync(item => item.ID == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
            else
            {
                Assert.Equal("Deleting", (await cut.Meetings.SingleAsync(item => item.ID == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken)).LifecycleState.ToString());
                Assert.Equal(taskId, (await cut.AgentReplyTasks.SingleAsync(task => task.ID == child.GetProperty("taskId").GetGuid(), cancellationToken: TestContext.Current.CancellationToken)).ParentTaskId);
                Assert.Equal(2, await cut.AgentReplyTasks.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            }
            Assert.Equal(0, await cut.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
        }
        await host.StartAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            await using var final = host.Database();
            if (!await final.Meetings.AnyAsync(item => item.ID == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken))
            {
                Assert.False(await final.AgentReplyTasks.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.AgentCollaborationBoundaries.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.AgentTaskDeliveryAttempts.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.AgentConnectionSessions.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.AgentContextCursors.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.AgentInitialContextDeliveries.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.AgentRecoveryOperations.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.MessageEventDeliveries.AnyAsync(item => eventIds.Contains(item.EventId), cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.MessageAppendedEvents.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.MessageMentions.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.MeetingMessages.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await final.MeetingParticipants.AnyAsync(item => item.MeetingId == meeting.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                var receipt = await final.MeetingCreationReceipts.SingleAsync(item => item.ID == receiptId, cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal("Deleted", receipt.State.ToString());
                Assert.Null(receipt.MeetingId);
                Assert.Null(receipt.ExpiresAtUtc);
                Assert.True(await final.UserAccounts.AnyAsync(item => item.ID == ownerId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.Equal("Active", (await final.Meetings.SingleAsync(item => item.ID == other.MeetingId, cancellationToken: TestContext.Current.CancellationToken)).LifecycleState.ToString());
                Assert.Equal("Other meeting history", (await final.MeetingMessages.SingleAsync(item => item.MeetingId == other.MeetingId, cancellationToken: TestContext.Current.CancellationToken)).Content);
                Assert.True(await final.AgentCollaborationBoundaries.AnyAsync(item => item.MeetingId == other.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
                Assert.Equal(0, await final.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
                break;
            }
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Deletion recovery did not finish.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        using var recoveredHttp = host.Client();
        await using var recovered = await CreateSdkAsync(recoveredHttp);
        var denied = await ActivationCallAsync(recovered, "resume_agent", new { connectionSessionId = id,
            recoveryKey = new string('A', 43), operationId = Guid.NewGuid() });
        Assert.False(denied.GetProperty("succeeded").GetBoolean());
        using var visible = await browser.GetAsync($"/api/meetings/{other.MeetingId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        await PostHumanMessageAsync(browser, other.MeetingId, "Other meeting still writable");
    }
}
