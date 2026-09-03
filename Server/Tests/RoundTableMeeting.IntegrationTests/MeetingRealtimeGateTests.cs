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
}
