using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Services;

public sealed class AgentCollaborationCleanupService(RoundTableMeetingDbContext dbContext)
    : IAgentCollaborationCleanupService, IScopedDependency<IAgentCollaborationCleanupService>
{
    public async Task DeleteForMeetingAsync(Guid meetingId, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Agent cleanup requires the meeting deletion transaction.");
        var tasks = await dbContext.AgentReplyTasks.AsTracking()
            .Where(task => task.MeetingId == meetingId && task.State != AgentReplyTaskState.Replied && task.State != AgentReplyTaskState.Cancelled)
            .ToArrayAsync(cancellationToken);
        foreach (var task in tasks) task.Cancel(cancelledAtUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
        foreach (var task in tasks) dbContext.Entry(task).State = EntityState.Detached;
        await dbContext.AgentContextCursors.Where(item => item.MeetingId == meetingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CurrentConnectionId, (Guid?)null), cancellationToken);
        await dbContext.AgentReplyTasks.Where(item => item.MeetingId == meetingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CurrentAttemptId, (Guid?)null), cancellationToken);
        await dbContext.AgentTaskDeliveryAttempts.Where(item => item.MeetingId == meetingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ReplacementAttemptId, (Guid?)null), cancellationToken);
        await dbContext.AgentConnectionSessions.Where(item => item.MeetingId == meetingId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SupersededByConnectionId, (Guid?)null), cancellationToken);
        await dbContext.AgentRecoveryOperations.Where(item => item.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.AgentInitialContextDeliveries.Where(item => item.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.AgentTaskDeliveryAttempts.Where(item => item.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.AgentReplyTasks.Where(item => item.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.AgentContextCursors.Where(item => item.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.AgentConnectionSessions.Where(item => item.MeetingId == meetingId).ExecuteDeleteAsync(cancellationToken);
    }
}
