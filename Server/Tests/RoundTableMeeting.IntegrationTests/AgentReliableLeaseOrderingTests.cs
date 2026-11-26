using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseRenewalAndTakeoverRespectBothOwnershipLockOrders(bool takeoverFirst)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        test.Clock.Now += TimeSpan.FromSeconds(30);
        Task<RoundTableMeeting.Abstraction.Contracts.AgentCollaboration.AgentOperationResult> Renew()
            => test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport));
        Task<RoundTableMeeting.Abstraction.Contracts.AgentCollaboration.AgentOperationResult> Takeover()
            => JoinReliableForTestAsync(test.Services, ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), RecoveryTransport(254));
        var (first, second) = await test.OrderAsync(takeoverFirst ? Takeover : Renew, takeoverFirst ? Renew : Takeover);
        Assert.True(first.Succeeded);
        Assert.Equal(!takeoverFirst, second.Succeeded);
        if (takeoverFirst) Assert.Equal("connection_superseded", second.ErrorCode);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var current = await db.AgentConnectionSessions.SingleAsync(c => c.State == AgentConnectionState.Connected, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(test.ConnectionId, current.ID);
        Assert.Equal(2, current.ConnectionGeneration);
        Assert.Equal(test.Clock.Now.AddSeconds(120), current.LeaseExpiresAtUtc);
        Assert.Equal(AgentConnectionState.Superseded, (await db.AgentConnectionSessions.SingleAsync(c => c.ID == test.ConnectionId, cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Equal(current.ID, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).CurrentConnectionId);
        Assert.Equal("connection_superseded", (await Renew()).ErrorCode);
        Assert.Equal(current.LeaseExpiresAtUtc, (await db.AgentConnectionSessions.AsNoTracking().SingleAsync(c => c.ID == current.ID, cancellationToken: TestContext.Current.CancellationToken)).LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task ProductionLongWaitAndOutOfOrderRenewalResponsesNeverShortenTheDurableLease()
    {
        using var original = new TestApplicationFactory();
        var time = new RecoveryClock(DateTimeOffset.UtcNow);
        var signals = new ObservedTaskSignals();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(time);
            services.RemoveAll<IAgentTaskSignalRegistry>(); services.AddSingleton<IAgentTaskSignalRegistry>(signals);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Long wait lease response ordering");
        using var delayed = new DelayedRenewalResponse(factory.Server.CreateHandler());
        using var http = new HttpClient(delayed) { BaseAddress = new Uri("http://localhost") };
        await using var sdk = await CreateSdkAsync(http);
        var joined = (await ActivationCallAsync(sdk, "join_meeting", new { meetingCode = meeting.MeetingCode,
            displayName = "Worker", contextCharacterLimit = 12000, reliabilityMode = "explicit_ack_v1",
            operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) })).GetProperty("data");
        var id = joined.GetProperty("connection").GetProperty("connectionSessionId").GetGuid();
        await ActivationCallAsync(sdk, "confirm_context", new { connectionSessionId = id, kind = "initial", initialDeliveryId = joined.GetProperty("initialDeliveryId").GetGuid() });
        async Task<DateTimeOffset?> Lease()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync()).LeaseExpiresAtUtc;
        }
        var initialLease = await Lease();
        foreach (var seconds in new[] { -1, 0, 301 })
        {
            Assert.Equal("agent_request_invalid", (await ActivationCallAsync(sdk, "wait_for_mention",
                new { connectionSessionId = id, waitTimeoutSeconds = seconds })).GetProperty("errorCode").GetString());
            Assert.Equal(initialLease, await Lease());
        }
        using var cancelWait = new CancellationTokenSource();
        var requestId = new RequestId("long-reliable-wait");
        var start = time.Now;
        var wait = sdk.SendRequestAsync<CallToolRequestParams, CallToolResult>("tools/call", new()
        {
            Name = "wait_for_mention", Arguments = new Dictionary<string, JsonElement>
            { ["connectionSessionId"] = JsonSerializer.SerializeToElement(id), ["waitTimeoutSeconds"] = JsonSerializer.SerializeToElement(300) }
        }, requestId: requestId, cancellationToken: cancelWait.Token).AsTask();
        Task<JsonElement>? older = null;
        try
        {
            await signals.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await EventuallyAsync(factory, async db => await db.AgentConnectionSessions.AnyAsync(c => c.LeaseExpiresAtUtc == start.AddSeconds(330)));
            Assert.Equal(start.AddSeconds(330), await Lease());
            time.Now = start.AddSeconds(220);
            delayed.Armed = true;
            older = ActivationCallAsync(sdk, "renew_agent_lease", new { connectionSessionId = id });
            await delayed.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(older.IsCompleted);
            time.Now = start.AddSeconds(250);
            var newer = await ActivationCallAsync(sdk, "renew_agent_lease", new { connectionSessionId = id });
            var newerExpiry = newer.GetProperty("data").GetProperty("leaseExpiresAt").GetDateTimeOffset();
            Assert.Equal(start.AddSeconds(370), newerExpiry);
            Assert.Equal(newerExpiry, await Lease());
            delayed.Release.TrySetResult();
            var olderExpiry = (await older).GetProperty("data").GetProperty("leaseExpiresAt").GetDateTimeOffset();
            Assert.Equal(start.AddSeconds(340), olderExpiry);
            Assert.True(olderExpiry < newerExpiry);
            Assert.Equal(newerExpiry, await Lease());
            Assert.False(wait.IsCompleted);
        }
        finally { delayed.Release.TrySetResult(); cancelWait.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await sdk.SendNotificationAsync("notifications/cancelled", new CancelledNotificationParams { RequestId = requestId }, cancellationToken: TestContext.Current.CancellationToken);
        await signals.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True((await ActivationCallAsync(sdk, "get_agent_status", new { connectionSessionId = id })).GetProperty("succeeded").GetBoolean());
    }

    private sealed class DelayedRenewalResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var hold = false;
            if (Armed && request.Method == HttpMethod.Post && request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                hold = body.RootElement.TryGetProperty("params", out var parameters)
                    && parameters.TryGetProperty("name", out var name) && name.GetString() == "renew_agent_lease";
                if (hold) Armed = false;
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (hold)
            {
                var originalContent = response.Content;
                var bytes = await originalContent.ReadAsByteArrayAsync(cancellationToken);
                response.Content = new ByteArrayContent(bytes);
                foreach (var header in originalContent.Headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                originalContent.Dispose();
                Captured.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return response;
        }
    }
}
