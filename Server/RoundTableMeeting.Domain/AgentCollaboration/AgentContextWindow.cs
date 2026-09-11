namespace RoundTableMeeting.Domain.AgentCollaboration;

/// <summary>Immutable metadata for rebuilding the exact context that was offered to an Agent.</summary>
public sealed record AgentContextWindow
{
    public long SnapshotSequence { get; }
    public long? CandidateFirstSequence { get; }
    public long? CandidateLastSequence { get; }
    public long? DeliveredFirstSequence { get; }
    public long? DeliveredLastSequence { get; }
    public int DeclaredCharacterLimit { get; }
    public int DeliveredCharacterCount { get; }
    public bool Truncated { get; }
    public bool OverLimit { get; }
    public int TruncatedMessageCount { get; }
    public string ContextFormatVersion { get; }

    public AgentContextWindow(long snapshotSequence, long? candidateFirstSequence, long? candidateLastSequence,
        long? deliveredFirstSequence, long? deliveredLastSequence, int declaredCharacterLimit,
        int deliveredCharacterCount, bool truncated, bool overLimit, int truncatedMessageCount, string contextFormatVersion)
    {
        if (snapshotSequence is < 0 or > 9007199254740991) throw new ArgumentOutOfRangeException(nameof(snapshotSequence));
        ArgumentOutOfRangeException.ThrowIfLessThan(declaredCharacterLimit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(deliveredCharacterCount);
        ArgumentOutOfRangeException.ThrowIfNegative(truncatedMessageCount);
        if (string.IsNullOrWhiteSpace(contextFormatVersion) || contextFormatVersion.Length > 64)
            throw new ArgumentException("Context format version is required.", nameof(contextFormatVersion));
        if (snapshotSequence == 0)
        {
            if (candidateFirstSequence is not null || candidateLastSequence is not null || deliveredFirstSequence is not null
                || deliveredLastSequence is not null || deliveredCharacterCount != 0 || truncated || overLimit || truncatedMessageCount != 0)
                throw new ArgumentException("An empty snapshot cannot contain a delivery range.");
        }
        else if (candidateFirstSequence is not long first || candidateLastSequence is not long last
            || deliveredFirstSequence is not long deliveredFirst || deliveredLastSequence is not long deliveredLast
            || first < 1 || first > last || last != snapshotSequence || deliveredFirst < first
            || deliveredFirst > deliveredLast || deliveredLast != last || deliveredCharacterCount == 0
            || truncatedMessageCount != deliveredFirst - first)
            throw new ArgumentException("The fixed context range is inconsistent.");
        if (truncated != (truncatedMessageCount > 0) || overLimit != (deliveredCharacterCount > declaredCharacterLimit))
            throw new ArgumentException("The context budget metadata is inconsistent.");
        if (overLimit && deliveredFirstSequence != deliveredLastSequence)
            throw new ArgumentException("Only the final required message may exceed the character budget.");
        SnapshotSequence = snapshotSequence;
        CandidateFirstSequence = candidateFirstSequence;
        CandidateLastSequence = candidateLastSequence;
        DeliveredFirstSequence = deliveredFirstSequence;
        DeliveredLastSequence = deliveredLastSequence;
        DeclaredCharacterLimit = declaredCharacterLimit;
        DeliveredCharacterCount = deliveredCharacterCount;
        Truncated = truncated;
        OverLimit = overLimit;
        TruncatedMessageCount = truncatedMessageCount;
        ContextFormatVersion = contextFormatVersion;
    }
}
