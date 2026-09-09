using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Materal.Extensions.DependencyInjection;
using Materal.TTA.Common;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class MeetingParticipantRepository : SqliteRepository<MeetingParticipant, Guid>, IMeetingParticipantRepository, IRepository<MeetingParticipant, Guid>, IRepository<MeetingParticipant>, IRepository, IScopedDependency<IMeetingParticipantRepository>, IScopedDependency, IRegisterType, IRegisterType<IMeetingParticipantRepository>
{
	private readonly RoundTableMeetingDbContext dbContext;

	public MeetingParticipantRepository(RoundTableMeetingDbContext dbContext)
		: base(dbContext)
	{
		this.dbContext = dbContext;
	}

	public Task<MeetingParticipant?> FindByUserAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.Set<MeetingParticipant>().SingleOrDefaultAsync((MeetingParticipant x) => x.MeetingId == meetingId && x.UserId == userId, cancellationToken);
	}

	public Task<MeetingParticipant?> FindByNormalizedNameAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.Set<MeetingParticipant>().AsNoTracking().SingleOrDefaultAsync((MeetingParticipant x) => x.MeetingId == meetingId && x.NormalizedDisplayName == normalizedDisplayName, cancellationToken);
	}

	public Task<bool> IsNameTakenAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.Set<MeetingParticipant>().AnyAsync((MeetingParticipant x) => x.MeetingId == meetingId && x.NormalizedDisplayName == normalizedDisplayName, cancellationToken);
	}

	public async Task<IReadOnlyList<MeetingParticipant>> ListAsync(Guid meetingId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return (from participant in await (from x in dbContext.Set<MeetingParticipant>().AsNoTracking()
				where x.MeetingId == meetingId
				select x).ToListAsync(cancellationToken)
			orderby participant.JoinedAtUtc, participant.ID
			select participant).ToArray();
	}
}
