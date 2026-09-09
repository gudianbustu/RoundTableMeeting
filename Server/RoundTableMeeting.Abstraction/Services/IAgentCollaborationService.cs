using System.Threading;
using System.Threading.Tasks;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentCollaborationService
{
	Task<AgentOperationResult> JoinAsync(AgentJoinRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentOperationResult> WaitAsync(AgentWaitRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentOperationResult> ReplyAsync(AgentReplyRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));

	Task<AgentOperationResult> GetStatusAsync(AgentStatusRequest request, McpTransportSessionKey? transportSession, CancellationToken cancellationToken = default(CancellationToken));
}
