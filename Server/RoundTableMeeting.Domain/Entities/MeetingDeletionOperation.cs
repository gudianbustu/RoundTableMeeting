namespace RoundTableMeeting.Domain.Entities;

// This receipt has no foreign key to deleted business data and contains no meeting content.
public sealed class MeetingDeletionOperation : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid CreatorUserId { get; private set; }
    public bool Completed { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public MeetingDeletionOperation() { }
    public static MeetingDeletionOperation Create(Guid meetingId, Guid creatorUserId, Guid? operationId = null)
    {
        if (meetingId == Guid.Empty || creatorUserId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("Deletion identity is required.");
        return new() { OperationId = operationId ?? Guid.NewGuid(), MeetingId = meetingId, CreatorUserId = creatorUserId };
    }
    public void Complete(DateTimeOffset now) { Completed = true; CompletedAtUtc ??= now; }
}
