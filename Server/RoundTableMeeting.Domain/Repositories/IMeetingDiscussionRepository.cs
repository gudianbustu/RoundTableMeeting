using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Repositories;

public interface IMeetingDiscussionRepository
{
    Task<MeetingDiscussionTurn?> FindTurnAsync(Guid messageId, CancellationToken ct);
    Task<MeetingDiscussionTurn?> ClassifyAsync(MeetingMessage message, bool mentioned, bool startsPublicTopic, CancellationToken ct, bool groupMention = false);
    Task<long> InboxStartAsync(Guid meetingId, Guid participantId, CancellationToken ct);
    Task<bool> WasInterruptedAsync(Guid meetingId, long sequence, CancellationToken ct);
    Task<bool> IsFreeAsync(Guid meetingId, long throughSequence, CancellationToken ct);
}
