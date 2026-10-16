namespace RoundTableMeeting.Domain.Entities;

public sealed class MeetingLeaveOperation : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid ParticipantId { get; private set; }
    public Guid OperationId { get; private set; }
    public long VisibleThroughSequence { get; private set; }
    public long MembershipRevision { get; private set; }
    public MeetingLeaveOperation() { }
    public static MeetingLeaveOperation Create(MeetingParticipant participant, Guid operationId)
    {
        if (operationId == Guid.Empty || participant.LeftThroughSequence is null)
            throw new ArgumentException("A completed leave operation is required.");
        return new() { MeetingId = participant.MeetingId, ParticipantId = participant.ID, OperationId = operationId,
            VisibleThroughSequence = participant.LeftThroughSequence.Value, MembershipRevision = participant.MembershipRevision };
    }
}
