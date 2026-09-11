namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentContextCursor
{
    public Guid? CurrentConnectionId { get; private set; }
    public long ConnectionGeneration { get; private set; }
    public long FirstJoinSnapshotSequence { get; private set; }
    public string RequiredReliabilityMode { get; private set; } = "legacy_v1";
    public long ActivityRevision { get; private set; }
    public DateTimeOffset ActivityChangedAtUtc { get; private set; }

    public long NextConnectionGeneration()
    {
        if (ConnectionGeneration == MaximumSequence) throw new InvalidOperationException("Connection generation is exhausted.");
        return ConnectionGeneration + 1;
    }

    public void AssignConnection(AgentConnectionSession connection, DateTimeOffset now)
    {
        if (connection.MeetingId != MeetingId || connection.ParticipantId != ParticipantId
            || connection.ConnectionGeneration != NextConnectionGeneration()) throw new InvalidOperationException("Connection ownership does not match the cursor.");
        if (RequiredReliabilityMode == "explicit_ack_v1" && connection.ReliabilityMode != RequiredReliabilityMode)
            throw new InvalidOperationException("A reliable participant cannot be downgraded.");
        EnsureUpdateTime(now);
        CurrentConnectionId = connection.ID;
        ConnectionGeneration = connection.ConnectionGeneration;
        if (connection.ReliabilityMode == "explicit_ack_v1") RequiredReliabilityMode = connection.ReliabilityMode;
        UpdatedAtUtc = now;
    }

    public bool ConfirmThrough(long sequence, DateTimeOffset now) =>
        AdvanceDeliveredThrough(Math.Max(DeliveredThroughSequence, sequence), now);

    public void RecordActivityChange(DateTimeOffset now)
    {
        if (now < ActivityChangedAtUtc) throw new ArgumentOutOfRangeException(nameof(now));
        if (ActivityRevision == MaximumSequence) throw new InvalidOperationException("Activity revision is exhausted.");
        ActivityRevision++;
        ActivityChangedAtUtc = now;
    }
}
