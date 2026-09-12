using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Repositories;

public interface IMessageRepository
{
    Task<IReadOnlyList<MessageMention>> ListMentionsForMessagesAsync(Guid meetingId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default);

	Task<MeetingMessage?> FindBySubmissionAsync(Guid meetingId, Guid participantId, Guid submissionId, CancellationToken cancellationToken = default(CancellationToken));

	Task<MeetingMessage?> FindSystemBySubmissionAsync(Guid meetingId, Guid submissionId, CancellationToken cancellationToken = default(CancellationToken));

	Task<long> GetNextSequenceAsync(Guid meetingId, CancellationToken cancellationToken = default(CancellationToken));

	Task<MessageSequenceSummary> GetSequenceSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<MeetingMessage>> ListThroughSequenceAsync(Guid meetingId, long throughSequence, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<MeetingMessage>> ListAsync(Guid meetingId, long? before, long? after, long? through, int limit, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> HasBeforeAsync(Guid meetingId, long sequence, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> HasAfterAsync(Guid meetingId, long sequence, long? through, CancellationToken cancellationToken = default(CancellationToken));

	Task<MeetingMessage?> FindByIdAsync(Guid meetingId, Guid id, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<MeetingMessage>> ListAfterSequenceThroughAsync(Guid meetingId, long afterSequence, long throughSequence, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<MessageMention>> ListMentionsAsync(Guid meetingId, Guid messageId, CancellationToken cancellationToken = default(CancellationToken));
}
