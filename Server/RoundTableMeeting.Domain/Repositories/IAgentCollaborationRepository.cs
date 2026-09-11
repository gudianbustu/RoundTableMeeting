using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Repositories;

public interface IAgentCollaborationRepository
{
    Task<AgentInitialContextDelivery?> FindInitialContextAsync(Guid meetingId, Guid participantId, Guid connectionSessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentConnectionSession>> ListConnectedConnectionsAsync(CancellationToken cancellationToken = default);

	Task<AgentConnectionSession?> FindConnectionAsync(Guid meetingId, Guid connectionSessionId, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentConnectionSession?> FindConnectedConnectionAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> DisconnectConnectionAsync(Guid meetingId, Guid participantId, Guid connectionSessionId, string transportBinding, DateTimeOffset disconnectedAtUtc, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<AgentConnectionSession>> ListConnectionsAsync(Guid meetingId, Guid? participantId = null, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentContextCursor?> FindCursorAsync(Guid meetingId, Guid participantId, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentReplyTask?> FindTaskAsync(Guid meetingId, Guid taskId, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<AgentReplyTask>> ListTasksAsync(Guid meetingId, Guid agentParticipantId, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentReplyTask?> FindTaskForAgentAsync(Guid meetingId, Guid agentParticipantId, Guid taskId, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentTaskDeliveryAttempt?> FindDeliveryAttemptAsync(Guid meetingId, Guid taskId, Guid deliveryAttemptId, CancellationToken cancellationToken = default(CancellationToken));

	Task<IReadOnlyList<AgentTaskDeliveryAttempt>> ListDeliveryAttemptsAsync(Guid meetingId, Guid taskId, CancellationToken cancellationToken = default(CancellationToken));
}
