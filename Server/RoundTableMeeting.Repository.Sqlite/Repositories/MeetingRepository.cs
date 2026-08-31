using Microsoft.EntityFrameworkCore;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class MeetingRepository : SqliteRepository<Meeting, Guid>, IMeetingRepository, IScopedDependency<IMeetingRepository>
{
    private readonly RoundTableMeetingDbContext dbContext;
    public MeetingRepository(RoundTableMeetingDbContext dbContext) : base(dbContext) => this.dbContext = dbContext;
    public Task<Meeting?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.Set<Meeting>().SingleOrDefaultAsync(x => x.ID == id, cancellationToken);
    public Task<Meeting?> FindActiveByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.Set<Meeting>().AsNoTracking().SingleOrDefaultAsync(x => x.ID == id && x.LifecycleState == MeetingLifecycleState.Active, cancellationToken);
    public Task<Meeting?> FindActiveByCodeAsync(Guid code, CancellationToken cancellationToken = default)
        => dbContext.Set<Meeting>().AsNoTracking().SingleOrDefaultAsync(x => x.MeetingCode == code && x.LifecycleState == MeetingLifecycleState.Active, cancellationToken);
    public async Task<IReadOnlyList<(Meeting Meeting, MeetingParticipant Participant)>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var rows = await (from meeting in dbContext.Set<Meeting>().AsNoTracking()
                          join participant in dbContext.Set<MeetingParticipant>().AsNoTracking() on meeting.ID equals participant.MeetingId
                          where meeting.LifecycleState == MeetingLifecycleState.Active && participant.UserId == userId
                          select new { Meeting = meeting, Participant = participant }).ToListAsync(cancellationToken);
        rows = rows.OrderByDescending(row => row.Participant.JoinedAtUtc).ThenBy(row => row.Meeting.ID).ToList();
        return rows.Select(row => (row.Meeting, row.Participant)).ToArray();
    }
    public Task<MeetingCreationReceipt?> FindReceiptAsync(Guid creatorUserId, Guid requestId, CancellationToken cancellationToken = default)
        => dbContext.Set<MeetingCreationReceipt>().SingleOrDefaultAsync(x => x.CreatorUserId == creatorUserId && x.CreationRequestId == requestId, cancellationToken);
    public async Task<IReadOnlyList<Meeting>> ListDeletingAsync(CancellationToken cancellationToken = default)
        => await dbContext.Set<Meeting>().AsNoTracking().Where(x => x.LifecycleState == MeetingLifecycleState.Deleting).ToListAsync(cancellationToken);
    public Task<MeetingCreationReceipt?> FindActiveReceiptByMeetingIdAsync(Guid meetingId, CancellationToken cancellationToken = default)
        => dbContext.Set<MeetingCreationReceipt>().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.State == MeetingCreationReceiptState.Active, cancellationToken);
    public async Task<IReadOnlyList<MeetingCreationReceipt>> ListExpiredDeletedReceiptsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var deleted = await dbContext.Set<MeetingCreationReceipt>()
            .Where(x => x.State == MeetingCreationReceiptState.Deleted && x.ExpiresAtUtc != null)
            .ToListAsync(cancellationToken);
        return deleted.Where(receipt => receipt.ExpiresAtUtc <= nowUtc).ToArray();
    }
}
