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
        var delivery = await dbContext.MessageEventDeliveries.AsTracking().SingleOrDefaultAsync(x => x.ID == deliveryId, cancellationToken);
        if (delivery is null) return false;
        delivery.Resume(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
