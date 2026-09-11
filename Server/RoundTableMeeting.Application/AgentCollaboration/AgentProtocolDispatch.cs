using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationService
{
    // This read selects the protocol only. Each implementation revalidates durable ownership
    // within its own transaction before accepting any operation or returning task content.
    private async Task<(bool Reliable, AgentOperationResult? Error)> ResolveProtocolAsync(
        Guid? connectionId, McpTransportSessionKey? transport, bool reliableParameters,
        CancellationToken cancellationToken)
    {
        if (!TryResolveRegistration(connectionId, transport, out var registration)) return (false, null);
        var connection = await agentRepository.FindConnectionAsync(registration.MeetingId, registration.ConnectionSessionId, cancellationToken);
        if (connection is null) return (false, null);
        if (connection.ReliabilityMode == AgentReliabilityProtocol.ExplicitAck) return (true, null);
        if (connection.State == AgentConnectionState.Superseded)
            return (false, Failure("connection_superseded", "The Agent connection was replaced."));
        return reliableParameters
            ? (false, Failure("reliability_mode_required", "These parameters require reliable mode."))
            : (false, null);
    }
}
