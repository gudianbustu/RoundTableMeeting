namespace RoundTableMeeting.Domain.Entities;

public sealed class MeetingCreationReceipt : BaseEntity
{
    public Guid CreatorUserId { get; private set; }
    public Guid CreationRequestId { get; private set; }
    public Guid? MeetingId { get; private set; }
    public MeetingCreationReceiptState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ExpiresAtUtc { get; private set; }
    public MeetingCreationReceipt() { }
    private MeetingCreationReceipt(Guid creatorUserId, Guid requestId, Guid meetingId, DateTimeOffset createdAtUtc)
    {
        if (creatorUserId == Guid.Empty || requestId == Guid.Empty || meetingId == Guid.Empty) throw new ArgumentException("Receipt identifiers are required.");
        CreatorUserId = creatorUserId; CreationRequestId = requestId; MeetingId = meetingId; State = MeetingCreationReceiptState.Active; CreatedAtUtc = createdAtUtc;
    }
    public static MeetingCreationReceipt Create(Guid creatorUserId, Guid requestId, Guid meetingId, DateTimeOffset createdAtUtc) => new(creatorUserId, requestId, meetingId, createdAtUtc);
    public void MarkDeleted(DateTimeOffset deletedAtUtc) { State = MeetingCreationReceiptState.Deleted; MeetingId = null; ExpiresAtUtc = deletedAtUtc.AddDays(30); }
}
public enum MeetingCreationReceiptState { Active, Deleted }
