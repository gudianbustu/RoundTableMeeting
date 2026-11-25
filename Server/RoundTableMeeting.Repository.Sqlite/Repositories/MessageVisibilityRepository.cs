using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class MessageVisibilityRepository(RoundTableMeetingDbContext db) : IMessageVisibilityRepository, IScopedDependency<IMessageVisibilityRepository>
{
    public async Task<long> HighWatermarkAsync(Guid meetingId, Guid participantId, CancellationToken ct = default)
        => await db.Set<ParticipantMessageCounter>().Where(x => x.MeetingId == meetingId && x.ParticipantId == participantId).Select(x => (long?)x.HighWatermark).SingleOrDefaultAsync(ct) ?? 0;
    public async Task<VisibleMessage?> FindAsync(Guid meetingId, Guid participantId, Guid messageId, long? through = null, CancellationToken ct = default)
        => await Query(meetingId, participantId, "all", null, through).Where(x => x.Message.ID == messageId)
            .Select(x => new VisibleMessage(x.Message, x.Sequence, x.AuthorName)).SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<VisibleMessage>> PageAsync(Guid meetingId, Guid participantId, string scope, Guid? peer, long? before, long? after, long? through, int limit, CancellationToken ct = default, bool attachmentsOnly = false)
    {
        var q = Query(meetingId, participantId, scope, peer, through);
        if (attachmentsOnly) q = q.Where(x => db.Set<MeetingAttachment>().Any(a => a.MessageId == x.Message.ID));
        if (before.HasValue) q = q.Where(x => x.Sequence < before.Value);
        if (after.HasValue) q = q.Where(x => x.Sequence > after.Value);
        var rows = await (after.HasValue ? q.OrderBy(x => x.Sequence) : q.OrderByDescending(x => x.Sequence)).Take(limit).ToArrayAsync(ct);
        return rows.OrderBy(x => x.Sequence).Select(x => new VisibleMessage(x.Message, x.Sequence, x.AuthorName)).ToArray();
    }
    private sealed class ProjectionRow
    {
        public MeetingMessage Message { get; init; } = null!;
        public long Sequence { get; init; }
        public string? AuthorName { get; init; }
    }
    private IQueryable<ProjectionRow> Query(Guid meetingId, Guid participantId, string scope, Guid? peer, long? through)
    {
        var q = from v in db.Set<ParticipantMessageView>().AsNoTracking()
                join m in db.MeetingMessages.AsNoTracking() on v.MessageId equals m.ID
                where v.MeetingId == meetingId && v.ParticipantId == participantId && (!through.HasValue || v.Sequence <= through.Value)
                select new ProjectionRow { Message = m, Sequence = v.Sequence,
                    AuthorName = db.Set<MeetingParticipant>().Where(p => p.MeetingId == meetingId && p.ID == m.AuthorParticipantId)
                        .Select(p => p.DisplayName).FirstOrDefault() };
        if (scope != "all") q = q.Where(x => x.Message.Visibility == scope);
        if (peer.HasValue) q = q.Where(x => x.Message.Visibility == "private" && (x.Message.AuthorParticipantId == peer || x.Message.RecipientParticipantId == peer));
        return q;
    }
    public async Task<IReadOnlyList<ParticipantMessageView>> RecipientsAsync(Guid meetingId, Guid messageId, CancellationToken ct = default)
        => await db.Set<ParticipantMessageView>().AsNoTracking().Where(x => x.MeetingId == meetingId && x.MessageId == messageId).ToArrayAsync(ct);
}
public sealed class MeetingAttachmentRepository(RoundTableMeetingDbContext db) : IMeetingAttachmentRepository, IScopedDependency<IMeetingAttachmentRepository>
{
    public Task<MeetingAttachment?> FindAsync(Guid meetingId, Guid id, CancellationToken ct = default)
        => db.Set<MeetingAttachment>().AsNoTracking().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.ID == id, ct);
    public Task<MeetingAttachment?> FindStorageAsync(Guid meetingId, string storageKey, CancellationToken ct = default)
        => db.Set<MeetingAttachment>().AsNoTracking().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.StorageKey == storageKey, ct);
    public Task<MeetingAttachment?> FindUploadAsync(Guid meetingId, Guid participantId, Guid requestId, CancellationToken ct = default)
        => db.Set<MeetingAttachment>().AsNoTracking().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.UploaderParticipantId == participantId && x.UploadRequestId == requestId, ct);
    public async Task<IReadOnlyList<MeetingAttachment>> ForMessageAsync(Guid meetingId, Guid messageId, CancellationToken ct = default)
        => await db.Set<MeetingAttachment>().AsNoTracking().Where(x => x.MeetingId == meetingId && x.MessageId == messageId).OrderBy(x => x.MessagePosition).ThenBy(x => x.ID).ToArrayAsync(ct);
    public async Task<IReadOnlyList<MeetingAttachment>> ExpiredAsync(DateTimeOffset before, CancellationToken ct = default)
        => (await db.Set<MeetingAttachment>().AsNoTracking().Where(x => x.State == "staged").ToArrayAsync(ct)).Where(x => x.CreatedAtUtc < before).ToArray();
}
