using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfirmationAndTimeoutScanHaveOneOutcomeAtTheExactDeadline(bool expired, bool scanFirst)
    {
        using var test = await ReliableRaceFixture.CreateAsync(confirmTask: false);
        var registration = test.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(test.MeetingId).Single();
        test.Clock.Now = test.Mention.ConfirmationDeadline!.Value.AddTicks(expired ? 0 : -1);
        Task<AgentOperationResult> Confirm() => test.Run(s => s.ConfirmReliableAsync(new(test.ConnectionId, "task",
            TaskId: test.Mention.TaskId, DeliveryAttemptId: test.Mention.DeliveryAttemptId), test.Transport));
        Task<AgentOperationResult> Scan() => test.Run(async s =>
            AgentOperationResult.Success(await s.ReconcileConnectionAsync(registration), "Scan complete."));
        var (first, second) = await test.OrderAsync(scanFirst ? Scan : Confirm, scanFirst ? Confirm : Scan);
        var confirmation = scanFirst ? second : first;
        var scanned = Assert.IsType<bool>((scanFirst ? first : second).Data);
        Assert.Equal(!expired, confirmation.Succeeded);
        Assert.Equal(expired, scanned);
        if (expired) Assert.Equal(scanFirst ? "delivery_attempt_stale" : "confirmation_expired", confirmation.ErrorCode);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var attempt = await db.AgentTaskDeliveryAttempts.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expired ? AgentReplyTaskState.Failed : AgentReplyTaskState.Processing, task.State);
        Assert.Equal(expired, attempt.InvalidatedAtUtc.HasValue);
        Assert.Equal(!expired, attempt.ConfirmedAtUtc.HasValue);
        Assert.Equal(expired ? 0 : task.TriggerSequence, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeliveredThroughSequence);
        Assert.Equal(0, task.AutomaticRetryCount);
        Assert.Equal(AgentConnectionState.Connected, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.False(Assert.IsType<bool>((await Scan()).Data));
    }

    [Fact]
    public async Task CancellingAQueuedReliableRequestReleasesItsLockWithoutDisconnectingTheBinding()
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        var round = test.Coordinator.Arm();
        var first = test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport));
        using var cancellation = new CancellationTokenSource();
        try
        {
            await round.Held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var second = test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport, cancellation.Token));
            await round.Contended.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally { round.Release.TrySetResult(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Succeeded);
        Assert.True((await test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport)).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Succeeded);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(AgentConnectionState.Connected, (await db.AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
        Assert.Equal(AgentReplyTaskState.Processing, (await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReliableReplyAndTakeoverHaveOneDurableWinner(bool takeoverFirst)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        Task<AgentOperationResult> Reply() => test.Run(s => s.ReplyReliableAsync(test.Reply, test.Transport));
        Task<AgentOperationResult> Takeover() => JoinReliableForTestAsync(test.Services,
            ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), RecoveryTransport(212));
        var (first, second) = await test.OrderAsync(takeoverFirst ? Takeover : Reply, takeoverFirst ? Reply : Takeover);
        Assert.True(first.Succeeded);
        Assert.Equal(!takeoverFirst, second.Succeeded);
        if (takeoverFirst) Assert.Equal("connection_superseded", second.ErrorCode);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(takeoverFirst ? AgentReplyTaskState.Failed : AgentReplyTaskState.Replied, task.State);
        Assert.Equal(takeoverFirst ? 1 : 2, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(c => c.State == AgentConnectionState.Connected, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(c => c.State == AgentConnectionState.Superseded, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, (await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ConnectionGeneration);
        Assert.Equal(test.Mention.DeliveryAttemptId, task.CurrentAttemptId);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>("SELECT count(*) AS Value FROM pragma_foreign_key_check").SingleAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReliableRepliesReplayOrRejectDifferentContentWithoutDuplicatingOutbox(bool different)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        var (first, second) = await test.OrderAsync(
            () => test.Run(s => s.ReplyReliableAsync(test.Reply, test.Transport)),
            () => test.Run(s => s.ReplyReliableAsync(different ? test.Reply with { Content = "Different reply" } : test.Reply, test.Transport)));
        var accepted = Assert.IsType<AgentReplyResultDto>(first.Data);
        Assert.False(accepted.Replayed);
        if (different) Assert.Equal("reply_idempotency_mismatch", second.ErrorCode);
        else
        {
            var replay = Assert.IsType<AgentReplyResultDto>(second.Data);
            Assert.True(replay.Replayed);
            Assert.Equal(accepted.Message.MessageId, replay.Message.MessageId);
        }
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        Assert.Equal(2, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.MessageMentions.CountAsync(m => m.MessageId == accepted.Message.MessageId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(accepted.Message.MessageId, (await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).ReplyMessageId);
        var appended = await db.MessageAppendedEvents.SingleAsync(e => e.MessageId == accepted.Message.MessageId, cancellationToken: TestContext.Current.CancellationToken);
        var deliveries = await db.MessageEventDeliveries.Where(d => d.EventId == appended.ID).ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(deliveries);
        Assert.Equal(deliveries.Length, deliveries.Select(d => d.ConsumerName).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReliableFailureAndReplyCannotBothCommit(bool failureFirst)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        Task<AgentOperationResult> Reply() => test.Run(s => s.ReplyReliableAsync(test.Reply, test.Transport));
        Task<AgentOperationResult> Fail() => test.Run(s => s.ReportFailureReliableAsync(test.Failure, test.Transport));
        var (first, second) = await test.OrderAsync(failureFirst ? Fail : Reply, failureFirst ? Reply : Fail);
        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Equal(failureFirst ? "delivery_attempt_stale" : "task_not_available", second.ErrorCode);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(failureFirst ? AgentReplyTaskState.Failed : AgentReplyTaskState.Replied, task.State);
        Assert.Equal(failureFirst ? 1 : 2, await db.MeetingMessages.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(failureFirst, task.RetryExhausted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReliableRetriesCreateOnlyOneNewCycle(bool sameOperation)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        Assert.True((await test.Run(s => s.ReportFailureReliableAsync(test.Failure, test.Transport))).Succeeded);
        var request = new AgentRetryRequest(test.ConnectionId, test.Mention.TaskId, Guid.NewGuid());
        var (first, second) = await test.OrderAsync(
            () => test.Run(s => s.RetryReliableAsync(request, test.Transport)),
            () => test.Run(s => s.RetryReliableAsync(sameOperation ? request : request with { OperationId = Guid.NewGuid() }, test.Transport)));
        Assert.Equal(1, Assert.IsType<AgentRetryResultDto>(first.Data).RetryCycle);
        if (sameOperation) Assert.True(Assert.IsType<AgentRetryResultDto>(second.Data).Replayed);
        else Assert.Equal("retry_not_required", second.ErrorCode);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var task = await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, task.RetryCycle);
        Assert.Equal(0, task.AutomaticRetryCount);
        Assert.False(task.RetryExhausted);
        Assert.Equal(1, await db.AgentRecoveryOperations.CountAsync(o => o.OperationKind == AgentRecoveryOperationKind.Retry, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentTaskDeliveryAttempts.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentTakeoversSerializeGenerationsAndLeaveOnlyTheLastBindingUsable()
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        var firstTransport = RecoveryTransport(213);
        var secondTransport = RecoveryTransport(214);
        var (first, second) = await test.OrderAsync(
            () => JoinReliableForTestAsync(test.Services, ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), firstTransport),
            () => JoinReliableForTestAsync(test.Services, ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), secondTransport));
        var firstId = Assert.IsType<AgentJoinResultDto>(first.Data).Connection.ConnectionSessionId;
        var secondId = Assert.IsType<AgentJoinResultDto>(second.Data).Connection.ConnectionSessionId;
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("connection_superseded", (await test.Run(s => s.RenewReliableAsync(new(firstId), firstTransport))).ErrorCode);
        Assert.True((await test.Run(s => s.RenewReliableAsync(new(secondId), secondTransport))).Succeeded);
        await using var scope = test.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
        var cursor = await db.AgentContextCursors.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, cursor.ConnectionGeneration);
        Assert.Equal(secondId, cursor.CurrentConnectionId);
        Assert.Equal(1, await db.MeetingParticipants.CountAsync(p => p.ParticipantType == ParticipantType.Agent, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(c => c.State == AgentConnectionState.Connected, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, await db.AgentConnectionSessions.CountAsync(c => c.State == AgentConnectionState.Superseded, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, (await db.AgentReplyTasks.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AutomaticRetryCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReliableRenewalUsesTheTimeAfterAcquiringTheParticipantLock(bool expireWhileQueued)
    {
        using var test = await ReliableRaceFixture.CreateAsync();
        var renewed = Assert.IsType<AgentLeaseResultDto>((await test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport))).Data);
        test.Clock.Now = renewed.LeaseExpiresAt.AddTicks(-1);
        var round = test.Coordinator.Arm();
        var request = test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport));
        try
        {
            await round.Held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (expireWhileQueued) test.Clock.Now = renewed.LeaseExpiresAt;
        }
        finally { round.Release.TrySetResult(); }
        var result = await request.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(!expireWhileQueued, result.Succeeded);
        if (expireWhileQueued) Assert.Equal("connection_expired", result.ErrorCode);
        await using var scope = test.Services.CreateAsyncScope();
        var connection = await scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>().AgentConnectionSessions.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expireWhileQueued ? renewed.LeaseExpiresAt : test.Clock.Now.AddSeconds(120), connection.LeaseExpiresAtUtc);
    }

    private sealed class ReliableRaceFixture : IDisposable
    {
        private readonly TestApplicationFactory original = new();
        private WebApplicationFactory<Program> factory = null!;
        public readonly OrderedAgentCoordinator Coordinator = new();
        public readonly RecoveryClock Clock = new(DateTimeOffset.UtcNow);
        public IServiceProvider Services => factory.Services;
        public WebApplicationFactory<Program> Factory => factory;
        public McpTransportSessionKey Transport { get; } = RecoveryTransport(211);
        public Guid ConnectionId { get; private set; }
        public Guid MeetingId { get; private set; }
        public string MeetingCode { get; private set; } = "";
        public string RecoveryKey { get; private set; } = "";
        public AgentMentionResultDto Mention { get; private set; } = null!;
        public AgentReplyRequest Reply => new(ConnectionId, Mention.TaskId, Mention.DeliveryAttemptId, Mention.ReplyIdempotencyKey, "@Alice Race reply");
        public AgentFailureRequest Failure => new(ConnectionId, Mention.TaskId, Mention.DeliveryAttemptId, "agent_permanent_failure");
        public Task<AgentOperationResult> Run(Func<AgentCollaborationService, Task<AgentOperationResult>> action) => ReliableForTestAsync(Services, action);
        public static async Task<ReliableRaceFixture> CreateAsync(bool confirmTask = true, IMeetingRealtimeGate? meetingGate = null)
        {
            var test = new ReliableRaceFixture();
            try
            {
                test.factory = test.original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IAgentJoinCoordinator>();
                    services.AddSingleton<IAgentJoinCoordinator>(test.Coordinator);
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(test.Clock);
                    if (meetingGate is not null)
                    {
                        services.RemoveAll<IMeetingRealtimeGate>();
                        services.AddSingleton(meetingGate);
                    }
                }));
                using var browser = test.factory.CreateClient(new() { HandleCookies = true });
                await LoginAsync(browser, "alice", "test-password-alice");
                var meeting = await CreateMeetingAsync(browser, "Reliable ordered races");
                test.MeetingId = meeting.MeetingId;
                test.MeetingCode = meeting.MeetingCode;
                var join = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
                test.RecoveryKey = join.RecoveryKey!;
                var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(test.Services, join, test.Transport)).Data);
                test.ConnectionId = joined.Connection.ConnectionSessionId;
                Assert.True((await test.Run(s => s.ConfirmReliableAsync(new(test.ConnectionId, "initial", joined.InitialDeliveryId), test.Transport))).Succeeded);
                await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker Ordered race question");
                test.Mention = Assert.IsType<AgentMentionResultDto>((await test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport))).Data);
                if (confirmTask)
                    Assert.True((await test.Run(s => s.ConfirmReliableAsync(new(test.ConnectionId, "task",
                        TaskId: test.Mention.TaskId, DeliveryAttemptId: test.Mention.DeliveryAttemptId), test.Transport))).Succeeded);
                return test;
            }
            catch { test.Dispose(); throw; }
        }
        public async Task<(AgentOperationResult First, AgentOperationResult Second)> OrderAsync(
            Func<Task<AgentOperationResult>> firstCall, Func<Task<AgentOperationResult>> secondCall)
        {
            var round = Coordinator.Arm();
            var first = firstCall();
            Task<AgentOperationResult>? second = null;
            try
            {
                await round.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
                second = secondCall();
                await round.Contended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(second.IsCompleted);
            }
            finally { round.Release.TrySetResult(); }
            var results = await Task.WhenAll(first, second!).WaitAsync(TimeSpan.FromSeconds(10));
            return (results[0], results[1]);
        }
        public void Dispose() { Coordinator.Unblock(); factory?.Dispose(); original.Dispose(); }
    }

    private sealed class OrderedAgentCoordinator : IAgentJoinCoordinator
    {
        private readonly AgentJoinCoordinator inner = new();
        private Round? round;
        public Round Arm() { round = new(); return round; }
        public void Unblock() => round?.Release.TrySetResult();
        public async ValueTask<IAsyncDisposable> AcquireAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default)
        {
            var current = normalizedDisplayName == "WORKER" ? round : null;
            var order = current is null ? 0 : Interlocked.Increment(ref current.Acquisitions);
            if (order == 2) current!.Contended.TrySetResult();
            var lease = await inner.AcquireAsync(meetingId, normalizedDisplayName, cancellationToken);
            if (order != 1) return lease;
            try
            {
                current!.Held.TrySetResult();
                await current.Release.Task.WaitAsync(cancellationToken);
                return lease;
            }
            catch { await lease.DisposeAsync(); throw; }
        }
        public sealed class Round
        {
            public int Acquisitions;
            public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Contended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
