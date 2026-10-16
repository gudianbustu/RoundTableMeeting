namespace RoundTableMeeting.Domain.Entities;

/// <summary>只在获准参与者范围内连续编号的消息投影。</summary>
public sealed class ParticipantMessageView : BaseEntity
{
    public Guid MeetingId { get; set; }
    public Guid ParticipantId { get; set; }
    public Guid MessageId { get; set; }
    public long Sequence { get; set; }
}

/// <summary>参与者自己的可见序号；不会因第三人的私聊递增。</summary>
public sealed class ParticipantMessageCounter : BaseEntity
{
    public Guid MeetingId { get; set; }
    public Guid ParticipantId { get; set; }
    public long HighWatermark { get; set; }
}
