using System.Threading;
using System.Threading.Tasks;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentCollaborationService
{
    Task<AgentOperationResult> ResumeReliableAsync(AgentResumeRequest request, McpTransportSessionKey? transportSession, string? sourceAddress = null, CancellationToken cancellationToken = default);

    Task<AgentOperationResult> ConfirmReliableAsync(AgentConfirmContextRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default);

    Task<AgentOperationResult> RenewReliableAsync(AgentLeaseRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default);

    Task<AgentOperationResult> ReportFailureReliableAsync(AgentFailureRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default);

    Task<AgentOperationResult> RetryReliableAsync(AgentRetryRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default);

	Task<AgentOperationResult> JoinAsync(AgentJoinRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentOperationResult> WaitAsync(AgentWaitRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentOperationResult> ReplyAsync(AgentReplyRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentOperationResult> GetStatusAsync(AgentStatusRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));
}
