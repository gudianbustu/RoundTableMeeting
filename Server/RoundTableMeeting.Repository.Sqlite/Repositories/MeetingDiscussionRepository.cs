using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class MeetingDiscussionRepository(RoundTableMeetingDbContext db) : IMeetingDiscussionRepository, IScopedDependency<IMeetingDiscussionRepository>
{
    public Task<bool> WasInterruptedAsync(Guid meetingId, long sequence, CancellationToken ct)
        => db.Set<MeetingDiscussionChange>().AnyAsync(change => change.MeetingId == meetingId
            && !change.FreeDiscussion && change.Sequence > sequence, ct);

    public Task<MeetingDiscussionTurn?> FindTurnAsync(Guid messageId, CancellationToken ct)
        => db.Set<MeetingDiscussionTurn>().AsNoTracking().SingleOrDefaultAsync(t => t.ID == messageId, ct);

    public async Task<MeetingDiscussionTurn?> ClassifyAsync(MeetingMessage message, bool mentioned, bool startsPublicTopic, CancellationToken ct, bool groupMention = false)
    {
        var mode = await db.Set<MeetingDiscussionChange>().Where(x => x.MeetingId == message.MeetingId)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        if (mode?.FreeDiscussion != true) return null;
        if (groupMention) return MeetingDiscussionTurn.Create(message, message.ID, true);
        if (startsPublicTopic || message.AuthorKind == Domain.Enums.MessageAuthorKind.Human)
            return mentioned ? null : MeetingDiscussionTurn.Create(message, message.ID, true);

        var turns = db.Set<MeetingDiscussionTurn>().Where(t => t.MeetingId == message.MeetingId && t.Sequence > mode.Sequence);
        var topic = message.ReplyToMessageId is Guid parent
            ? await turns.Where(t => t.ID == parent).Select(t => (Guid?)t.TopicId).SingleOrDefaultAsync(ct)
            : await turns.Where(t => t.ID == t.TopicId).OrderByDescending(t => t.Sequence).Select(t => (Guid?)t.TopicId).FirstOrDefaultAsync(ct);
        if (!topic.HasValue) return null;
        return MeetingDiscussionTurn.Create(message, topic.Value, !mentioned);
    }

    public Task<long> InboxStartAsync(Guid meetingId, Guid participantId, CancellationToken ct)
        => db.Database.SqlQuery<long>($"""
            SELECT COALESCE((SELECT StartSequence FROM AgentInboxStarts
                WHERE MeetingId = {meetingId} AND ParticipantId = {participantId}), 0) AS Value
            """).SingleAsync(ct);

    public Task<bool> IsFreeAsync(Guid meetingId, long throughSequence, CancellationToken ct)
        => db.Set<MeetingDiscussionChange>()
            .Where(change => change.MeetingId == meetingId && change.Sequence <= throughSequence)
            .OrderByDescending(change => change.Sequence)
            .Select(change => change.FreeDiscussion)
            .FirstOrDefaultAsync(ct);
}
