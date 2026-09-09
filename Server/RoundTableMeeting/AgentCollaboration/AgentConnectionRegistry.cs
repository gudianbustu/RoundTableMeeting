using RoundTableMeeting.Abstraction.Services;

namespace RoundTableMeeting.AgentCollaboration;

public sealed class AgentConnectionRegistry : IAgentConnectionRegistry
{
    private readonly Dictionary<McpTransportSessionKey, AgentConnectionRegistration> registrations = [];
    private readonly object sync = new();
    // Retain only the protocol binding until session close to report meeting_not_found.
    private readonly Dictionary<McpTransportSessionKey, AgentConnectionRegistration> terminated = [];

    public void TerminateMeeting(Guid meetingId)
    {
        lock (sync)
        {
            foreach (var pair in registrations.Where(pair => pair.Value.MeetingId == meetingId).ToArray())
            {
                registrations.Remove(pair.Key);
                terminated[pair.Key] = pair.Value;
            }
        }
    }

    public bool ContainsTransport(McpTransportSessionKey transportSession)
    {
        lock (sync) return registrations.ContainsKey(transportSession) || terminated.ContainsKey(transportSession);
    }

    public bool IsRegistered(
        Guid meetingId,
        Guid participantId,
        Guid connectionSessionId,
        string transportBinding)
    {
        lock (sync)
            return registrations.Values.Any(registration =>
                registration.MeetingId == meetingId
                && registration.ParticipantId == participantId
                && registration.ConnectionSessionId == connectionSessionId
                && registration.TransportBinding == transportBinding);
    }

    public IReadOnlyList<AgentConnectionRegistration> ListRegistrations(Guid meetingId)
    {
        lock (sync)
            return registrations.Values.Where(registration => registration.MeetingId == meetingId).ToArray();
    }

    public IReadOnlyList<AgentConnectionRegistration> RemoveRegistrations(McpTransportSessionKey transportSession)
    {
        lock (sync)
        {
            terminated.Remove(transportSession);
            if (!registrations.Remove(transportSession, out var registration))
                return [];
            return [registration];
        }
    }

    public bool TryGetRegistration(
        McpTransportSessionKey transportSession,
        out AgentConnectionRegistration registration)
    {
        lock (sync)
            return registrations.TryGetValue(transportSession, out registration!)
                || terminated.TryGetValue(transportSession, out registration!);
    }

    public bool TryRegister(McpTransportSessionKey transportSession, AgentConnectionRegistration registration)
    {
        if (transportSession.IsEmpty
            || registration.MeetingId == Guid.Empty
            || registration.ParticipantId == Guid.Empty
            || registration.ConnectionSessionId == Guid.Empty
            || string.IsNullOrWhiteSpace(registration.TransportBinding))
            return false;

        lock (sync)
        {
            if (registrations.ContainsKey(transportSession) || terminated.ContainsKey(transportSession)
                || registrations.Values.Any(existing =>
                    existing.MeetingId == registration.MeetingId
                    && existing.ParticipantId == registration.ParticipantId))
                return false;
            registrations.Add(transportSession, registration);
            return true;
        }
    }

    public bool Unregister(McpTransportSessionKey transportSession, Guid connectionSessionId)
    {
        lock (sync)
        {
            if (!registrations.TryGetValue(transportSession, out var registration)
                || registration.ConnectionSessionId != connectionSessionId)
                return false;
            return registrations.Remove(transportSession);
        }
    }
}
