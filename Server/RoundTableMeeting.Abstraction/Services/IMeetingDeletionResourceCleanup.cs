namespace RoundTableMeeting.Abstraction.Services;

// Runs before the database purge, outside its write transaction. Implementations must be idempotent.
// Batch 2 registers the managed attachment-store implementation here.
public interface IMeetingDeletionResourceCleanup
{
    Task DeleteForMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default);
}
