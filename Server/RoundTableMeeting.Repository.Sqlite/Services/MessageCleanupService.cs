using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Services;

public sealed class MessageCleanupService(RoundTableMeetingDbContext dbContext) : IMessageCleanupService, IScopedDependency<IMessageCleanupService>
{
    public async Task DeleteForMeetingAsync(Guid meetingId, CancellationToken cancellationToken = default)
    {
        await dbContext.MessageEventDeliveries
            .Where(delivery => dbContext.MessageAppendedEvents.Any(evt => evt.ID == delivery.EventId && evt.MeetingId == meetingId))
            .ExecuteDeleteAsync(cancellationToken);
        await dbContext.MessageAppendedEvents.Where(evt => evt.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.MessageMentions.Where(mention => mention.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Set<MeetingAttachment>().Where(x => x.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Set<ParticipantMessageView>().Where(x => x.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.Set<ParticipantMessageCounter>().Where(x => x.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.MeetingMessages.Where(message => message.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
    }
}
