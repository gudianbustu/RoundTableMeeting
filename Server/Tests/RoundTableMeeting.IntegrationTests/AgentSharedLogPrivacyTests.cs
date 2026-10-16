using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.RealtimeChat;
using RoundTableMeeting.RealtimeChat;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    private const string SharedLogSecret = "private-agent-context-from-dependency";

    [Fact]
    public async Task SubscriptionDependencyFailureDoesNotLogPrivateException()
    {
        var logger = new SharedPrivacyLogger<MeetingChatHub>();
        using var hub = new MeetingChatHub(null!, null!, new SharedPrivacyGate(), TimeProvider.System, logger);
        hub.Context = new SubscriptionCaller(Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => hub.SubscribeMeeting(Guid.NewGuid()));
        AssertPrivateLog(await logger.Written.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task DispatchScanFailureDoesNotLogPrivateException()
    {
        var logger = new SharedPrivacyLogger<MessageEventDispatcherService>();
        using var dispatcher = new MessageEventDispatcherService(new SharedPrivacyScopes(), TimeProvider.System, new(), logger);
        await dispatcher.StartAsync(CancellationToken.None);
        try { AssertPrivateLog(await logger.Written.Task.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { await dispatcher.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DispatchConsumerFailurePersistsRetryWithoutLoggingPrivateException()
    {
        using var factory = new TestApplicationFactory();
        var logger = new SharedPrivacyLogger<MessageEventDispatcherService>();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILogger<MessageEventDispatcherService>>(logger);
            services.AddSingleton<IMessageEventConsumerCatalog>(new MessageEventConsumerCatalog([new("privacy-v1", typeof(SharedPrivacyConsumer))]));
            services.AddSingleton<SharedPrivacyConsumer>();
        }));
        using var browser = configured.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Private consumer failure");
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker " + SharedLogSecret);
        AssertPrivateLog(await logger.Written.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await using var scope = configured.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var delivery = await db.MessageEventDeliveries.SingleAsync();
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Null(delivery.LeaseId);
        Assert.Equal(RoundTableMeeting.Domain.Enums.MessageEventDeliveryState.Pending, delivery.State);
    }

    private static void AssertPrivateLog((string Message, Exception? Exception) entry)
    {
        Assert.Contains("InvalidOperationException", entry.Message);
        Assert.DoesNotContain(SharedLogSecret, entry.Message);
        Assert.Null(entry.Exception);
    }

    private sealed class SharedPrivacyConsumer : IMessageEventConsumer
    { public Task ConsumeAsync(MessageAppendedEnvelope envelope, CancellationToken cancellationToken = default) => throw new InvalidOperationException(SharedLogSecret); }
    private sealed class SharedPrivacyScopes : IServiceScopeFactory
    { public IServiceScope CreateScope() => throw new InvalidOperationException(SharedLogSecret); }
    private sealed class SharedPrivacyGate : IMeetingRealtimeGate
    {
        public ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default) => throw new InvalidOperationException(SharedLogSecret);
        public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveMutationAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void Forget(Guid meetingId) { }
    }
    private sealed class SharedPrivacyLogger<T> : ILogger<T>
    {
        public TaskCompletionSource<(string Message, Exception? Exception)> Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Error) Written.TrySetResult((formatter(state, exception), exception)); }
    }
}
