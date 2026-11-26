using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;
using RoundTableMeeting.Repository.Sqlite.Transactions;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("resume", false)]
    [InlineData("resume", true)]
    [InlineData("takeover", false)]
    [InlineData("takeover", true)]
    public async Task FailedActivationCompensationRollsBackAndAllowsReplayOrScannerRecovery(string mode, bool scanFirst)
    {
        using var original = new TestApplicationFactory();
        var registry = new ActivationFaultRegistry();
        var fault = new CompensationTransactionFault();
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AgentCollaboration:RecoveryScanIntervalSeconds"] = "3600" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAgentConnectionRegistry>();
                services.AddSingleton<IAgentConnectionRegistry>(registry);
                services.RemoveAll<IMeetingTransactionRunner>();
                services.AddScoped<MeetingTransactionRunner>();
                services.AddScoped<IMeetingTransactionRunner, CompensationTransactionRunner>();
                services.AddSingleton(fault);
            });
        });
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Compensation transaction failure");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var first = await CreateSdkAsync(http);
        await using var pendingSdk = await CreateSdkAsync(http);
        await using var recoverySdk = await CreateSdkAsync(http);
        var key = new string('A', 43);
        var join = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key };
        object operation = join;
        if (mode != "create")
        {
            var joined = await ActivationCallAsync(first, "join_meeting", join);
            var originalId = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            operation = mode == "resume"
                ? new { connectionSessionId = originalId, recoveryKey = key, operationId = Guid.NewGuid() }
                : new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 6000,
                    reliabilityMode = "explicit_ack_v1", joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = key };
        }
        var operationName = mode == "resume" ? "resume_agent" : "join_meeting";
        fault.Armed = true;
        registry.Arm(0);
        var failed = await ActivationCallAsync(pendingSdk, operationName, operation);
        Assert.Equal("agent_operation_unavailable", failed.GetProperty("errorCode").GetString());
        Assert.True(fault.Triggered);
        Assert.Empty(registry.ListRegistrations(meeting.MeetingId));
        AgentConnectionRegistration pending;
        Guid initialId;
        int receiptCount;
        long revision;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var cursor = await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            var connection = await db.AgentConnectionSessions.SingleAsync(c => c.ID == cursor.CurrentConnectionId, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(AgentConnectionState.Activating, connection.State);
            Assert.Null(connection.DisconnectedAtUtc);
            Assert.Equal(mode == "resume" ? 2 : 1, connection.BindingVersion);
            Assert.Equal(mode == "takeover" ? 2 : 1, connection.ConnectionGeneration);
            revision = cursor.ActivityRevision;
            initialId = (await db.AgentInitialContextDeliveries.SingleAsync(i => i.ConnectionSessionId == connection.ID, cancellationToken: TestContext.Current.CancellationToken)).ID;
            receiptCount = await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken);
            pending = new(connection.MeetingId, connection.ParticipantId, connection.ID, connection.McpTransportBinding)
            { ConnectionGeneration = connection.ConnectionGeneration, BindingVersion = connection.BindingVersion,
                ReliabilityMode = connection.ReliabilityMode };
        }
        if (scanFirst)
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<AgentCollaborationService>();
                Assert.True(await service.ReconcileConnectionAsync(pending, cancellationToken: TestContext.Current.CancellationToken));
                Assert.False(await service.ReconcileConnectionAsync(pending, cancellationToken: TestContext.Current.CancellationToken));
            }
            var replay = await ActivationCallAsync(pendingSdk, operationName, operation);
            if (mode == "resume") Assert.Equal("operation_superseded", replay.GetProperty("errorCode").GetString());
            else Assert.Equal("recovery_required", replay.GetProperty("data").GetProperty("kind").GetString());
            var recovered = await ActivationCallAsync(recoverySdk, "resume_agent", new { connectionSessionId = pending.ConnectionSessionId,
                recoveryKey = key, operationId = Guid.NewGuid() });
            Assert.True(recovered.GetProperty("succeeded").GetBoolean());
        }
        else
        {
            var replay = await ActivationCallAsync(pendingSdk, operationName, operation);
            Assert.True(replay.GetProperty("succeeded").GetBoolean());
            Assert.True(replay.GetProperty("data").GetProperty("replayed").GetBoolean());
            Assert.Equal(initialId, replay.GetProperty("data").GetProperty("initialDeliveryId").GetGuid());
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<AgentCollaborationService>().ReconcileConnectionAsync(pending, cancellationToken: TestContext.Current.CancellationToken));
        }
        var current = Assert.Single(registry.ListRegistrations(meeting.MeetingId));
        Assert.Equal(pending.ConnectionSessionId, current.ConnectionSessionId);
        Assert.Equal(pending.ConnectionGeneration, current.ConnectionGeneration);
        Assert.Equal(pending.BindingVersion + (scanFirst ? 1 : 0), current.BindingVersion);
        await first.DisposeAsync();
        if (scanFirst) await pendingSdk.DisposeAsync();
        var activeSdk = scanFirst ? recoverySdk : pendingSdk;
        Assert.True((await ActivationCallAsync(activeSdk, "renew_agent_lease", new { connectionSessionId = pending.ConnectionSessionId }))
            .GetProperty("succeeded").GetBoolean());
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(AgentConnectionState.Connected, (await final.AgentConnectionSessions.SingleAsync(c => c.ID == pending.ConnectionSessionId, cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Equal(receiptCount + (scanFirst ? 1 : 0), await final.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(revision + (scanFirst ? 3 : 1), (await final.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ActivityRevision);
    }

    private sealed class CompensationTransactionFault
    {
        public bool Armed { get; set; }
        public bool Triggered { get; set; }
    }

    private sealed class CompensationTransactionRunner(MeetingTransactionRunner core, RoundTableMeetingDbContext db,
        CompensationTransactionFault fault) : IMeetingTransactionRunner
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
            => core.RunAsync(async token =>
            {
                var activating = fault.Armed ? await db.AgentConnectionSessions.AsNoTracking()
                    .Where(c => c.State == AgentConnectionState.Activating).Select(c => c.ID).ToArrayAsync(token) : [];
                var result = await action(token);
                if (fault.Armed && activating.Length > 0 && await db.AgentConnectionSessions.AsNoTracking()
                    .AnyAsync(c => activating.Contains(c.ID) && c.State == AgentConnectionState.Disconnected, token))
                {
                    fault.Armed = false;
                    fault.Triggered = true;
                    throw new InvalidOperationException("Injected failure after compensation writes before commit.");
                }
                return result;
            }, cancellationToken);
    }
}
