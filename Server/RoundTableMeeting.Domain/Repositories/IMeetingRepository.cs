using Materal.TTA.Common;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Repositories;
public interface IMeetingRepository : IRepository<Meeting, Guid>
{
    Task<Meeting?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Meeting?> FindActiveByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Meeting?> FindActiveByCodeAsync(Guid code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(Meeting Meeting, MeetingParticipant Participant)>> ListForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<MeetingCreationReceipt?> FindReceiptAsync(Guid creatorUserId, Guid requestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Meeting>> ListDeletingAsync(CancellationToken cancellationToken = default);
    Task<MeetingCreationReceipt?> FindActiveReceiptByMeetingIdAsync(Guid meetingId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeetingCreationReceipt>> ListExpiredDeletedReceiptsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
