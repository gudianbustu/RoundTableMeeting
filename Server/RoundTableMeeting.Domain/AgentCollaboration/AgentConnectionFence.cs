namespace RoundTableMeeting.Domain.AgentCollaboration;

/// <summary>The complete durable ownership expected by one transport operation.</summary>
public sealed record AgentConnectionFence(Guid MeetingId, Guid ParticipantId, Guid ConnectionSessionId,
    long ConnectionGeneration, long BindingVersion, string TransportBinding);
