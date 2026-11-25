namespace RoundTableMeeting.Domain.Entities;

/// <summary>受会议和消息范围保护的附件元数据；文件名不是存储路径。</summary>
public sealed class MeetingAttachment : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public int? MessagePosition { get; private set; }
    public Guid UploaderParticipantId { get; private set; }
    public Guid UploadRequestId { get; private set; }
    public string OriginalName { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string MediaType { get; private set; } = "application/octet-stream";
    public string StorageKey { get; private set; } = "";
    public string ContentHash { get; private set; } = "";
    public string State { get; private set; } = "staged";
    public Guid? MessageId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public MeetingAttachment() { }
    public static MeetingAttachment Stage(Guid meetingId, Guid uploader, Guid requestId, string name,
        long size, string mediaType, string key, string hash, DateTimeOffset now)
    {
        if (meetingId == Guid.Empty || uploader == Guid.Empty || requestId == Guid.Empty || size < 0
            || string.IsNullOrWhiteSpace(name) || name.Length > 512 || key.Length != 32 || hash.Length != 64)
            throw new ArgumentException("Invalid attachment metadata.");
        return new() { MeetingId = meetingId, UploaderParticipantId = uploader, UploadRequestId = requestId,
            OriginalName = name, SizeBytes = size, MediaType = mediaType, StorageKey = key, ContentHash = hash, CreatedAtUtc = now };
    }
    public void Bind(Guid messageId, int position)
    {
        if (State != "staged" || MessageId is not null || messageId == Guid.Empty) throw new InvalidOperationException("Attachment is unavailable.");
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        MessagePosition = position;
        MessageId = messageId; State = "attached";
    }
}
