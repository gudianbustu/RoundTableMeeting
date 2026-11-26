using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.RealtimeChat;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Fact]
    public async Task DeletionBarrierRejectsEveryReliableOperationWhileAnotherMeetingContinues()
    {
        var gate = new PausedDeletionGate();
        using var test = await ReliableRaceFixture.CreateAsync(meetingGate: gate);
        using var browser = test.Factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var other = await CreateMeetingAsync(browser, "Independent during deletion");
        var registration = test.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(test.MeetingId).Single();
        var deletion = DeleteLifecycleMeetingAsync(browser, test.MeetingId, "Reliable ordered races");
        try
        {
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(deletion.IsCompleted);
            var blockedCalls = new Func<Task<AgentOperationResult>>[]
            {
                () => JoinReliableForTestAsync(test.Services, ReliableJoinRequest(test.MeetingCode, "Other", "create"), RecoveryTransport(221)),
                () => JoinReliableForTestAsync(test.Services, ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), RecoveryTransport(222)),
                () => test.Run(s => s.ResumeReliableAsync(new(test.ConnectionId, test.RecoveryKey, Guid.NewGuid()), RecoveryTransport(223))),
                () => test.Run(s => s.ConfirmReliableAsync(new(test.ConnectionId, "task", TaskId: test.Mention.TaskId,
                    DeliveryAttemptId: test.Mention.DeliveryAttemptId), test.Transport)),
                () => test.Run(s => s.ReplyReliableAsync(test.Reply, test.Transport)),
                () => test.Run(s => s.RenewReliableAsync(new(test.ConnectionId), test.Transport)),
                () => test.Run(s => s.WaitReliableAsync(new(test.ConnectionId), test.Transport)),
                () => test.Run(s => s.StatusReliableAsync(new(test.ConnectionId), test.Transport)),
                () => test.Run(s => s.ReportFailureReliableAsync(test.Failure, test.Transport)),
                () => test.Run(s => s.RetryReliableAsync(new(test.ConnectionId, test.Mention.TaskId, Guid.NewGuid()), test.Transport)),
            };
            var denied = await Task.WhenAll(blockedCalls.Select(call => call())).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.All(denied, result => Assert.Equal("meeting_not_found", result.ErrorCode));
            var scan = await test.Run(async s => AgentOperationResult.Success(await s.ReconcileConnectionAsync(registration), "Scan complete."));
            Assert.False(Assert.IsType<bool>(scan.Data));
            await PostHumanMessageAsync(browser, other.MeetingId, "Other meeting remains writable");
            var otherAgent = Assert.IsType<AgentJoinResultDto>((await JoinReliableForTestAsync(test.Services,
                ReliableJoinRequest(other.MeetingCode, "IndependentWorker", "create"), RecoveryTransport(224))
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Data);
            Assert.True((await test.Run(s => s.RenewReliableAsync(new(otherAgent.Connection.ConnectionSessionId), RecoveryTransport(224)))).Succeeded);
            await using var scope = test.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoundTableMeetingDbContext>();
            Assert.Equal(1, await db.AgentConnectionSessions.CountAsync(c => c.MeetingId == test.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.AgentRecoveryOperations.CountAsync(o => o.MeetingId == test.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.MeetingMessages.CountAsync(m => m.MeetingId == test.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(1, await db.MeetingMessages.CountAsync(m => m.MeetingId == other.MeetingId, cancellationToken: TestContext.Current.CancellationToken));
        }
        finally { gate.Release.TrySetResult(); }
        await deletion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await AssertAgentMeetingGoneAsync(test.Factory, test.MeetingId);
        Assert.Equal("meeting_not_found", (await JoinReliableForTestAsync(test.Services,
            ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), RecoveryTransport(225))).ErrorCode);
        Assert.False((await test.Run(s => s.ResumeReliableAsync(new(test.ConnectionId, test.RecoveryKey, Guid.NewGuid()), RecoveryTransport(226)))).Succeeded);
    }

    private sealed class PausedDeletionGate : IMeetingRealtimeGate
    {
        private readonly MeetingRealtimeGate inner = new();
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default)
            => inner.AcquireSharedAsync(meetingId, cancellationToken);
        public async ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var lease = await inner.AcquireExclusiveCloseAsync(meetingId, timeout, cancellationToken);
            if (lease is null) return null;
            try
            {
                Held.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveMutationAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => inner.AcquireExclusiveMutationAsync(meetingId, timeout, cancellationToken);
        public void Forget(Guid meetingId) => inner.Forget(meetingId);
    }
}
