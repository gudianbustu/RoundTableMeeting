using Microsoft.Extensions.DependencyInjection;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.IntegrationTests;

public sealed partial class McpAgentContractTests
{
    [Theory]
    [InlineData("join")]
    [InlineData("takeover")]
    [InlineData("resume")]
    [InlineData("reply")]
    [InlineData("scan")]
    public async Task DeletionWaitsForAnAlreadyAdmittedReliableOperationWithoutReverseLocking(string operation)
    {
        var gate = new ObservedClosingGate();
        using var test = await ReliableRaceFixture.CreateAsync(meetingGate: gate);
        using var browser = test.Factory.CreateClient(new() { HandleCookies = true });
        await LoginAsync(browser, "alice", "test-password-alice");
        var other = await CreateMeetingAsync(browser, "Other meeting during pending deletion");
        var registration = Assert.Single(test.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(test.MeetingId));
        var round = test.Coordinator.Arm();
        var admitted = operation switch
        {
            "join" => JoinReliableForTestAsync(test.Services, ReliableJoinRequest(test.MeetingCode, "Worker", "create"), RecoveryTransport(252)),
            "takeover" => JoinReliableForTestAsync(test.Services, ReliableJoinRequest(test.MeetingCode, "Worker", "takeover"), RecoveryTransport(252)),
            "resume" => test.Run(s => s.ResumeReliableAsync(new(test.ConnectionId, test.RecoveryKey, Guid.NewGuid()), RecoveryTransport(252))),
            "reply" => test.Run(s => s.ReplyReliableAsync(test.Reply, test.Transport)),
            _ => test.Run(async s => AgentOperationResult.Success(await s.ReconcileConnectionAsync(registration), "Scan completed."))
        };
        Task? deletion = null;
        try
        {
            await round.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            deletion = DeleteLifecycleMeetingAsync(browser, test.MeetingId, "Reliable ordered races");
            await gate.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(deletion.IsCompleted);
            Assert.False(admitted.IsCompleted);
            Assert.Null(await gate.AcquireSharedAsync(test.MeetingId));
            await PostHumanMessageAsync(browser, other.MeetingId, "Human is not blocked by the pending deletion");
            Assert.True((await JoinReliableForTestAsync(test.Services,
                ReliableJoinRequest(other.MeetingCode, "IndependentWorker", "create"), RecoveryTransport(253))
                .WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        }
        finally { round.Release.TrySetResult(); }
        var result = await admitted.WaitAsync(TimeSpan.FromSeconds(10));
        if (operation == "join") Assert.Equal("agent_connection_already_active", result.ErrorCode);
        else Assert.True(result.Succeeded, result.ErrorCode);
        await deletion!.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertAgentMeetingGoneAsync(test.Factory, test.MeetingId);
        Assert.Empty(test.Services.GetRequiredService<IAgentConnectionRegistry>().ListRegistrations(test.MeetingId));
    }

    private sealed class ObservedClosingGate : IMeetingRealtimeGate
    {
        private readonly MeetingRealtimeGate inner = new();
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(Guid meetingId, CancellationToken cancellationToken = default)
            => inner.AcquireSharedAsync(meetingId, cancellationToken);
        public ValueTask<IMeetingRealtimeExclusiveLease?> AcquireExclusiveCloseAsync(Guid meetingId, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // The call synchronously sets Closing before yielding for the admitted reader.
            var pending = inner.AcquireExclusiveCloseAsync(meetingId, timeout, cancellationToken);
            Closing.TrySetResult();
            return pending;
        }
        public void Forget(Guid meetingId) => inner.Forget(meetingId);
    }
}
