using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task ReliableNamesUseHumanProtectionAndOneNormalizedAgentIdentity()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Normalized reliable identities");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var owner = await CreateSdkAsync(http);
        await using var contender = await CreateSdkAsync(http);
        foreach (var name in new[] { "Alice", " alice ", "ALICE" })
            foreach (var mode in new[] { "create", "takeover" })
            {
                var denied = await ActivationCallAsync(contender, "join_meeting", new { meetingCode = meeting.MeetingCode,
                    displayName = name, contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", joinMode = mode,
                    operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) });
                Assert.Equal("agent_name_in_use", denied.GetProperty("errorCode").GetString());
            }
        var joined = (await ActivationCallAsync(owner, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Café", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
        foreach (var name in new[] { " cafe\u0301 ", "CAFÉ" })
        {
            var denied = await ActivationCallAsync(contender, "join_meeting", new { meetingCode = meeting.MeetingCode,
                displayName = name, contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
                operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) });
            Assert.Equal("agent_connection_already_active", denied.GetProperty("errorCode").GetString());
        }
        var missing = await ActivationCallAsync(contender, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Misspelled", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1", joinMode = "takeover",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) });
        Assert.Equal("agent_not_found", missing.GetProperty("errorCode").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(2, await db.MeetingParticipants.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(joined.GetProperty("agent").GetProperty("participantId").GetGuid(),
            (await db.MeetingParticipants.SingleAsync(p => p.ParticipantType == ParticipantType.Agent, cancellationToken: TestContext.Current.CancellationToken)).ID);
    }

    [Fact]
    public async Task JoinReceiptCannotChangeModeAndCannotDisplaceALaterResumeOrTakeover()
    {
        using var factory = new TestApplicationFactory();
        using var browser = CreateBrowserClient(factory);
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Join receipt ownership");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var first = await CreateSdkAsync(http);
        await using var second = await CreateSdkAsync(http);
        await using var third = await CreateSdkAsync(http);
        var join = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", joinMode = "create", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) };
        var joined = (await ActivationCallAsync(first, "join_meeting", join)).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.Equal("operation_conflict", (await ActivationCallAsync(first, "join_meeting", join with { joinMode = "takeover" }))
            .GetProperty("errorCode").GetString());
        var resumed = await ActivationCallAsync(second, "resume_agent", new { connectionSessionId = id,
            recoveryKey = join.recoveryKey, operationId = Guid.NewGuid() });
        Assert.True(resumed.GetProperty("succeeded").GetBoolean());
        foreach (var sdk in new[] { first, third })
        {
            var replay = (await ActivationCallAsync(sdk, "join_meeting", join)).GetProperty("data");
            Assert.Equal("recovery_required", replay.GetProperty("kind").GetString());
            Assert.Equal(id, replay.GetProperty("connectionSessionId").GetGuid());
            var registration = Assert.Single(factory.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(meeting.MeetingId));
            Assert.Equal(id, registration.ConnectionSessionId);
            Assert.Equal(2, registration.BindingVersion);
        }
        // A new key explicitly takes ownership; the original recovery key grants no reverse takeover.
        var takeover = join with { joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = new string('B', 42) + "A" };
        Assert.True((await ActivationCallAsync(third, "join_meeting", takeover)).GetProperty("succeeded").GetBoolean());
        Assert.Equal("connection_superseded", (await ActivationCallAsync(first, "join_meeting", join)).GetProperty("errorCode").GetString());
        Assert.Equal("connection_superseded", (await ActivationCallAsync(second, "resume_agent", new { connectionSessionId = id,
            recoveryKey = join.recoveryKey, operationId = Guid.NewGuid() })).GetProperty("errorCode").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(2, await db.AgentConnectionSessions.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentContextCursors.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReliableCreateConvergesExpiredLeaseButPreservesLiveLegacyWithoutLease(bool legacy)
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AgentCollaboration:RecoveryScanIntervalSeconds"] = "3600" }));
            builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); });
        });
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Create admission at lease boundary");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var owner = await CreateSdkAsync(http);
        await using var contender = await CreateSdkAsync(http);
        var join = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) };
        var joined = (await ActivationCallAsync(owner, "join_meeting", legacy ?
            new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000 } : (object)join)).GetProperty("data");
        if (legacy) time.Now += TimeSpan.FromHours(1);
        else time.Now = joined.GetProperty("connection").GetProperty("leaseExpiresAt").GetDateTimeOffset();
        var denied = await ActivationCallAsync(contender, "join_meeting", join with { operationId = Guid.NewGuid() });
        Assert.Equal(legacy ? "agent_connection_already_active" : "agent_takeover_required", denied.GetProperty("errorCode").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var connection = await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(legacy ? AgentConnectionState.Connected : AgentConnectionState.Disconnected, connection.State);
            Assert.Equal(legacy ? 0 : 1, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
            if (legacy) Assert.Null(connection.LeaseExpiresAtUtc);
        }
        if (legacy) await owner.DisposeAsync();
        var offline = await ActivationCallAsync(contender, "join_meeting", join with { operationId = Guid.NewGuid() });
        Assert.Equal("agent_takeover_required", offline.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task AllReliableBoundToolsRejectExactLeaseExpiryWithoutMutatingOrDisclosingTaskState()
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AgentCollaboration:RecoveryScanIntervalSeconds"] = "3600" }));
            builder.ConfigureTestServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time); });
        });
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Exact lease expiry tool matrix");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        var joined = (await ActivationCallAsync(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker private pending task");
        var mention = (await ActivationCallAsync(sdk, "wait_for_mention", new { connectionSessionId = id })).GetProperty("data");
        var taskId = mention.GetProperty("taskId").GetGuid();
        var attemptId = mention.GetProperty("deliveryAttemptId").GetGuid();
        var replyKey = mention.GetProperty("replyIdempotencyKey").GetGuid();
        await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId });
        await using (var scope = factory.Services.CreateAsyncScope())
            time.Now = (await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).LeaseExpiresAtUtc!.Value;
        async Task<string> Facts()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            return JsonSerializer.Serialize(new { Connection = await db.AgentConnectionSessions.SingleAsync(),
                Task = await db.AgentReplyTasks.SingleAsync(), Attempt = await db.AgentTaskDeliveryAttempts.SingleAsync(),
                Cursor = await db.AgentContextCursors.SingleAsync(), Messages = await db.MeetingMessages.CountAsync(),
                Receipts = await db.AgentRecoveryOperations.CountAsync() });
        }
        var before = await Facts();
        var calls = new (string Name, object Args)[]
        {
            ("get_agent_status", new { connectionSessionId = id, taskId }),
            ("wait_for_mention", new { connectionSessionId = id, waitTimeoutSeconds = 1 }),
            ("renew_agent_lease", new { connectionSessionId = id }),
            ("confirm_context", new { connectionSessionId = id, kind = "task", taskId, deliveryAttemptId = attemptId }),
            ("reply_to_mention", new { connectionSessionId = id, taskId, deliveryAttemptId = attemptId, replyIdempotencyKey = replyKey, content = "Expired reply" }),
            ("report_task_failure", new { connectionSessionId = id, taskId, deliveryAttemptId = attemptId, failureCode = "agent_permanent_failure" }),
            ("retry_task", new { connectionSessionId = id, taskId, operationId = Guid.NewGuid() }),
        };
        foreach (var (name, args) in calls)
        {
            var denied = await ActivationCallAsync(sdk, name, args);
            Assert.Equal("connection_expired", denied.GetProperty("errorCode").GetString());
            var data = denied.GetProperty("data");
            Assert.Equal("resume_agent", data.GetProperty("nextAction").GetString());
            Assert.False(data.TryGetProperty("taskSummary", out var summary) && summary.ValueKind != JsonValueKind.Null);
            Assert.Equal(before, await Facts());
        }
    }
}
