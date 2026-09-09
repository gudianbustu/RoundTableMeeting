using System;

namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentConnectionRegistry
{
    void TerminateMeeting(Guid meetingId);

	bool ContainsTransport(McpTransportSessionKey transportSession);

	bool IsRegistered(Guid meetingId, Guid participantId, Guid connectionSessionId, string transportBinding);

	IReadOnlyList<AgentConnectionRegistration> ListRegistrations(Guid meetingId);

	IReadOnlyList<AgentConnectionRegistration> RemoveRegistrations(McpTransportSessionKey transportSession);

	bool TryGetRegistration(McpTransportSessionKey transportSession, out AgentConnectionRegistration registration);

	bool TryRegister(McpTransportSessionKey transportSession, AgentConnectionRegistration registration);

	bool Unregister(McpTransportSessionKey transportSession, Guid connectionSessionId);
}
