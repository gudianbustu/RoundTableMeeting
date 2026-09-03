using RoundTableMeeting.Domain.Entities;
namespace RoundTableMeeting.Domain.Repositories;
public interface IMessageRepository
{
    Task<MeetingMessage?> FindBySubmissionAsync(Guid meetingId, Guid participantId, Guid submissionId, CancellationToken cancellationToken = default);
    Task<MeetingMessage?> FindSystemBySubmissionAsync(Guid meetingId, Guid submissionId, CancellationToken cancellationToken = default);
    Task<long> GetNextSequenceAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeetingMessage>> ListAsync(Guid meetingId, long? before, long? after, long? through, int limit, CancellationToken cancellationToken = default);
    Task<bool> HasBeforeAsync(Guid meetingId, long sequence, CancellationToken cancellationToken = default);
    Task<bool> HasAfterAsync(Guid meetingId, long sequence, long? through, CancellationToken cancellationToken = default);
    Task<MeetingMessage?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageMention>> ListMentionsAsync(Guid messageId, CancellationToken cancellationToken = default);
}
