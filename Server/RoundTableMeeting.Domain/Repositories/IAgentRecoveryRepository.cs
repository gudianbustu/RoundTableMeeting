using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Entities;

namespace RoundTableMeeting.Domain.Repositories;

public interface IAgentRecoveryRepository
{
    Task<AgentConnectionSession?> FindConnectionAsync(Guid connectionSessionId, CancellationToken cancellationToken = default);
    Task<AgentRecoveryOperation?> FindJoinOperationAsync(Guid meetingId, string normalizedName, Guid operationId, CancellationToken cancellationToken = default);
    Task<AgentRecoveryOperation?> FindResumeOperationAsync(Guid meetingId, Guid connectionSessionId, Guid operationId, CancellationToken cancellationToken = default);
    Task<AgentRecoveryOperation?> FindRetryOperationAsync(Guid meetingId, Guid participantId, Guid taskId, Guid operationId, CancellationToken cancellationToken = default);
    Task<bool> SupersedeAsync(AgentConnectionFence fence, Guid replacementId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> RebindAsync(AgentConnectionFence fence, string newTransportBinding, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> ActivateAsync(AgentConnectionFence fence, DateTimeOffset now, int leaseSeconds, CancellationToken cancellationToken = default);
    Task<bool> RenewAsync(AgentConnectionFence fence, DateTimeOffset now, DateTimeOffset minimumExpiry, CancellationToken cancellationToken = default);
    Task<bool> DisconnectAsync(AgentConnectionFence fence, string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentConnectionSession>> ListUnsettledConnectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentConnectionSession>> ListUnsettledBatchAsync(Guid? afterId, int limit, CancellationToken cancellationToken = default);
}
