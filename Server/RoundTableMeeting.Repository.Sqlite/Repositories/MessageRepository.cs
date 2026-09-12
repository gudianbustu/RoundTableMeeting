using Microsoft.EntityFrameworkCore;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class MessageRepository(RoundTableMeetingDbContext dbContext) : IMessageRepository, IScopedDependency<IMessageRepository>
{
    public async Task<IReadOnlyList<MessageMention>> ListMentionsForMessagesAsync(Guid meetingId,
        IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default)
        => await dbContext.MessageMentions.AsNoTracking()
            .Where(item => item.MeetingId == meetingId && messageIds.Contains(item.MessageId))
            .OrderBy(item => item.StartOffset).ToArrayAsync(cancellationToken);

    public Task<MeetingMessage?> FindBySubmissionAsync(Guid meetingId, Guid participantId, Guid submissionId, CancellationToken cancellationToken = default)
        => dbContext.MeetingMessages.AsNoTracking().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.AuthorParticipantId == participantId && x.ClientSubmissionId == submissionId, cancellationToken);

    public Task<MeetingMessage?> FindSystemBySubmissionAsync(Guid meetingId, Guid submissionId, CancellationToken cancellationToken = default)
        => dbContext.MeetingMessages.AsNoTracking().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.AuthorKind == RoundTableMeeting.Domain.Enums.MessageAuthorKind.System && x.ClientSubmissionId == submissionId, cancellationToken);

    public async Task<long> GetNextSequenceAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        var current = await dbContext.MeetingMessages.Where(x => x.MeetingId == meetingId).MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
        if (current >= 9_007_199_254_740_991) throw new InvalidOperationException("Message sequence capacity exhausted.");
        return current + 1;
    }

    public async Task<MessageSequenceSummary> GetSequenceSummaryAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        var summary = await dbContext.MeetingMessages.AsNoTracking()
            .Where(x => x.MeetingId == meetingId)
            .GroupBy(_ => 1)
            .Select(group => new MessageSequenceSummary(group.Max(x => x.Sequence), group.Min(x => (long?)x.Sequence), group.Count()))
            .SingleOrDefaultAsync(cancellationToken);
        return summary ?? new MessageSequenceSummary(0, null, 0);
    }

    public async Task<IReadOnlyList<MeetingMessage>> ListThroughSequenceAsync(Guid meetingId, long throughSequence, CancellationToken cancellationToken = default)
        => await dbContext.MeetingMessages.AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.Sequence <= throughSequence)
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<MeetingMessage>> ListAsync(Guid meetingId, long? before, long? after, long? through, int limit, CancellationToken cancellationToken = default)
    {
        var query = dbContext.MeetingMessages.AsNoTracking().Where(x => x.MeetingId == meetingId);
        if (before.HasValue) query = query.Where(x => x.Sequence < before.Value).OrderByDescending(x => x.Sequence).Take(limit);
        else if (after.HasValue) query = query.Where(x => x.Sequence > after.Value && (!through.HasValue || x.Sequence <= through.Value)).OrderBy(x => x.Sequence).Take(limit);
        else query = query.Where(x => !through.HasValue || x.Sequence <= through.Value).OrderByDescending(x => x.Sequence).Take(limit);
        var rows = await query.ToListAsync(cancellationToken);
        return before.HasValue || !after.HasValue ? rows.OrderBy(x => x.Sequence).ToArray() : rows;
    }

    public Task<bool> HasBeforeAsync(Guid meetingId, long sequence, CancellationToken cancellationToken = default)
        => dbContext.MeetingMessages.AnyAsync(x => x.MeetingId == meetingId && x.Sequence < sequence, cancellationToken);

    public Task<bool> HasAfterAsync(Guid meetingId, long sequence, long? through, CancellationToken cancellationToken = default)
        => dbContext.MeetingMessages.AnyAsync(x => x.MeetingId == meetingId && x.Sequence > sequence && (!through.HasValue || x.Sequence <= through.Value), cancellationToken);

    public Task<MeetingMessage?> FindByIdAsync(Guid meetingId, Guid id, CancellationToken cancellationToken = default)
        => dbContext.MeetingMessages.AsNoTracking().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.ID == id, cancellationToken);

    public async Task<IReadOnlyList<MeetingMessage>> ListAfterSequenceThroughAsync(Guid meetingId, long afterSequence, long throughSequence, CancellationToken cancellationToken = default)
        => await dbContext.MeetingMessages.AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.Sequence > afterSequence && x.Sequence <= throughSequence)
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<MessageMention>> ListMentionsAsync(Guid meetingId, Guid messageId, CancellationToken cancellationToken = default)
        => await dbContext.MessageMentions.AsNoTracking()
            .Where(x => x.MeetingId == meetingId && x.MessageId == messageId)
            .OrderBy(x => x.StartOffset)
            .ToArrayAsync(cancellationToken);

}
