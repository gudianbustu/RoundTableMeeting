namespace RoundTableMeeting.Abstraction.Services;

/// <summary>Request-scoped identity set only by a validated Agent transport adapter.</summary>
public sealed class MeetingAgentContext
{
    private (Guid MeetingId, Guid ParticipantId)? actor;
    public Guid? ParticipantFor(Guid meetingId) => actor is { } value && value.MeetingId == meetingId ? value.ParticipantId : null;
    public IDisposable Enter(Guid meetingId, Guid participantId)
    {
        if (actor is not null) throw new InvalidOperationException("An Agent identity is already bound.");
        actor = (meetingId, participantId);
        return new Scope(() => actor = null);
    }
    // The adapter holds the shared gate before entering this identity. Re-entering a
    // writer-preferring gate could deadlock while a membership mutation waits.
    public ValueTask<IMeetingRealtimeSharedLease?> AcquireSharedAsync(IMeetingRealtimeGate gate, Guid meetingId, CancellationToken ct)
        => ParticipantFor(meetingId) is not null ? ValueTask.FromResult<IMeetingRealtimeSharedLease?>(new BorrowedLease()) : gate.AcquireSharedAsync(meetingId, ct);
    private sealed class BorrowedLease : IMeetingRealtimeSharedLease { public void Dispose() { } }
    private sealed class Scope(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}
