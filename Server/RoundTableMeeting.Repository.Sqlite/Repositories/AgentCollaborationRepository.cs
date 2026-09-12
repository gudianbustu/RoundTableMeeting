using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Materal.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RoundTableMeeting.Domain.Entities;
using RoundTableMeeting.Domain.Repositories;
using RoundTableMeeting.Repository.Sqlite.Database;

namespace RoundTableMeeting.Repository.Sqlite.Repositories;

public sealed class AgentCollaborationRepository(RoundTableMeetingDbContext dbContext) : IAgentCollaborationRepository, IScopedDependency<IAgentCollaborationRepository>, IScopedDependency, IRegisterType, IRegisterType<IAgentCollaborationRepository>
{
    public Task<AgentReplyTask?> FindQueueHeadAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default)
        => dbContext.AgentReplyTasks.AsNoTracking().Where(item => item.MeetingId == meetingId
            && item.AgentParticipantId == participantId
            && item.State != RoundTableMeeting.Domain.Enums.AgentReplyTaskState.Replied
            && item.State != RoundTableMeeting.Domain.Enums.AgentReplyTaskState.Cancelled)
            .OrderBy(item => item.TriggerSequence).ThenBy(item => item.ID).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentReplyTask>> ListTasksInRangeAsync(Guid meetingId, Guid participantId,
        long after, long through, CancellationToken cancellationToken = default)
        => await dbContext.AgentReplyTasks.AsNoTracking().Where(item => item.MeetingId == meetingId
            && item.AgentParticipantId == participantId && item.TriggerSequence > after && item.TriggerSequence <= through)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<AgentReplyTask>> ListReplySourcesAsync(Guid meetingId,
        IReadOnlyList<Guid> replyMessageIds, CancellationToken cancellationToken = default)
        => await dbContext.AgentReplyTasks.AsNoTracking().Where(item => item.MeetingId == meetingId
            && item.ReplyMessageId.HasValue && replyMessageIds.Contains(item.ReplyMessageId.Value))
            .ToArrayAsync(cancellationToken);

    public Task<AgentCollaborationBoundary?> FindBoundaryAsync(Guid meetingId, CancellationToken cancellationToken = default)
        => dbContext.AgentCollaborationBoundaries.AsNoTracking()
            .SingleOrDefaultAsync(item => item.MeetingId == meetingId, cancellationToken);

    public Task<AgentReplyTask?> FindTaskByReplyMessageAsync(Guid meetingId, Guid replyMessageId, CancellationToken cancellationToken = default)
        => dbContext.AgentReplyTasks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.MeetingId == meetingId && item.ReplyMessageId == replyMessageId, cancellationToken);

    public Task<AgentInitialContextDelivery?> FindInitialContextAsync(Guid meetingId, Guid participantId,
        Guid connectionSessionId, CancellationToken cancellationToken = default)
        => dbContext.AgentInitialContextDeliveries.AsNoTracking().SingleOrDefaultAsync(item =>
            item.MeetingId == meetingId && item.ParticipantId == participantId
            && item.ConnectionSessionId == connectionSessionId, cancellationToken);

    public async Task<IReadOnlyList<AgentConnectionSession>> ListConnectedConnectionsAsync(CancellationToken cancellationToken = default)
        => await dbContext.AgentConnectionSessions.AsNoTracking()
            .Where(item => item.State == RoundTableMeeting.Domain.Enums.AgentConnectionState.Connected)
            .ToArrayAsync(cancellationToken);

	public Task<AgentConnectionSession?> FindConnectionAsync(Guid meetingId, Guid connectionSessionId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.AgentConnectionSessions.AsNoTracking().SingleOrDefaultAsync((AgentConnectionSession x) => x.MeetingId == meetingId && x.ID == connectionSessionId, cancellationToken);
	}

	public Task<AgentConnectionSession?> FindConnectedConnectionAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.AgentConnectionSessions.AsNoTracking().SingleOrDefaultAsync((AgentConnectionSession x) => x.MeetingId == meetingId && x.ParticipantId == participantId && (int)x.State == 0, cancellationToken);
	}

	public async Task<bool> DisconnectConnectionAsync(Guid meetingId, Guid participantId, Guid connectionSessionId, string transportBinding, DateTimeOffset disconnectedAtUtc, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (meetingId == Guid.Empty || participantId == Guid.Empty || connectionSessionId == Guid.Empty)
		{
			return false;
		}
		if (string.IsNullOrWhiteSpace(transportBinding))
		{
			return false;
		}
		string normalizedBinding = transportBinding.Trim();
		return await dbContext.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentConnectionSessions\r\nSET State = 'Disconnected',\r\n    DisconnectedAtUtc = {disconnectedAtUtc},\r\n    LastActivityAtUtc = {disconnectedAtUtc}\r\nWHERE MeetingId = {meetingId}\r\n  AND ParticipantId = {participantId}\r\n  AND ID = {connectionSessionId}\r\n  AND State = 'Connected'\r\n  AND McpTransportBinding = {normalizedBinding}\r\n  AND LastActivityAtUtc <= {disconnectedAtUtc}", cancellationToken) == 1;
	}

	public async Task<IReadOnlyList<AgentConnectionSession>> ListConnectionsAsync(Guid meetingId, Guid? participantId = null, CancellationToken cancellationToken = default(CancellationToken))
	{
		IQueryable<AgentConnectionSession> query = from x in dbContext.AgentConnectionSessions.AsNoTracking()
			where x.MeetingId == meetingId
			select x;
		if (participantId.HasValue)
		{
			query = query.Where((AgentConnectionSession x) => x.ParticipantId == ((Guid?)participantId).Value);
		}
		return (from x in await query.ToArrayAsync(cancellationToken)
			orderby x.ConnectedAtUtc descending, x.ID
			select x).ToArray();
	}

	public Task<AgentContextCursor?> FindCursorAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.AgentContextCursors.AsNoTracking().SingleOrDefaultAsync((AgentContextCursor x) => x.MeetingId == meetingId && x.ParticipantId == participantId, cancellationToken);
	}

	public Task<AgentReplyTask?> FindTaskAsync(Guid meetingId, Guid taskId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.AgentReplyTasks.AsNoTracking().SingleOrDefaultAsync((AgentReplyTask x) => x.MeetingId == meetingId && x.ID == taskId, cancellationToken);
	}

	public Task<AgentReplyTask?> FindTaskForAgentAsync(Guid meetingId, Guid agentParticipantId, Guid taskId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.AgentReplyTasks.AsNoTracking().SingleOrDefaultAsync((AgentReplyTask x) => x.MeetingId == meetingId && x.AgentParticipantId == agentParticipantId && x.ID == taskId, cancellationToken);
	}

	public async Task<IReadOnlyList<AgentReplyTask>> ListTasksAsync(Guid meetingId, Guid agentParticipantId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return await (from x in dbContext.AgentReplyTasks.AsNoTracking()
			where x.MeetingId == meetingId && x.AgentParticipantId == agentParticipantId
			orderby x.TriggerSequence, x.ID
			select x).ToArrayAsync(cancellationToken);
	}

	public Task<AgentTaskDeliveryAttempt?> FindDeliveryAttemptAsync(Guid meetingId, Guid taskId, Guid deliveryAttemptId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return dbContext.AgentTaskDeliveryAttempts.AsNoTracking().SingleOrDefaultAsync((AgentTaskDeliveryAttempt x) => x.MeetingId == meetingId && x.TaskId == taskId && x.ID == deliveryAttemptId, cancellationToken);
	}

	public async Task<IReadOnlyList<AgentTaskDeliveryAttempt>> ListDeliveryAttemptsAsync(Guid meetingId, Guid taskId, CancellationToken cancellationToken = default(CancellationToken))
	{
		return (from x in await (from x in dbContext.AgentTaskDeliveryAttempts.AsNoTracking()
				where x.MeetingId == meetingId && x.TaskId == taskId
				select x).ToArrayAsync(cancellationToken)
			orderby x.CreatedAtUtc, x.ID
			select x).ToArray();
	}
}
