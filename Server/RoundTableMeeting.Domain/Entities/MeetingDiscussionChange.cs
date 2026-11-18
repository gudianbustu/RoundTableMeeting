namespace RoundTableMeeting.Domain.Entities;

public sealed class MeetingDiscussionChange : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public long Sequence { get; private set; }
    public bool FreeDiscussion { get; private set; }

    private MeetingDiscussionChange() { }

    public static MeetingDiscussionChange Create(Guid meetingId, long sequence, bool freeDiscussion)
        => new() { MeetingId = meetingId, Sequence = sequence, FreeDiscussion = freeDiscussion };
}
