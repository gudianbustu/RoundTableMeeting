using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;
namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class MeetingLifecycleRepository(RoundTableMeetingDbContext dbContext)
    : IMeetingLifecycleRepository, IScopedDependency<IMeetingLifecycleRepository>
{
    public Task<MeetingDeletionOperation?> FindDeletionAsync(Guid meetingId, CancellationToken cancellationToken = default)
        => dbContext.Set<MeetingDeletionOperation>().SingleOrDefaultAsync(x => x.MeetingId == meetingId, cancellationToken);
    public Task<MeetingLeaveOperation?> FindLeaveAsync(Guid meetingId, Guid participantId, Guid operationId, CancellationToken cancellationToken = default)
        => dbContext.Set<MeetingLeaveOperation>().SingleOrDefaultAsync(x => x.MeetingId == meetingId && x.ParticipantId == participantId && x.OperationId == operationId, cancellationToken);
    public async Task<IReadOnlyList<MeetingLeaveOperation>> ListLeavesAsync(Guid meetingId, CancellationToken cancellationToken = default)
        => await dbContext.Set<MeetingLeaveOperation>().Where(x => x.MeetingId == meetingId).ToArrayAsync(cancellationToken);
    public Task<bool> HasProofAsync(Guid meetingId, string proofHash, CancellationToken cancellationToken = default)
        => dbContext.Set<MeetingLifecycleProof>().AnyAsync(x => x.MeetingId == meetingId && x.ProofHash == proofHash, cancellationToken);
}
