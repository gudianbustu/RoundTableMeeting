using RoundTableMeeting.RealtimeChat;

namespace RoundTableMeeting.IntegrationTests;

public sealed class MeetingRealtimeGateTests
{
    [Fact]
    public async Task ExclusiveCloseRejectsNewReadersAndWaitsForExistingReader()
    {
        var gate = new MeetingRealtimeGate();
        var meetingId = Guid.NewGuid();
        using var shared = await gate.AcquireSharedAsync(meetingId);
        Assert.NotNull(shared);

        var closing = gate.AcquireExclusiveCloseAsync(meetingId, TimeSpan.FromSeconds(2)).AsTask();
        await Task.Delay(20);
        Assert.Null(await gate.AcquireSharedAsync(meetingId));

        shared.Dispose();
        using var exclusive = await closing;
        Assert.NotNull(exclusive);
        exclusive.CommitClosed();
        Assert.Null(await gate.AcquireSharedAsync(meetingId));

        gate.Forget(meetingId);
        using var reopened = await gate.AcquireSharedAsync(meetingId);
        Assert.NotNull(reopened);
    }
    [Fact]
    public async Task MembershipMutationWaitsReadersWithoutRejectingTheRoom()
    {
        var gate = new MeetingRealtimeGate();
        var id = Guid.NewGuid();
        using var initial = await gate.AcquireSharedAsync(id);
        var mutation = gate.AcquireExclusiveMutationAsync(id, TimeSpan.FromSeconds(3)).AsTask();
        var waiting = gate.AcquireSharedAsync(id).AsTask();
        Assert.False(waiting.IsCompleted);
        initial!.Dispose();
        using var exclusive = await mutation;
        Assert.NotNull(exclusive);
        Assert.False(waiting.IsCompleted);
        exclusive.Dispose();
        using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(resumed);
    }
}
