namespace RoundTableMeeting.Abstraction.Services;

public interface IMeetingFileTransferGate
{
    IMeetingFileTransferLease? Acquire(Guid meetingId);
    Task CancelAndDrainAsync(Guid meetingId, CancellationToken ct = default);
}
public interface IMeetingFileTransferLease : IDisposable
{
    CancellationToken CancellationToken { get; }
}
