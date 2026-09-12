namespace RoundTableMeeting.Domain.Entities;

/// <summary>Persisted deployment boundary for Agent-authored task triggers.</summary>
public sealed class AgentCollaborationBoundary : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public long AgentTriggerStartSequence { get; private set; }

    private AgentCollaborationBoundary() { }

    public static AgentCollaborationBoundary Create(Guid meetingId, long agentTriggerStartSequence = 0)
    {
        if (meetingId == Guid.Empty) throw new ArgumentException("Meeting is required.", nameof(meetingId));
        if (agentTriggerStartSequence is < 0 or > 9_007_199_254_740_991)
            throw new ArgumentOutOfRangeException(nameof(agentTriggerStartSequence));
        return new AgentCollaborationBoundary
        {
            MeetingId = meetingId,
            AgentTriggerStartSequence = agentTriggerStartSequence
        };
    }
}
