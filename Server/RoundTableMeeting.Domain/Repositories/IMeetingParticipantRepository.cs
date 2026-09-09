using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Materal.TTA.Common;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Repositories;

public interface IMeetingParticipantRepository : IRepository<MeetingParticipant, Guid>, IRepository<MeetingParticipant>, IRepository
{
	Task<MeetingParticipant?> FindByUserAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default(CancellationToken));

	Task<MeetingParticipant?> FindByNormalizedNameAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> IsNameTakenAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<MeetingParticipant>> ListAsync(Guid meetingId, CancellationToken cancellationToken = default(CancellationToken));
}
