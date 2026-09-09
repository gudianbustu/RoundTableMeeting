using System;

namespace RoundTableMeeting.Abstraction.Services;

public sealed record AgentConnectionRegistration(Guid MeetingId, Guid ParticipantId, Guid ConnectionSessionId, string TransportBinding);
