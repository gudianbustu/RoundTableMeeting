namespace RoundTableMeeting.Domain.Entities;

public sealed class MeetingDiscussionTurn : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid TopicId { get; private set; }
    public long Sequence { get; private set; }
    public bool Optional { get; private set; }

    public static MeetingDiscussionTurn Create(MeetingMessage message, Guid topicId, bool optional)
        => new() { ID = message.ID, MeetingId = message.MeetingId, TopicId = topicId, Sequence = message.Sequence, Optional = optional };
}
