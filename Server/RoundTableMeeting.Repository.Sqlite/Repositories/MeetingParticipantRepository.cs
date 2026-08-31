using Microsoft.EntityFrameworkCore;
using Materal.Extensions.DependencyInjection;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class MeetingParticipantRepository : SqliteRepository<MeetingParticipant, Guid>, IMeetingParticipantRepository, IScopedDependency<IMeetingParticipantRepository>
{
    private readonly RoundTableMeetingDbContext dbContext;
    public MeetingParticipantRepository(RoundTableMeetingDbContext dbContext) : base(dbContext) => this.dbContext = dbContext;
    public Task<MeetingParticipant?> FindByUserAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default)
        => dbContext.Set<MeetingParticipant>().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.UserId == userId, cancellationToken);
    public Task<bool> IsNameTakenAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default)
        => dbContext.Set<MeetingParticipant>().AnyAsync(x => x.MeetingId == meetingId && x.NormalizedDisplayName == normalizedDisplayName, cancellationToken);
    public async Task<IReadOnlyList<MeetingParticipant>> ListAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Set<MeetingParticipant>().AsNoTracking().Where(x => x.MeetingId == meetingId).ToListAsync(cancellationToken);
        return rows.OrderBy(participant => participant.JoinedAtUtc).ThenBy(participant => participant.ID).ToArray();
    }
}
