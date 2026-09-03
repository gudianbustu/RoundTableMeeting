using System.Text;

namespace RoundTableMeeting.Domain.Entities;

public sealed class MessageMention : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid MessageId { get; private set; }
    public Guid TargetParticipantId { get; private set; }
    public string TargetDisplayName { get; private set; } = string.Empty;
    public int StartOffset { get; private set; }
    public int Length { get; private set; }
    public MessageMention() { }
    private MessageMention(Guid meetingId, Guid messageId, Guid targetParticipantId, string targetDisplayName, int startOffset, int length)
    {
        if (meetingId == Guid.Empty || messageId == Guid.Empty || targetParticipantId == Guid.Empty) throw new ArgumentException("Mention identity is required.");
        if (startOffset < 0) throw new ArgumentOutOfRangeException(nameof(startOffset));
        if (length <= 1) throw new ArgumentOutOfRangeException(nameof(length));
        var normalizedDisplayName = (targetDisplayName ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (normalizedDisplayName.Length is < 1 or > 128) throw new ArgumentException("Target display name is invalid.", nameof(targetDisplayName));
        MeetingId = meetingId; MessageId = messageId; TargetParticipantId = targetParticipantId;
        TargetDisplayName = normalizedDisplayName; StartOffset = startOffset; Length = length;
    }
    public static MessageMention Create(Guid meetingId, Guid messageId, Guid targetParticipantId, string targetDisplayName, int startOffset, int length)
        => new(meetingId, messageId, targetParticipantId, targetDisplayName, startOffset, length);
}
