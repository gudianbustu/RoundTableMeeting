namespace RoundTableMeeting.Domain.Entities;

// Independent of member and connection rows so offline clients can confirm deletion.
public sealed class MeetingLifecycleProof : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public string ProofHash { get; private set; } = string.Empty;
    public MeetingLifecycleProof() { }
    public static MeetingLifecycleProof Create(Guid meetingId, string proofHash)
    {
        if (meetingId == Guid.Empty || proofHash.Length != 64 || !proofHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Lifecycle proof is invalid.");
        return new() { MeetingId = meetingId, ProofHash = proofHash };
    }
}
