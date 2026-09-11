using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task SdkSupportsPersistedConcurrentLeaseAndNewSessionBindingWithoutOldCloseDisconnectingIt()
    {
        // Capability experiment only: the isolated probe is NOT the production recovery service.
        var probe = new RecoveryCapabilityProbe();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        var renew = McpServerTool.Create(probe.RenewAsync, new()
        { Name = "probe_renew", UseStructuredContent = true, SerializerOptions = options });
        var resume = McpServerTool.Create(probe.ResumeAsync, new()
        { Name = "probe_resume", UseStructuredContent = true, SerializerOptions = options });
        using var original = new TestApplicationFactory();
        var signals = new ObservedTaskSignals();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentTaskSignalRegistry>();
            services.AddSingleton<IAgentTaskSignalRegistry>(signals);
            services.AddMcpServer().WithTools([renew, resume]);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        probe.Services = factory.Services;
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Recovery capability experiment");
        using var http1 = factory.CreateClient(new() { HandleCookies = false });
        using var http2 = factory.CreateClient(new() { HandleCookies = false });
        await using var oldSdk = await CreateSdkAsync(http1);
        await using var newSdk = await CreateSdkAsync(http2);
        var joined = SdkPayload(await oldSdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "SdkWorker", ["contextCharacterLimit"] = 12000
        }));
        var connectionId = joined.GetProperty("data").GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        var args = new Dictionary<string, object?> { ["connectionSessionId"] = connectionId };
        await probe.InitializeAsync(meeting.MeetingId);
        var wait = oldSdk.CallToolAsync("wait_for_mention", args).AsTask();
        await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        probe.Now = probe.Now.AddSeconds(30);
        var renewed = SdkPayload(await oldSdk.CallToolAsync("probe_renew", args).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(renewed.GetProperty("succeeded").GetBoolean());
        Assert.Equal(probe.Now.AddSeconds(120), renewed.GetProperty("data").GetProperty("leaseExpiresAt").GetDateTimeOffset());
        Assert.Equal(probe.Now.AddSeconds(120).UtcTicks, await probe.ReadLeaseAsync());
        Assert.False(wait.IsCompleted);

        var resumed = SdkPayload(await newSdk.CallToolAsync("probe_resume", args).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(resumed.GetProperty("succeeded").GetBoolean());
        Assert.Equal(connectionId, resumed.GetProperty("data").GetProperty("connectionSessionId").GetGuid());
        Assert.Equal(2, resumed.GetProperty("data").GetProperty("bindingVersion").GetInt64());
        AssertToolFailure(SdkPayload(await wait.WaitAsync(TimeSpan.FromSeconds(5))), "connection_invalid");
        await oldSdk.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await EventuallyAsync(factory, _ => Task.FromResult(!factory.Services.GetRequiredService<IAgentConnectionRegistry>()
            .ContainsTransport(probe.FirstTransport!.Value)));

        var status = SdkPayload(await newSdk.CallToolAsync("get_agent_status", args));
        Assert.True(status.GetProperty("succeeded").GetBoolean());
        Assert.Equal("connected", status.GetProperty("data").GetProperty("connection").GetProperty("state").GetString());
        probe.Now = probe.Now.AddSeconds(30);
        Assert.True(SdkPayload(await newSdk.CallToolAsync("probe_renew", args)).GetProperty("succeeded").GetBoolean());
        Assert.Equal(probe.Now.AddSeconds(120).UtcTicks, await probe.ReadLeaseAsync());
        await newSdk.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await EventuallyAsync(factory, async db => await db.AgentConnectionSessions.AnyAsync(item =>
            item.ID == connectionId && item.State == AgentConnectionState.Disconnected));
    }

    private sealed class RecoveryCapabilityProbe
    {
        public IServiceProvider Services { get; set; } = null!;
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public McpTransportSessionKey? FirstTransport { get; private set; }
        private AgentConnectionRegistration current = null!;
        private IAgentConnectionRegistry Registry => Services.GetRequiredService<IAgentConnectionRegistry>();

        public async Task InitializeAsync(Guid meetingId)
        {
            current = Assert.Single(Registry.ListRegistrations(meetingId));
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            // A single test-owned row proves persistent I/O while the real SDK request remains suspended.
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE RecoveryCapabilityLease (Id INTEGER PRIMARY KEY CHECK (Id = 1), Binding TEXT NOT NULL, Version INTEGER NOT NULL, Expires INTEGER NOT NULL)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO RecoveryCapabilityLease VALUES (1, {current.TransportBinding}, 1, {Now.AddSeconds(120).UtcTicks})");
        }

        public async Task<long> ReadLeaseAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().Database
                .SqlQueryRaw<long>("SELECT Expires AS Value FROM RecoveryCapabilityLease").SingleAsync();
        }

        public async Task<AgentOperationResult> RenewAsync(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken)
        {
            var key = McpTransportSessionAccessor.FromSessionId(server.SessionId)!.Value;
            if (!Registry.TryGetRegistration(key, out var registration) || registration.ConnectionSessionId != connectionSessionId
                || Registry.GetBindingCancellationToken(key, registration).IsCancellationRequested)
                return AgentOperationResult.Failure("connection_invalid", "Probe binding rejected.");
            FirstTransport ??= key;
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            var changed = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE RecoveryCapabilityLease SET Expires = MAX(Expires, {Now.AddSeconds(120).UtcTicks}) WHERE Id = 1 AND Binding = {registration.TransportBinding} AND Expires > {Now.UtcTicks}", cancellationToken);
            return changed == 1
                ? AgentOperationResult.Success(new AgentLeaseResultDto(Now, new DateTimeOffset(await ReadLeaseAsync(), TimeSpan.Zero), 30), "Probe lease persisted.")
                : AgentOperationResult.Failure("connection_invalid", "Probe lease rejected.");
        }

        public async Task<AgentOperationResult> ResumeAsync(Guid connectionSessionId, McpServer server, CancellationToken cancellationToken)
        {
            // Authentication, operation receipts and recovery state machines are deliberately outside this experiment.
            var key = McpTransportSessionAccessor.FromSessionId(server.SessionId)!.Value;
            await using var reservation = await Registry.ReserveTransportAsync(key, cancellationToken);
            if (connectionSessionId != current.ConnectionSessionId || Registry.ContainsTransport(key))
                return AgentOperationResult.Failure("connection_invalid", "Probe binding rejected.");
            // This test adapter replaces the durable binding as well as the transport registration.
            var previous = current;
            var activeBeforeResume = Registry.ListRegistrations(current.MeetingId).Single();
            Assert.Equal(previous, activeBeforeResume);
            Assert.True(Registry.Retire(previous));
            var next = previous with { TransportBinding = key.ToStorageBinding(), ConnectionGeneration = 1, BindingVersion = 2 };
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            Assert.Equal(1, await db.AgentConnectionSessions.Where(item => item.ID == connectionSessionId && item.McpTransportBinding == previous.TransportBinding)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.McpTransportBinding, next.TransportBinding)
                    .SetProperty(item => item.BindingVersion, next.BindingVersion), cancellationToken));
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE RecoveryCapabilityLease SET Binding = {next.TransportBinding}, Version = 2 WHERE Id = 1 AND Binding = {previous.TransportBinding}", cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            Assert.True(Registry.TryActivate(key, next, null));
            current = next;
            return AgentOperationResult.Success(new { connectionSessionId, bindingVersion = next.BindingVersion }, "Probe binding replaced.");
        }
    }
}
