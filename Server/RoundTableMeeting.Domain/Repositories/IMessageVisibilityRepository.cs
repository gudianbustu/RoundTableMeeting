using RoundTableMeeting.Domain.Entities;
namespace RoundTableMeeting.Domain.Repositories;

public sealed record VisibleMessage(MeetingMessage Message, long Sequence);
public interface IMessageVisibilityRepository
{
    Task<long> HighWatermarkAsync(Guid meetingId, Guid participantId, CancellationToken ct = default);
    Task<VisibleMessage?> FindAsync(Guid meetingId, Guid participantId, Guid messageId, long? through = null, CancellationToken ct = default);
    Task<IReadOnlyList<VisibleMessage>> PageAsync(Guid meetingId, Guid participantId, string scope, Guid? peer,
        long? before, long? after, long? through, int limit, CancellationToken ct = default, bool attachmentsOnly = false);
    Task<IReadOnlyList<ParticipantMessageView>> RecipientsAsync(Guid meetingId, Guid messageId, CancellationToken ct = default);
}
public interface IMeetingAttachmentRepository
{
    Task<MeetingAttachment?> FindAsync(Guid meetingId, Guid id, CancellationToken ct = default);
    Task<MeetingAttachment?> FindStorageAsync(Guid meetingId, string storageKey, CancellationToken ct = default);
    Task<MeetingAttachment?> FindUploadAsync(Guid meetingId, Guid participantId, Guid requestId, CancellationToken ct = default);
    Task<IReadOnlyList<MeetingAttachment>> ForMessageAsync(Guid meetingId, Guid messageId, CancellationToken ct = default);
    Task<IReadOnlyList<MeetingAttachment>> ExpiredAsync(DateTimeOffset before, CancellationToken ct = default);
}
