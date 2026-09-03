namespace RoundTableMeeting.Domain.Entities;

public sealed class MessageAppendedEvent : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid MessageId { get; private set; }
    public long Sequence { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public MessageAppendedEvent() { }
    private MessageAppendedEvent(Guid meetingId, Guid messageId, long sequence, DateTimeOffset occurredAtUtc)
    {
        if (meetingId == Guid.Empty) throw new ArgumentException("Meeting is required.", nameof(meetingId));
        if (messageId == Guid.Empty) throw new ArgumentException("Message is required.", nameof(messageId));
        if (sequence is < 1 or > 9_007_199_254_740_991) throw new ArgumentOutOfRangeException(nameof(sequence));
        MeetingId = meetingId; MessageId = messageId; Sequence = sequence; OccurredAtUtc = occurredAtUtc;
    }
    public static MessageAppendedEvent Create(Guid meetingId, Guid messageId, long sequence, DateTimeOffset occurredAtUtc)
        => new(meetingId, messageId, sequence, occurredAtUtc);
}
