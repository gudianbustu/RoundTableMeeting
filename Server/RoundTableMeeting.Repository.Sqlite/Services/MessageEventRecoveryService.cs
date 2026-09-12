using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Services;

public sealed class MessageEventRecoveryService(RoundTableMeetingDbContext dbContext, TimeProvider timeProvider)
    : IMessageEventRecoveryService, IScopedDependency<IMessageEventRecoveryService>
{
    public async Task<bool> ResumeAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var delivery = await dbContext.MessageEventDeliveries.AsTracking().SingleOrDefaultAsync(x => x.ID == deliveryId, cancellationToken);
        if (delivery is null) return false;
        var active = await (from appended in dbContext.MessageAppendedEvents
            join meeting in dbContext.Meetings on appended.MeetingId equals meeting.ID
            where appended.ID == delivery.EventId && meeting.LifecycleState == Domain.Enums.MeetingLifecycleState.Active
            select meeting.ID).AnyAsync(cancellationToken);
        if (!active || delivery.State != Domain.Enums.MessageEventDeliveryState.Suspended) return false;
        delivery.Resume(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
