using Materal.TTA.Common;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Repositories;
public interface IMeetingParticipantRepository : IRepository<MeetingParticipant, Guid>
{
    Task<MeetingParticipant?> FindByUserAsync(Guid meetingId, Guid userId, CancellationToken cancellationToken = default);
    Task<bool> IsNameTakenAsync(Guid meetingId, string normalizedDisplayName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeetingParticipant>> ListAsync(Guid meetingId, CancellationToken cancellationToken = default);
}
