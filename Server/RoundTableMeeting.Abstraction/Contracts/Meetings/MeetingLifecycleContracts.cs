namespace RoundTableMeeting.Abstraction.Contracts.Meetings;

public sealed record LeaveMeetingRequest(Guid OperationId);
public sealed record MeetingMembershipDto(Guid ParticipantId, string MembershipState, long? VisibleThroughSequence, long MembershipRevision);
public sealed record MeetingPermissionsDto(bool CanReadHistory, bool CanSend, bool CanSubscribe, bool CanInvite, bool CanEdit, bool CanDelete, bool CanLeave, long? VisibleThroughSequence, long MembershipRevision);
public sealed record MeetingDeletionStatusDto(Guid MeetingId, Guid OperationId, string Status, string StatusUrl);
public sealed record MeetingLifecycleStatusRequest(Guid MeetingId, string LifecycleProof)
{
    public override string ToString() => "MeetingLifecycleStatusRequest { redacted }";
}
public sealed record MeetingLifecycleStatusDto(Guid MeetingId, string Status);
