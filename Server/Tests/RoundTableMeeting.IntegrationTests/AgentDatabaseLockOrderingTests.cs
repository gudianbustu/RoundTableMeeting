using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;
using RoundTableMeeting.Repository.Sqlite.Transactions;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("join", false)]
    [InlineData("resume", false)]
    [InlineData("reply", false)]
    [InlineData("scan", false)]
    [InlineData("resume", true)]
    [InlineData("reply", true)]
    public async Task ActualSqliteWriterLockCannotReverseDeletionOrderingAndCancelledOperationsReleaseTheirLocks(string operation, bool cancel)
    {
        using var original = new TestApplicationFactory();
        var gate = new ObservedClosingGate();
        var transactionCut = new DatabaseLockCut();
        using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMeetingRealtimeGate>(); services.AddSingleton<IMeetingRealtimeGate>(gate);
            services.RemoveAll<IMeetingTransactionRunner>();
            services.AddScoped<IMeetingTransactionRunner>(sp => new ObservedDatabaseTransaction(
                ActivatorUtilities.CreateInstance<MeetingTransactionRunner>(sp), transactionCut));
            services.PostConfigure<AgentCollaborationOptions>(o => o.RecoveryScanIntervalSeconds = 3600);
        }));
        using var browser = factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var meeting = await CreateMeetingAsync(browser, "Database lock ordering");
        var other = await CreateMeetingAsync(browser, "Independent after writer release");
        var transport = RecoveryTransport(230);
        var join = ReliableJoinRequest(meeting.MeetingCode, "Worker", "create");
        var joined = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(factory.Services, join, transport)).Data);
        var id = joined.Connection.ConnectionSessionId;
        await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "initial", joined.InitialDeliveryId), transport));
        await PostHumanMessageAsync(browser, meeting.MeetingId, "@Worker locked database reply");
        var mention = Assert.IsType<AgentMentionResultDto>((await ReliableForTestAsync(factory.Services,
            s => s.WaitReliableAsync(new(id), transport))).Data);
        await ReliableForTestAsync(factory.Services, s => s.ConfirmReliableAsync(new(id, "task", TaskId: mention.TaskId, DeliveryAttemptId: mention.DeliveryAttemptId), transport));
        var registration = Assert.Single(factory.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(meeting.MeetingId));
        await using var lockConnection = new SqliteConnection($"Data Source={original.DatabasePath};Pooling=False");
        await lockConnection.OpenAsync();
        using var writer = lockConnection.BeginTransaction(deferred: false);
        using var cancellation = new CancellationTokenSource();
        var admitted = Task.Run(async () =>
        {
            transactionCut.Observe.Value = true;
            try
            {
                return await ReliableForTestAsync(factory.Services, async s => operation switch
                {
                    "join" => await s.JoinReliableAsync(ReliableJoinRequest(meeting.MeetingCode, "Worker", "takeover"), RecoveryTransport(231), cancellation.Token),
                    "resume" => await s.ResumeReliableAsync(new(id, join.RecoveryKey, Guid.NewGuid()), RecoveryTransport(231), cancellationToken: cancellation.Token),
                    "reply" => await s.ReplyReliableAsync(new(id, mention.TaskId, mention.DeliveryAttemptId, mention.ReplyIdempotencyKey, "Locked reply"), transport, cancellation.Token),
                    _ => AgentOperationResult.Success(await s.ReconcileConnectionAsync(registration, cancellationToken: cancellation.Token), "Scan completed"),
                });
            }
            finally { transactionCut.Observe.Value = false; }
        });
        Task? deletion = null;
        try
        {
            await transactionCut.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(admitted.IsCompleted);
            deletion = DeleteLifecycleMeetingAsync(browser, meeting.MeetingId, "Database lock ordering");
            await gate.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(deletion.IsCompleted);
            if (cancel) cancellation.Cancel();
        }
        finally { writer.Rollback(); }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admitted.WaitAsync(TimeSpan.FromSeconds(10)));
        else Assert.True((await admitted.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
        await deletion!.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertAgentMeetingGoneAsync(factory, meeting.MeetingId);
        await using (var released = await factory.Services.GetRequiredService<IAgentConnectionRegistry>()
            .ReserveTransportAsync(RecoveryTransport(231)).AsTask().WaitAsync(TimeSpan.FromSeconds(5))) { }
        await PostHumanMessageAsync(browser, other.MeetingId, "Human work continues after the global SQLite writer releases");
        Assert.True((await JoinReliableForTestAsync(factory.Services,
            ReliableJoinRequest(other.MeetingCode, "Worker", "create"), RecoveryTransport(232))).Succeeded);
    }

    private sealed class DatabaseLockCut
    {
        public AsyncLocal<bool> Observe { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ObservedDatabaseTransaction(MeetingTransactionRunner core, DatabaseLockCut cut) : IMeetingTransactionRunner
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
        {
            if (cut.Observe.Value) cut.Entered.TrySetResult();
            return core.RunAsync(action, cancellationToken);
        }
    }
}
