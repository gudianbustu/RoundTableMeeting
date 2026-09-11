using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task ReliableActivationFaultRequiresNewTransportAndPreservesCommittedIdentity(bool resuming, int faultMode)
    {
        using var original = new TestApplicationFactory();
        var registry = new ActivationFaultRegistry();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentConnectionRegistry>();
            services.AddSingleton<IAgentConnectionRegistry>(registry);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reliable activation boundary");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var first = await CreateSdkAsync(http);
        await using var second = await CreateSdkAsync(http);
        await using var recovery = await CreateSdkAsync(http);
        var key = new string('A', 43);
        var join = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = key };
        Guid id;
        var failedSdk = resuming ? second : first;
        var failedOperation = Guid.NewGuid();
        if (resuming)
        {
            var joined = await ActivationCallAsync(first, "join_meeting", join);
            Assert.True(joined.GetProperty("succeeded").GetBoolean());
            id = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
            registry.Arm(faultMode);
            var failed = await ActivationCallAsync(second, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = failedOperation });
            Assert.Equal("agent_operation_unavailable", failed.GetProperty("errorCode").GetString());
        }
        else
        {
            registry.Arm(faultMode);
            var failed = await ActivationCallAsync(first, "join_meeting", join);
            Assert.Equal("agent_operation_unavailable", failed.GetProperty("errorCode").GetString());
            await using var scope = factory.Services.CreateAsyncScope();
            id = (await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync()).ID;
        }
        Guid initialId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var connection = await db.AgentConnectionSessions.SingleAsync();
            Assert.Equal(AgentConnectionState.Disconnected, connection.State);
            Assert.Equal(id, connection.ID);
            Assert.Equal(resuming ? 2 : 1, connection.BindingVersion);
            Assert.Equal(1, connection.ConnectionGeneration);
            initialId = (await db.AgentInitialContextDeliveries.SingleAsync()).ID;
            Assert.Equal(id, (await db.AgentContextCursors.SingleAsync()).CurrentConnectionId);
        }
        if (resuming)
        {
            var replay = await ActivationCallAsync(failedSdk, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = failedOperation });
            Assert.Equal("operation_superseded", replay.GetProperty("errorCode").GetString());
        }
        else
        {
            var replay = await ActivationCallAsync(failedSdk, "join_meeting", join);
            Assert.True(replay.GetProperty("succeeded").GetBoolean());
            Assert.Equal("recovery_required", replay.GetProperty("data").GetProperty("kind").GetString());
            Assert.Equal(id, replay.GetProperty("data").GetProperty("connectionSessionId").GetGuid());
        }
        var sameTransport = await ActivationCallAsync(failedSdk, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() });
        Assert.Equal("connection_invalid", sameTransport.GetProperty("errorCode").GetString());
        var resumed = await ActivationCallAsync(recovery, "resume_agent", new { connectionSessionId = id, recoveryKey = key, operationId = Guid.NewGuid() });
        Assert.True(resumed.GetProperty("succeeded").GetBoolean());
        Assert.Equal(id, resumed.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid());
        Assert.Equal(resuming ? 3 : 2, resumed.GetProperty("data").GetProperty("connection").GetProperty("bindingVersion").GetInt64());
        Assert.Equal(initialId, resumed.GetProperty("data").GetProperty("initialDeliveryId").GetGuid());
        await using var finalScope = factory.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(1, await final.AgentConnectionSessions.CountAsync());
        Assert.Equal(1, await final.AgentInitialContextDeliveries.CountAsync());
        Assert.Equal(resuming ? 3 : 2, await final.AgentRecoveryOperations.CountAsync());
    }

    private static async Task<JsonElement> ActivationCallAsync(McpClient sdk, string name, object args)
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args))!;
        return SdkPayload(await sdk.CallToolAsync(name, arguments).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class ActivationFaultRegistry : IAgentConnectionRegistry
    {
        private readonly AgentConnectionRegistry inner = new();
        private int mode = -1;
        public void Arm(int faultMode) => Interlocked.Exchange(ref mode, faultMode);
        public bool TryActivate(McpTransportSessionKey session, AgentConnectionRegistration registration, AgentConnectionRegistration? previous)
        {
            var fault = Interlocked.Exchange(ref mode, -1);
            if (fault == 0) return false;
            if (fault == 2) throw new InvalidOperationException("Activation interrupted before registration.");
            var activated = inner.TryActivate(session, registration, previous);
            if (fault == 1) { inner.Retire(registration); return false; }
            if (fault == 3) throw new InvalidOperationException("Activation interrupted after registration.");
            return activated;
        }
        public ValueTask<IAsyncDisposable> ReserveTransportAsync(McpTransportSessionKey session, CancellationToken cancellationToken = default) => inner.ReserveTransportAsync(session, cancellationToken);
        public bool Retire(AgentConnectionRegistration registration) => inner.Retire(registration);
        public CancellationToken GetBindingCancellationToken(McpTransportSessionKey session, AgentConnectionRegistration registration) => inner.GetBindingCancellationToken(session, registration);
        public void TerminateMeeting(Guid meetingId) => inner.TerminateMeeting(meetingId);
        public bool ContainsTransport(McpTransportSessionKey session) => inner.ContainsTransport(session);
        public bool IsRegistered(Guid meetingId, Guid participantId, Guid connectionId, string binding) => inner.IsRegistered(meetingId, participantId, connectionId, binding);
        public IReadOnlyList<AgentConnectionRegistration> ListRegistrations(Guid meetingId) => inner.ListRegistrations(meetingId);
        public IReadOnlyList<AgentConnectionRegistration> RemoveRegistrations(McpTransportSessionKey session) => inner.RemoveRegistrations(session);
        public bool TryGetRegistration(McpTransportSessionKey session, out AgentConnectionRegistration registration) => inner.TryGetRegistration(session, out registration);
        public bool Unregister(McpTransportSessionKey session, Guid connectionId) => inner.Unregister(session, connectionId);
        public bool TryRegister(McpTransportSessionKey session, AgentConnectionRegistration registration) => inner.TryRegister(session, registration);
    }
}
