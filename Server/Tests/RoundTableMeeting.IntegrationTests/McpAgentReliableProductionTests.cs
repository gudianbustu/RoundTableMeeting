using System.Text.Json;
using Json.Schema;
using ModelContextProtocol.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ProductionReliableWaitAllowsConcurrentRenewalAndResumeCancelsOnlyOldBinding()
    {
        using var original = new TestApplicationFactory();
        var signals = new ObservedTaskSignals();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentTaskSignalRegistry>(); services.AddSingleton<IAgentTaskSignalRegistry>(signals);
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Production concurrent recovery");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var oldSdk = await CreateSdkAsync(http);
        await using var newSdk = await CreateSdkAsync(http);
        var key = new string('A', 43);
        var joined = SdkPayload(await oldSdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000,
            ["reliabilityMode"] = "explicit_ack_v1", ["operationId"] = Guid.NewGuid(), ["recoveryKey"] = key
        })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.True(SdkPayload(await oldSdk.CallToolAsync("confirm_context", new Dictionary<string, object?>
        { ["connectionSessionId"] = id, ["kind"] = "initial", ["initialDeliveryId"] = joined.GetProperty("initialDeliveryId").GetGuid() })).GetProperty("succeeded").GetBoolean());
        var args = new Dictionary<string, object?> { ["connectionSessionId"] = id };
        var wait = oldSdk.CallToolAsync("wait_for_mention", args).AsTask();
        await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        time.Now = time.Now.AddSeconds(30);
        var renewed = SdkPayload(await oldSdk.CallToolAsync("renew_agent_lease", args).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(renewed.GetProperty("succeeded").GetBoolean());
        Assert.False(wait.IsCompleted);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync();
            Assert.Equal(renewed.GetProperty("data").GetProperty("leaseExpiresAt").GetDateTimeOffset(), saved.LeaseExpiresAtUtc);
        }
        var resumed = SdkPayload(await newSdk.CallToolAsync("resume_agent", new Dictionary<string, object?>
        { ["connectionSessionId"] = id, ["recoveryKey"] = key, ["operationId"] = Guid.NewGuid() }).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(resumed.GetProperty("succeeded").GetBoolean(), resumed.GetRawText());
        var oldResult = SdkPayload(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(oldResult.GetProperty("succeeded").GetBoolean());
        Assert.Equal("connection_invalid", oldResult.GetProperty("errorCode").GetString());
        await oldSdk.DisposeAsync();
        Assert.True(SdkPayload(await newSdk.CallToolAsync("get_agent_status", args)).GetProperty("succeeded").GetBoolean());
        await newSdk.DisposeAsync();
        await EventuallyAsync(factory, async db => await db.AgentConnectionSessions.AnyAsync(item => item.ID == id && item.State == AgentConnectionState.Disconnected));
    }

    [Fact]
    public async Task ProductionSdkReliableToolsCompleteFailureRetryResumeAndReplyCycle()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Production reliable SDK");
        using var http = CreateAnonymousClient(factory);
        await using var sdk = await CreateSdkAsync(http);
        var tools = await sdk.ListToolsAsync();
        var schemas = tools.ToDictionary(tool => tool.Name, tool => JsonSchema.FromText(tool.ProtocolTool.OutputSchema!.Value.GetRawText()));
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        async Task<JsonElement> Call(McpClient client, string name, object args, string? error = null)
        {
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
            var result = SdkPayload(await client.CallToolAsync(name, arguments));
            Assert.True(schemas[name].Evaluate(result, new() { RequireFormatValidation = true }).IsValid, name + " output schema");
            Assert.DoesNotContain(key, result.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(error is null, result.GetProperty("succeeded").GetBoolean());
            Assert.Equal(error, result.GetProperty("errorCode").GetString());
            return result.GetProperty("data");
        }
        var joined = await Call(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key });
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.Equal("joined", joined.GetProperty("kind").GetString());
        await Call(sdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 }, "initial_context_unconfirmed");
        await Call(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await Call(sdk, "renew_agent_lease", new { connectionSessionId = id });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker first");
        var task = await Call(sdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 });
        var taskId = task.GetProperty("taskId").GetGuid();
        var attempt = task.GetProperty("deliveryAttemptId").GetGuid();
        var replyKey = task.GetProperty("replyIdempotencyKey").GetGuid();
        await Call(sdk, "reply_to_mention", new { connectionSessionId = id, taskId, deliveryAttemptId = attempt, replyIdempotencyKey = replyKey, content = "premature" }, "context_confirmation_required");
        await Call(sdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attempt });
        await Call(sdk, "report_task_failure", new { connectionSessionId = id, taskId, deliveryAttemptId = attempt, failureCode = "agent_permanent_failure" });
        await Call(sdk, "wait_for_mention", new { connectionSessionId = id }, "retry_exhausted");
        var retryOperation = Guid.NewGuid();
        await Call(sdk, "retry_task", new { connectionSessionId = id, taskId, operationId = retryOperation });
        Assert.True((await Call(sdk, "retry_task", new { connectionSessionId = id, taskId, operationId = retryOperation })).GetProperty("replayed").GetBoolean());
        task = await Call(sdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 });
        attempt = task.GetProperty("deliveryAttemptId").GetGuid();
        Assert.Equal(replyKey, task.GetProperty("replyIdempotencyKey").GetGuid());
        await Call(sdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attempt });
        var replyArgs = new { connectionSessionId = id, taskId, deliveryAttemptId = attempt, replyIdempotencyKey = replyKey, content = "@Alice completed" };
        var reply = await Call(sdk, "reply_to_mention", replyArgs);
        Assert.Single(reply.GetProperty("message").GetProperty("mentions").EnumerateArray());
        await using var resumedSdk = await CreateSdkAsync(http);
        var resumed = await Call(resumedSdk, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() });
        Assert.Equal(2, resumed.GetProperty("connection").GetProperty("bindingVersion").GetInt64());
        await Call(sdk, "get_agent_status", new { connectionSessionId = id }, "connection_invalid");
        var replay = await Call(resumedSdk, "reply_to_mention", replyArgs);
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(reply.GetProperty("message").GetProperty("messageId").GetGuid(), replay.GetProperty("message").GetProperty("messageId").GetGuid());
        var status = await Call(resumedSdk, "get_agent_status", new { connectionSessionId = id, taskId });
        Assert.Equal("replied", status.GetProperty("requestedTask").GetProperty("state").GetString());
        Assert.Equal("idle", (await Call(resumedSdk, "wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 })).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ProductionLegacyToolsRejectRecoveryParametersAndOnlineTakeoverPermanentlyUpgradesIdentity()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Legacy upgrade SDK");
        using var http = CreateAnonymousClient(factory);
        await using var old = await CreateSdkAsync(http);
        var joined = SdkPayload(await old.CallToolAsync("join_meeting", new Dictionary<string, object?>
        { ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000 })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        foreach (var (name, args) in new[]
        {
            ("wait_for_mention", new Dictionary<string, object?> { ["connectionSessionId"] = id, ["waitTimeoutSeconds"] = 1 }),
            ("get_agent_status", new Dictionary<string, object?> { ["connectionSessionId"] = id, ["taskId"] = Guid.NewGuid() }),
            ("renew_agent_lease", new Dictionary<string, object?> { ["connectionSessionId"] = id })
        }) AssertToolFailure(SdkPayload(await old.CallToolAsync(name, args)), "reliability_mode_required");
        await using var replacement = await CreateSdkAsync(http);
        var takeover = SdkPayload(await replacement.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000,
            ["reliabilityMode"] = "explicit_ack_v1", ["joinMode"] = "takeover", ["operationId"] = Guid.NewGuid(),
            ["recoveryKey"] = new string('A', 43)
        }));
        Assert.True(takeover.GetProperty("succeeded").GetBoolean(), takeover.GetRawText());
        foreach (var tool in new[] { "wait_for_mention", "get_agent_status", "reply_to_mention" })
            AssertToolFailure(SdkPayload(await old.CallToolAsync(tool, new Dictionary<string, object?>
            { ["connectionSessionId"] = id, ["taskId"] = null, ["deliveryAttemptId"] = null, ["replyIdempotencyKey"] = null, ["content"] = null })), "connection_superseded");
        await using var downgrade = await CreateSdkAsync(http);
        AssertToolFailure(SdkPayload(await downgrade.CallToolAsync("join_meeting", new Dictionary<string, object?>
        { ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000 })), "reliability_mode_required");
    }
}
