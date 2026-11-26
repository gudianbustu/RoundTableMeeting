using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;

namespace RoundTableMeeting.IntegrationTests;

public sealed class AgentInfrastructureLogPrivacyTests
{
    [Fact]
    public async Task DisconnectCompensationDoesNotLogRawDependencyExceptions()
    {
        var logger = new PrivateExceptionLogger<AgentConnectionCompensator>();
        var service = new AgentConnectionCompensator(new PrivateFaultScopes(), logger);
        Assert.False(await service.DisconnectAsync(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "safe-test-binding"), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.Single(logger.Messages);
        Assert.Contains("InvalidOperationException", logger.Messages[0]);
        Assert.DoesNotContain(Secret, logger.Messages[0]);
        Assert.Null(Assert.Single(logger.Exceptions));
    }

    [Fact]
    public async Task ActivityPublicationDoesNotLogRawDependencyExceptions()
    {
        var logger = new PrivateExceptionLogger<AgentActivityNotifier>();
        var service = new AgentActivityNotifier(null!, new PrivateFaultScopes(), new PrivateFaultGate(), logger);
        await service.NotifyAsync(new AgentActivityDto(Guid.NewGuid(), Guid.NewGuid(), "waiting", null, null, DateTimeOffset.UtcNow), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(logger.Messages);
        Assert.Contains("InvalidOperationException", logger.Messages[0]);
        Assert.DoesNotContain(Secret, logger.Messages[0]);
        Assert.Null(Assert.Single(logger.Exceptions));
    }

    private const string Secret = "private-recovery-key-and-message-from-dependency";
    private sealed class PrivateFaultScopes : IServiceScopeFactory
    { public IServiceScope CreateScope() => throw new InvalidOperationException(Secret); }
    private sealed class PrivateFaultGate : IMeetingRealtimeGate
    {
        public ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Secret);
        public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveMutationAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void Forget(Guid meetingId) { }
    }
    private sealed class PrivateExceptionLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, exception)); Exceptions.Add(exception); }
    }
}
