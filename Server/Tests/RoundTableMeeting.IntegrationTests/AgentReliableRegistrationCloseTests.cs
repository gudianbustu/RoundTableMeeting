using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSdkCloseAtReliableRegistrationCutNeverReturnsUsableOwnership(bool afterRegistration)
    {
        using var original = new TestApplicationFactory();
        var registry = new PausedReliableRegistration(afterRegistration);
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        { services.RemoveAll<IAgentConnectionRegistry>(); services.AddSingleton<IAgentConnectionRegistry>(registry); }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Reliable registration close");
        using var http = factory.CreateClient();
        var sdk = await CreateSdkAsync(http);
        var request = new { meetingCode = meeting.MeetingCode, displayName = "Worker", contextCharacterLimit = 12000,
            reliabilityMode = "explicit_ack_v1", operationId = Guid.NewGuid(), recoveryKey = new string('A', 43) };
        var pending = ActivationCallAsync(sdk, "join_meeting", request);
        Task? closing = null;
        try
        {
            await registry.Held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await EventuallyAsync(factory, db => db.AgentConnectionSessions.AnyAsync(c => c.State == AgentConnectionState.Activating));
            closing = sdk.DisposeAsync().AsTask();
            await registry.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { registry.Release.Set(); }
        if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var failure = await Record.ExceptionAsync(async () =>
        {
            var result = await pending;
            Assert.False(result.GetProperty("succeeded").GetBoolean());
        });
        if (failure is Xunit.Sdk.XunitException) throw failure;
        await EventuallyAsync(factory, db => db.AgentConnectionSessions.AnyAsync(c => c.State == AgentConnectionState.Disconnected));
        Assert.Empty(registry.ListRegistrations(meeting.MeetingId));
        await using var recovery = await CreateSdkAsync(http);
        var receipt = (await ProcessCallAsync(recovery, "join_meeting", request)).GetProperty("data");
        Assert.Equal("recovery_required", receipt.GetProperty("kind").GetString());
        var resumed = (await ProcessCallAsync(recovery, "resume_agent", new
        { connectionSessionId = receipt.GetProperty("connectionSessionId").GetGuid(), recoveryKey = request.recoveryKey,
            operationId = Guid.NewGuid() })).GetProperty("data");
        Assert.Equal("resumed", resumed.GetProperty("kind").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeeting.Repository.Sqlite.Database.RoundTableMeetingDbContext>();
        Assert.Equal(2, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).BindingVersion);
        Assert.Equal(2, await db.AgentRecoveryOperations.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        await sdk.DisposeAsync();
    }

    private sealed class PausedReliableRegistration(bool afterRegistration) : IAgentConnectionRegistry
    {
        private readonly AgentConnectionRegistry inner = new();
        private int armed = 1;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool TryActivate(McpTransportSessionKey session, AgentConnectionRegistration registration, AgentConnectionRegistration? previous)
        {
            if (Interlocked.Exchange(ref armed, 0) == 0) return inner.TryActivate(session, registration, previous);
            var result = afterRegistration && inner.TryActivate(session, registration, previous);
            Held.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Registration cut was not released.");
            return afterRegistration ? result : inner.TryActivate(session, registration, previous);
        }
        public IReadOnlyList<AgentConnectionRegistration> RemoveRegistrations(McpTransportSessionKey session)
        { var removed = inner.RemoveRegistrations(session); Closed.TrySetResult(); return removed; }
        public ValueTask<IAsyncDisposable> ReserveTransportAsync(McpTransportSessionKey session, CancellationToken cancellationToken = default) => inner.ReserveTransportAsync(session, cancellationToken);
        public bool Retire(AgentConnectionRegistration registration) => inner.Retire(registration);
        public CancellationToken GetBindingCancellationToken(McpTransportSessionKey session, AgentConnectionRegistration registration) => inner.GetBindingCancellationToken(session, registration);
        public void TerminateMeeting(Guid meetingId) => inner.TerminateMeeting(meetingId);
        public bool ContainsTransport(McpTransportSessionKey session) => inner.ContainsTransport(session);
        public bool IsRegistered(Guid meetingId, Guid participantId, Guid connectionId, string binding) => inner.IsRegistered(meetingId, participantId, connectionId, binding);
        public IReadOnlyList<AgentConnectionRegistration> ListRegistrations(Guid meetingId) => inner.ListRegistrations(meetingId);
        public bool TryGetRegistration(McpTransportSessionKey session, out AgentConnectionRegistration registration) => inner.TryGetRegistration(session, out registration);
        public bool Unregister(McpTransportSessionKey session, Guid connectionId) => inner.Unregister(session, connectionId);
        public bool TryRegister(McpTransportSessionKey session, AgentConnectionRegistration registration) => inner.TryRegister(session, registration);
    }
}
