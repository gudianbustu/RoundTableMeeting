using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
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
    public async Task CommittedActivatingOperationReplayCannotReviveAVersionReplacedByTakeover(string mode, bool takeoverFirst)
    {
        var resuming = mode == "resume";
        using var original = new TestApplicationFactory();
        var fault = new AfterActivatingCommitFault();
        var coordinator = new OrderedAgentCoordinator();
        using var factory = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["AgentCollaboration:RecoveryScanIntervalSeconds"] = "3600" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMeetingTransactionRunner>();
                services.AddScoped<MeetingTransactionRunner>();
                services.AddScoped<IMeetingTransactionRunner, AfterActivatingCommitRunner>();
                services.AddSingleton(fault);
                services.RemoveAll<IAgentJoinCoordinator>();
                services.AddSingleton<IAgentJoinCoordinator>(coordinator);
            });
        });
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Activating replay competition");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var first = await CreateSdkAsync(http);
        await using var second = await CreateSdkAsync(http);
        await using var taker = await CreateSdkAsync(http);
        var key = new string('A', 43);
        var join = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key };
        Guid id;
        object operation;
        var operationName = resuming ? "resume_agent" : "join_meeting";
        var pendingSdk = mode == "create" ? first : second;
        if (mode != "create")
        {
            var joined = await ActivationCallAsync(first, "join_meeting", join);
            id = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            operation = resuming
                ? new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() }
                : new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 10000,
                    reliabilityMode = "explicit_ack_v1", joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = key };
        }
        else { id = Guid.Empty; operation = join; }
        fault.Armed = true;
        var failed = await ActivationCallAsync(pendingSdk, operationName, operation);
        Assert.Equal("agent_operation_unavailable", failed.GetProperty("errorCode").GetString());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var pending = await db.AgentConnectionSessions.SingleAsync(c => c.State == AgentConnectionState.Activating, cancellationToken: TestContext.Current.CancellationToken);
            if (resuming) Assert.Equal(id, pending.ID);
            id = pending.ID;
            Assert.Equal(AgentConnectionState.Activating, pending.State);
            Assert.Equal(resuming ? 2 : 1, pending.BindingVersion);
            Assert.Equal("explicit_ack_v1", (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).RequiredReliabilityMode);
        }
        var round = coordinator.Arm();
        var takeover = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 6000,
            reliabilityMode = "explicit_ack_v1", joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = key };
        Task<System.Text.Json.JsonElement> Replay() => ActivationCallAsync(pendingSdk, operationName, operation);
        Task<System.Text.Json.JsonElement> Takeover() => ActivationCallAsync(taker, "join_meeting", takeover);
        var firstCall = takeoverFirst ? Takeover() : Replay();
        Task<System.Text.Json.JsonElement>? secondCall = null;
        try
        {
            await round.Held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            secondCall = takeoverFirst ? Replay() : Takeover();
            await round.Contended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(secondCall.IsCompleted);
        }
        finally { round.Release.TrySetResult(); }
        var outcomes = await Task.WhenAll(firstCall, secondCall!).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var replay = outcomes[takeoverFirst ? 1 : 0];
        var taken = outcomes[takeoverFirst ? 0 : 1];
        Assert.True(taken.GetProperty("succeeded").GetBoolean(), taken.GetProperty("errorCode").GetString());
        var nextId = taken.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        Assert.NotEqual(id, nextId);
        if (takeoverFirst) Assert.Equal("connection_superseded", replay.GetProperty("errorCode").GetString());
        else
        {
            Assert.True(replay.GetProperty("succeeded").GetBoolean());
            Assert.True(replay.GetProperty("data").GetProperty("replayed").GetBoolean());
            Assert.Equal(resuming ? 2 : 1, replay.GetProperty("data").GetProperty("connection").GetProperty("bindingVersion").GetInt64());
        }
        var obsolete = await ActivationCallAsync(pendingSdk, operationName, operation);
        Assert.Equal("connection_superseded", obsolete.GetProperty("errorCode").GetString());
        Assert.True((await ActivationCallAsync(taker, "renew_agent_lease", new { connectionSessionId = nextId })).GetProperty("succeeded").GetBoolean());
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var cursor = await final.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(nextId, cursor.CurrentConnectionId);
        Assert.Equal(mode == "takeover" ? 3 : 2, cursor.ConnectionGeneration);
        Assert.Equal(1, await final.AgentConnectionSessions.CountAsync(c => c.State == AgentConnectionState.Connected, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(AgentConnectionState.Superseded, (await final.AgentConnectionSessions.SingleAsync(c => c.ID == id, cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Equal(mode == "create" ? 2 : 3, await final.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await final.MeetingParticipants.CountAsync(p => p.ParticipantType == ParticipantType.Agent, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAfterCommittedBindingInterruptionRetiresEveryOlderRegistration(bool takeover)
    {
        using var original = new TestApplicationFactory();
        var fault = new AfterActivatingCommitFault();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMeetingTransactionRunner>();
            services.AddScoped<MeetingTransactionRunner>();
            services.AddScoped<IMeetingTransactionRunner, AfterActivatingCommitRunner>();
            services.AddSingleton(fault);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Interrupted ownership handoff");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var old = await CreateSdkAsync(http);
        await using var interrupted = await CreateSdkAsync(http);
        await using var recovery = await CreateSdkAsync(http);
        var key = new string('A', 43);
        var joined = await ActivationCallAsync(old, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = key });
        var oldId = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        fault.Armed = true;
        var failed = takeover
            ? await ActivationCallAsync(interrupted, "join_meeting", new { meetingCode = meeting.MeetingCode,
                displayName = "Worker", contextCharacterLimit = 6000, reliabilityMode = "explicit_ack_v1",
                joinMode = "takeover", operationId = Guid.NewGuid(), recoveryKey = key })
            : await ActivationCallAsync(interrupted, "resume_agent", new { connectionSessionId = oldId,
                operationId = Guid.NewGuid(), recoveryKey = key });
        Assert.Equal("agent_operation_unavailable", failed.GetProperty("errorCode").GetString());
        AgentConnectionRegistration pending;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var cursor = await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            var connection = await db.AgentConnectionSessions.SingleAsync(c => c.ID == cursor.CurrentConnectionId, cancellationToken: TestContext.Current.CancellationToken);
            pending = new(connection.MeetingId, connection.ParticipantId, connection.ID, connection.McpTransportBinding)
            { ConnectionGeneration = connection.ConnectionGeneration, BindingVersion = connection.BindingVersion,
                ReliabilityMode = connection.ReliabilityMode };
        }
        // Reconcile the interrupted activation without closing the older transport.
        // Its stale in-memory registration must not permanently prevent new resumes.
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<RoundTableMeeting.Application.AgentCollaboration.AgentCollaborationService>()
                .ReconcileConnectionAsync(pending, cancellationToken: TestContext.Current.CancellationToken);
        var resumed = await ActivationCallAsync(recovery, "resume_agent", new { connectionSessionId = pending.ConnectionSessionId,
            recoveryKey = key, operationId = Guid.NewGuid() });
        Assert.True(resumed.GetProperty("succeeded").GetBoolean(), resumed.GetProperty("errorCode").GetString());
        Assert.Equal(pending.ConnectionSessionId, resumed.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid());
        var registrations = factory.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(meeting.MeetingId);
        var current = Assert.Single(registrations);
        Assert.Equal(pending.BindingVersion + 1, current.BindingVersion);
        Assert.Equal(pending.ConnectionGeneration, current.ConnectionGeneration);
        await old.DisposeAsync();
        Assert.True((await ActivationCallAsync(recovery, "renew_agent_lease", new { connectionSessionId = pending.ConnectionSessionId })).GetProperty("succeeded").GetBoolean());
    }

    private sealed class AfterActivatingCommitFault { public bool Armed { get; set; } }

    private sealed class AfterActivatingCommitRunner(MeetingTransactionRunner core, RoundTableMeetingDbContext db,
        AfterActivatingCommitFault fault) : IMeetingTransactionRunner
    {
        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            var result = await core.RunAsync(action, cancellationToken);
            if (fault.Armed && await db.AgentConnectionSessions.AnyAsync(c => c.State == AgentConnectionState.Activating, cancellationToken))
            {
                fault.Armed = false;
                throw new InvalidOperationException("Injected interruption after durable activation intent.");
            }
            return result;
        }
    }
}
