using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task SameTransportConcurrentCrossMeetingJoinDoesNotCreateLosingIdentity()
    {
        using var original = new TestApplicationFactory();
        var coordinator = new PausedJoinCoordinator();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentJoinCoordinator>();
            services.AddSingleton<IAgentJoinCoordinator>(coordinator);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var firstMeeting = await CreateMeetingAsync(browser, "First reservation target");
        var secondMeeting = await CreateMeetingAsync(browser, "Second reservation target");
        var key = McpTransportSessionKey.FromDigest(Enumerable.Repeat((byte)17, 32).ToArray());
        await using var firstScope = factory.Services.CreateAsyncScope();
        await using var secondScope = factory.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IAgentCollaborationService>()
            .JoinAsync(new AgentJoinRequest(firstMeeting.MeetingCode, "Worker1", 12000), key, TestContext.Current.CancellationToken);
        Task<AgentOperationResult>? second = null;
        try
        {
            await coordinator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            // Direct invocation runs up to its first incomplete await, making this contention deterministic.
            second = secondScope.ServiceProvider.GetRequiredService<IAgentCollaborationService>()
                .JoinAsync(new AgentJoinRequest(secondMeeting.MeetingCode, "Worker2", 12000), key, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);
            Assert.Equal(1, coordinator.Acquisitions);
        }
        finally { coordinator.Release.TrySetResult(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal("connection_invalid", (await second!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ErrorCode);
        await using var readScope = factory.Services.CreateAsyncScope();
        var db = readScope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(firstMeeting.MeetingId, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).MeetingId);
        Assert.False(await db.MeetingParticipants.AnyAsync(item => item.MeetingId == secondMeeting.MeetingId && item.ParticipantType == ParticipantType.Agent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, coordinator.Acquisitions);
    }

    [Fact]
    public async Task TransportClosureAfterDatabaseWriteBeforeRegistrationCompensatesTheCommittedConnection()
    {
        using var original = new TestApplicationFactory();
        var registry = new CloseBeforeRegistrationRegistry();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentConnectionRegistry>();
            services.AddSingleton<IAgentConnectionRegistry>(registry);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Close before registration");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        AssertToolFailure(SdkPayload(await sdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000
        }, cancellationToken: TestContext.Current.CancellationToken)), "agent_operation_unavailable");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(AgentConnectionState.Disconnected, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Empty(registry.ListRegistrations(meeting.MeetingId));
    }

    [Fact]
    public async Task ClosureAfterRegistrationBeforeActivationCannotReturnAUsableJoin()
    {
        using var original = new TestApplicationFactory();
        var registry = new CloseBeforeRegistrationRegistry { CloseAfterRegistration = true };
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAgentConnectionRegistry>();
            services.AddSingleton<IAgentConnectionRegistry>(registry);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Close before activation");
        using var http = factory.CreateClient(new() { HandleCookies = false });
        await using var sdk = await CreateSdkAsync(http);
        AssertToolFailure(SdkPayload(await sdk.CallToolAsync("join_meeting", new Dictionary<string, object?>
        {
            ["meetingCode"] = meeting.MeetingCode, ["displayName"] = "Worker", ["contextCharacterLimit"] = 12000
        }, cancellationToken: TestContext.Current.CancellationToken)), "agent_operation_unavailable");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var connection = await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(AgentConnectionState.Disconnected, connection.State);
        Assert.Equal(connection.ID, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).CurrentConnectionId);
        Assert.Equal(InitialContextState.Prepared, (await db.AgentInitialContextDeliveries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Empty(registry.ListRegistrations(meeting.MeetingId));
    }

    private sealed class PausedJoinCoordinator : IAgentJoinCoordinator
    {
        private readonly AgentJoinCoordinator inner = new();
        private int acquisitions;
        public int Acquisitions => Volatile.Read(ref acquisitions);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IAsyncDisposable> AcquireAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref acquisitions);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.AcquireAsync(meetingId, normalizedDisplayName, cancellationToken);
        }
    }

    private sealed class CloseBeforeRegistrationRegistry : IAgentConnectionRegistry
    {
        public bool CloseAfterRegistration { get; init; }
        private readonly AgentConnectionRegistry inner = new();
        public ValueTask<IAsyncDisposable> ReserveTransportAsync(McpTransportSessionKey session, CancellationToken cancellationToken = default) => inner.ReserveTransportAsync(session, cancellationToken);
        public bool TryActivate(McpTransportSessionKey session, AgentConnectionRegistration registration, AgentConnectionRegistration? previous) => inner.TryActivate(session, registration, previous);
        public bool Retire(AgentConnectionRegistration registration) => inner.Retire(registration);
        public CancellationToken GetBindingCancellationToken(McpTransportSessionKey session, AgentConnectionRegistration registration) => inner.GetBindingCancellationToken(session, registration);
        public void TerminateMeeting(Guid meetingId) => inner.TerminateMeeting(meetingId);
        public bool ContainsTransport(McpTransportSessionKey session) => inner.ContainsTransport(session);
        public bool IsRegistered(Guid meetingId, Guid participantId, Guid connectionId, string binding) => inner.IsRegistered(meetingId, participantId, connectionId, binding);
        public IReadOnlyList<AgentConnectionRegistration> ListRegistrations(Guid meetingId) => inner.ListRegistrations(meetingId);
        public IReadOnlyList<AgentConnectionRegistration> RemoveRegistrations(McpTransportSessionKey session) => inner.RemoveRegistrations(session);
        public bool TryGetRegistration(McpTransportSessionKey session, out AgentConnectionRegistration registration) => inner.TryGetRegistration(session, out registration);
        public bool Unregister(McpTransportSessionKey session, Guid connectionId) => inner.Unregister(session, connectionId);
        public bool TryRegister(McpTransportSessionKey session, AgentConnectionRegistration registration)
        {
            if (CloseAfterRegistration)
            {
                var registered = inner.TryRegister(session, registration);
                inner.RemoveRegistrations(session);
                return registered;
            }
            // Inject the same registry callback used by RunSessionHandler, precisely before registration.
            inner.RemoveRegistrations(session);
            return inner.TryRegister(session, registration);
        }
    }
}
