using System;

namespace RoundTableMeeting.Abstraction.Services;

public sealed record AgentConnectionRegistration(Guid MeetingId, Guid ParticipantId, Guid ConnectionSessionId, string TransportBinding)
{
    public long ConnectionGeneration { get; init; }
    public long BindingVersion { get; init; }
    // Negotiated wire format only; durable ownership is still checked by Application.
    public string ReliabilityMode { get; init; } = "legacy_v1";
}
