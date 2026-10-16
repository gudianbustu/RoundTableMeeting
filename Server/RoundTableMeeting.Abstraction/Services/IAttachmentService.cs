using RoundTableMeeting.Abstraction.Contracts.RealtimeChat;
namespace RoundTableMeeting.Abstraction.Services;

public interface IAttachmentService
{
    Task<AttachmentDto> UploadAsync(Guid meetingId, Guid uploadRequestId, string name, Stream content, CancellationToken ct = default);
    Task<AttachmentPageDto> ListAsync(Guid meetingId, MessagePageRequest request, CancellationToken ct = default);
    Task<AttachmentDownload> OpenAsync(Guid meetingId, Guid attachmentId, CancellationToken ct = default);
}
public sealed record AttachmentDownload(Stream Content, string Name, string MediaType, long SizeBytes);
public sealed class AttachmentOperationException(string code, int status, long? limitBytes = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public long? LimitBytes { get; } = limitBytes;
}
public interface IAttachmentStore
{
    Task<StagedAttachmentFile> WriteAsync(Guid meetingId, Stream source, CancellationToken ct);
    Stream Open(Guid meetingId, string storageKey);
    void Delete(Guid meetingId, string storageKey);
    Task DeleteMeetingAsync(Guid meetingId, CancellationToken ct);
    IReadOnlyList<OrphanAttachmentFile> ListOldFiles(DateTimeOffset before);
}
public sealed record StagedAttachmentFile(string StorageKey, long SizeBytes, string ContentHash, string MediaType);
public sealed record OrphanAttachmentFile(Guid MeetingId, string StorageKey);
