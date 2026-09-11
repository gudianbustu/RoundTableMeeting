using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ReliableTaskErrorsPreserveThreeItemFifoAndOldAttemptsCannotSubmitAfterReplacement()
    {
        await using var test = await SdkTaskBoundary.CreateAsync(3);
        var first = await test.Call("wait_for_mention", new { connectionSessionId = test.Id });
        var taskId = first.GetProperty("taskId").GetGuid();
        var oldAttempt = first.GetProperty("deliveryAttemptId").GetGuid();
        var replyKey = first.GetProperty("replyIdempotencyKey").GetGuid();
        var retry = new { connectionSessionId = test.Id, taskId, operationId = Guid.NewGuid() };
        var failure = new { connectionSessionId = test.Id, taskId, deliveryAttemptId = oldAttempt, failureCode = "agent_transient_failure" };
        await test.RejectUnchanged("retry_task", retry, "retry_not_required");
        await test.RejectUnchanged("report_task_failure", failure, "task_not_available");
        await test.Call("confirm_context", new { connectionSessionId = test.Id, kind = "task", taskId, deliveryAttemptId = oldAttempt });
        await test.RejectUnchanged("retry_task", retry, "task_in_progress");
        foreach (var code in new[] { "private-unrecognized-failure", "connection_taken_over", "AGENT_TRANSIENT_FAILURE", "" })
            await test.RejectUnchanged("report_task_failure", failure with { failureCode = code }, "failure_code_invalid");
        var decision = await test.Call("report_task_failure", failure);
        Assert.False(decision.GetProperty("retryExhausted").GetBoolean());
        var pending = await test.Call("wait_for_mention", new { connectionSessionId = test.Id }, "retry_pending");
        await test.RejectUnchanged("retry_task", retry, "retry_not_required");
        await test.RejectUnchanged("report_task_failure", failure with { failureCode = "agent_permanent_failure" }, "operation_conflict");
        var due = pending.GetProperty("nextRetryAt").GetDateTimeOffset();
        var failedFacts = await test.Facts(includeConnection: false);
        test.Clock.Now = test.Clock.Now.AddSeconds(1);
        await test.Call("renew_agent_lease", new { connectionSessionId = test.Id });
        Assert.Equal(failedFacts, await test.Facts(includeConnection: false));
        await test.Call("wait_for_mention", new { connectionSessionId = test.Id }, "retry_pending");
        test.Clock.Now = due;
        var replacement = await test.Call("wait_for_mention", new { connectionSessionId = test.Id });
        Assert.Equal(taskId, replacement.GetProperty("taskId").GetGuid());
        var nextAttempt = replacement.GetProperty("deliveryAttemptId").GetGuid();
        Assert.NotEqual(oldAttempt, nextAttempt);
        var afterReplacement = await test.Facts();
        Assert.True((await test.Call("wait_for_mention", new { connectionSessionId = test.Id })).GetProperty("replayed").GetBoolean());
        Assert.Equal(afterReplacement, await test.Facts());
        await test.RejectUnchanged("confirm_context", new { connectionSessionId = test.Id, kind = "task", taskId,
            deliveryAttemptId = oldAttempt }, "delivery_attempt_stale");
        await test.RejectUnchanged("reply_to_mention", new { connectionSessionId = test.Id, taskId, deliveryAttemptId = oldAttempt,
            replyIdempotencyKey = replyKey, content = "Late obsolete attempt" }, "delivery_attempt_stale");
        var replay = await test.Call("report_task_failure", failure);
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(decision.GetProperty("nextRetryAt").GetDateTimeOffset(), replay.GetProperty("nextRetryAt").GetDateTimeOffset());
        Assert.Equal(afterReplacement, await test.Facts());
        await test.RejectUnchanged("report_task_failure", failure with { failureCode = "agent_permanent_failure" }, "operation_conflict");
        await using (var scope = test.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var tasks = await db.AgentReplyTasks.OrderBy(t => t.TriggerSequence).ToArrayAsync();
            Assert.Equal(new[] { AgentReplyTaskState.Delivered, AgentReplyTaskState.Queued, AgentReplyTaskState.Queued }, tasks.Select(t => t.State));
            Assert.Equal(1, tasks[0].AutomaticRetryCount);
            Assert.Equal(nextAttempt, (await db.AgentTaskDeliveryAttempts.SingleAsync(a => a.ID == oldAttempt)).ReplacementAttemptId);
        }
        var seen = new HashSet<Guid>();
        var current = replacement;
        for (var i = 1; i <= 3; i++)
        {
            var currentTask = current.GetProperty("taskId").GetGuid();
            Assert.True(seen.Add(currentTask));
            await test.Call("confirm_context", new { connectionSessionId = test.Id, kind = "task", taskId = currentTask,
                deliveryAttemptId = current.GetProperty("deliveryAttemptId").GetGuid() });
            await test.Call("reply_to_mention", new { connectionSessionId = test.Id, taskId = currentTask,
                deliveryAttemptId = current.GetProperty("deliveryAttemptId").GetGuid(),
                replyIdempotencyKey = current.GetProperty("replyIdempotencyKey").GetGuid(), content = "Completed task " + i });
            if (i < 3) current = await test.Call("wait_for_mention", new { connectionSessionId = test.Id });
        }
        await using var finalScope = test.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(6, await final.MeetingMessages.CountAsync());
        Assert.All(await final.AgentReplyTasks.ToArrayAsync(), task => Assert.Equal(AgentReplyTaskState.Replied, task.State));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task StatusObservesConfirmationOverdueWithoutWritingAndWaitThenScanFailsOnlyOnce(int quota)
    {
        await using var test = await SdkTaskBoundary.CreateAsync(1);
        test.Services.GetRequiredService<AgentCollaborationOptions>().MaximumAutomaticRetries = quota;
        var mention = await test.Call("wait_for_mention", new { connectionSessionId = test.Id });
        var before = await test.Facts();
        var normal = (await test.Call("get_agent_status", new { connectionSessionId = test.Id })).GetProperty("queueHead");
        Assert.Equal("awaiting_context_confirmation", normal.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, normal.GetProperty("failureCode").ValueKind);
        Assert.Equal("wait_for_mention", normal.GetProperty("nextAction").GetString());
        Assert.Equal(before, await test.Facts());
        test.Clock.Now = mention.GetProperty("confirmationDeadline").GetDateTimeOffset();
        var overdue = (await test.Call("get_agent_status", new { connectionSessionId = test.Id })).GetProperty("queueHead");
        Assert.Equal("confirmation_overdue", overdue.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, overdue.GetProperty("failureCode").ValueKind);
        Assert.Equal("wait_for_mention", overdue.GetProperty("nextAction").GetString());
        Assert.Equal(before, await test.Facts());
        await test.Call("wait_for_mention", new { connectionSessionId = test.Id }, quota == 0 ? "retry_exhausted" : "retry_pending");
        var failed = await test.Facts();
        var registration = Assert.Single(test.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(test.MeetingId));
        await using (var scope = test.Services.CreateAsyncScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>().ReconcileConnectionAsync(registration));
        Assert.Equal(failed, await test.Facts());
        var status = (await test.Call("get_agent_status", new { connectionSessionId = test.Id })).GetProperty("queueHead");
        Assert.Equal("confirmation_timeout", status.GetProperty("failureCode").GetString());
        Assert.Equal(quota == 0 ? "retry_exhausted" : "retry_pending", status.GetProperty("reason").GetString());
        Assert.Equal(quota == 0 ? "await_explicit_retry" : "wait_for_mention", status.GetProperty("nextAction").GetString());
        Assert.Equal(failed, await test.Facts());
    }

    [Fact]
    public async Task ReplayedProductionWindowIgnoresChangedServiceBudgetsAndConfirmationTimeout()
    {
        await using var test = await SdkTaskBoundary.CreateAsync(1);
        var first = await test.Call("wait_for_mention", new { connectionSessionId = test.Id });
        var before = await test.Facts(includeConnection: false);
        var options = test.Services.GetRequiredService<AgentCollaborationOptions>();
        options.DefaultContextCharacterLimit = options.MaximumContextCharacterLimit = 256;
        options.ContextConfirmationTimeoutSeconds = 1;
        test.Clock.Now += TimeSpan.FromSeconds(2);
        var replay = await test.Call("wait_for_mention", new { connectionSessionId = test.Id });
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        foreach (var field in new[] { "taskId", "deliveryAttemptId", "replyIdempotencyKey", "confirmationDeadline", "context" })
            Assert.True(JsonElement.DeepEquals(first.GetProperty(field), replay.GetProperty(field)), field);
        // A legal repeated wait may renew its lease, but cannot re-clip or extend the original delivery.
        Assert.Equal(before, await test.Facts(includeConnection: false));
        await using var scope = test.Services.CreateAsyncScope();
        Assert.Equal(test.Clock.Now.AddSeconds(120),
            (await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync()).LeaseExpiresAtUtc);
    }

    private sealed class SdkTaskBoundary : IAsyncDisposable
    {
        private readonly TestApplicationFactory original = new();
        private WebApplicationFactory<Program> factory = null!;
        private HttpClient browser = null!, http = null!;
        private McpClient sdk = null!;
        public RecoveryClock Clock { get; } = new(DateTimeOffset.UtcNow);
        public IServiceProvider Services => factory.Services;
        public Guid Id { get; private set; }
        public Guid MeetingId { get; private set; }
        public static async Task<SdkTaskBoundary> CreateAsync(int taskCount)
        {
            var test = new SdkTaskBoundary();
            try
            {
                test.factory = test.original.WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                        { ["AgentCollaboration:RecoveryScanIntervalSeconds"] = "3600" }));
                    builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(test.Clock); });
                });
                test.browser = test.factory.CreateClient(new() { HandleCookies = true });
                await LoginAsync(test.browser, "alice", "test-password-alice");
                var meeting = await CreateMeetingAsync(test.browser, "Reliable task error matrix");
                test.MeetingId = meeting.MeetingId;
                test.http = test.factory.CreateClient(new() { HandleCookies = false });
                test.sdk = await CreateSdkAsync(test.http);
                var joined = await test.Call("join_meeting", new { meetingCode = meeting.MeetingCode, displayName = "Worker",
                    contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) });
                test.Id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
                await test.Call("confirm_context", new { connectionSessionId = test.Id, kind = "initial", initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
                for (var i = 1; i <= taskCount; i++) await PostHumanMessageAsync(test.browser, meeting.MeetingId, "@Worker question " + i);
                await EventuallyAsync(test.factory, async db => await db.AgentReplyTasks.CountAsync() == taskCount);
                return test;
            }
            catch { await test.DisposeAsync(); throw; }
        }
        public async Task<JsonElement> Call(string name, object args, string? error = null)
        {
            var response = await ActivationCallAsync(sdk, name, args);
            Assert.Equal(error, response.GetProperty("errorCode").GetString());
            Assert.Equal(error is null, response.GetProperty("succeeded").GetBoolean());
            Assert.DoesNotContain("private-unrecognized-failure", response.GetRawText(), StringComparison.Ordinal);
            return response.GetProperty("data");
        }
        public async Task RejectUnchanged(string name, object args, string error)
        {
            var before = await Facts();
            await Call(name, args, error);
            Assert.Equal(before, await Facts());
        }
        public async Task<string> Facts(bool includeConnection = true)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            return JsonSerializer.Serialize(new
            {
                Connection = includeConnection ? await db.AgentConnectionSessions.Select(c => new { c.ID, c.State, c.BindingVersion,
                    c.ConnectionGeneration, c.LeaseExpiresAtUtc, c.LastActivityAtUtc }).SingleAsync() : null,
                Tasks = await db.AgentReplyTasks.OrderBy(t => t.ID).ToArrayAsync(),
                Attempts = await db.AgentTaskDeliveryAttempts.OrderBy(a => a.ID).ToArrayAsync(),
                Cursor = await db.AgentContextCursors.SingleAsync(),
                Messages = await db.MeetingMessages.CountAsync(), Receipts = await db.AgentRecoveryOperations.CountAsync()
            });
        }
        public async ValueTask DisposeAsync()
        {
            if (sdk is not null) await sdk.DisposeAsync();
            http?.Dispose(); browser?.Dispose(); factory?.Dispose(); original.Dispose();
        }
    }
}
