using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongConfirmationDeadlineNeedsLeaseAndCommittedConfirmationReplaysAfterItsDeadline(bool stopRenewal)
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AgentCollaboration:RecoveryScanIntervalSeconds"] = "3600", ["AgentCollaboration:ContextConfirmationTimeoutSeconds"] = "300" }));
            builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); });
        });
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Independent confirmation and lease clocks");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "Initial historical message");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var joined = (await ActivationCallAsync(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        // Initial confirmation is not subject to the task confirmation deadline.
        foreach (var elapsed in new[] { 110, 110, 90 })
        {
            time.Now += TimeSpan.FromSeconds(elapsed);
            Assert.True((await ActivationCallAsync(sdk, "renew_agent_lease", new { connectionSessionId = id })).GetProperty("succeeded").GetBoolean());
        }
        var initialArgs = new { connectionSessionId = id, kind = "initial", initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() };
        var confirmedInitial = (await ActivationCallAsync(sdk, "confirm_context", initialArgs)).GetProperty("data");
        Assert.Equal(1, confirmedInitial.GetProperty("deliveredThroughSequence").GetInt64());
        Assert.True((await ActivationCallAsync(sdk, "confirm_context", initialArgs)).GetProperty("data").GetProperty("replayed").GetBoolean());
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker delayed confirmation task");
        var task = (await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id })).GetProperty("data");
        var taskId = task.GetProperty("taskId").GetGuid();
        var attemptId = task.GetProperty("deliveryAttemptId").GetGuid();
        var deadline = task.GetProperty("confirmationDeadline").GetDateTimeOffset();
        var confirmArgs = new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId };
        if (stopRenewal)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
                time.Now = (await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).LeaseExpiresAtUtc!.Value;
            Assert.True(time.Now < deadline);
            Assert.Equal("connection_expired", (await ActivationCallAsync(sdk, "confirm_context", confirmArgs)).GetProperty("errorCode").GetString());
            await using var scopeAfter = factory.Services.CreateAsyncScope();
            var db = scopeAfter.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(AgentReplyTaskState.Delivered, (await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
            Assert.Null((await db.AgentTaskDeliveryAttempts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ConfirmedAtUtc);
            Assert.Equal(1, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeliveredThroughSequence);
            return;
        }
        foreach (var elapsed in new[] { 110, 110, 70 })
        {
            time.Now += TimeSpan.FromSeconds(elapsed);
            Assert.True((await ActivationCallAsync(sdk, "renew_agent_lease", new { connectionSessionId = id })).GetProperty("succeeded").GetBoolean());
        }
        Assert.True(time.Now < deadline);
        Assert.False((await ActivationCallAsync(sdk, "confirm_context", confirmArgs)).GetProperty("data").GetProperty("replayed").GetBoolean());
        var confirmedAt = time.Now;
        time.Now = deadline.AddSeconds(1);
        var replay = (await ActivationCallAsync(sdk, "confirm_context", confirmArgs)).GetProperty("data");
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal("reply_to_mention", replay.GetProperty("nextAction").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(confirmedAt, (await db.AgentTaskDeliveryAttempts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ConfirmedAtUtc);
            Assert.Equal(confirmedAt.AddSeconds(120), (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).LeaseExpiresAtUtc);
            Assert.Equal(2, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeliveredThroughSequence);
        }
        Assert.True((await ActivationCallAsync(sdk, "reply_to_mention", new { connectionSessionId = id, taskId,
            deliveryAttemptId = attemptId, replyIdempotencyKey = task.GetProperty("replyIdempotencyKey").GetGuid(),
            content = "Confirmed task completed after the former confirmation deadline" })).GetProperty("succeeded").GetBoolean());
        var completed = (await ActivationCallAsync(sdk, "confirm_context", confirmArgs)).GetProperty("data");
        Assert.True(completed.GetProperty("replayed").GetBoolean());
        Assert.Equal("wait_for_mention", completed.GetProperty("nextAction").GetString());
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(3, await final.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(attemptId, (await final.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).CurrentAttemptId);
        Assert.Equal(1, await final.AgentTaskDeliveryAttempts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
