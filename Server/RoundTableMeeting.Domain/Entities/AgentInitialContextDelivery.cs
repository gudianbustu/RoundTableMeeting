using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed class AgentInitialContextDelivery : BaseEntity
{
    public Guid MeetingId { get; private set; }
    public Guid ParticipantId { get; private set; }
    public Guid ConnectionSessionId { get; private set; }
    public long SnapshotSequence { get; private set; }
    public long? CandidateFirstSequence { get; private set; }
    public long? CandidateLastSequence { get; private set; }
    public long? DeliveredFirstSequence { get; private set; }
    public long? DeliveredLastSequence { get; private set; }
    public int DeclaredCharacterLimit { get; private set; }
    public int DeliveredCharacterCount { get; private set; }
    public bool Truncated { get; private set; }
    public bool OverLimit { get; private set; }
    public int TruncatedMessageCount { get; private set; }
    public string ContextFormatVersion { get; private set; } = string.Empty;
    public InitialContextState State { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    private AgentInitialContextDelivery() { }

    public static AgentInitialContextDelivery Create(Guid meetingId, Guid participantId, Guid connectionSessionId,
        AgentContextWindow window, DateTimeOffset createdAtUtc)
    {
        if (meetingId == Guid.Empty || participantId == Guid.Empty || connectionSessionId == Guid.Empty)
            throw new ArgumentException("Meeting, participant and connection are required.");
        ArgumentNullException.ThrowIfNull(window);
        if (window.CandidateFirstSequence is not null and not 1)
            throw new ArgumentException("Initial context must consider all messages through its snapshot.", nameof(window));
        return new()
        {
            MeetingId = meetingId, ParticipantId = participantId, ConnectionSessionId = connectionSessionId,
            SnapshotSequence = window.SnapshotSequence, CandidateFirstSequence = window.CandidateFirstSequence,
            CandidateLastSequence = window.CandidateLastSequence, DeliveredFirstSequence = window.DeliveredFirstSequence,
            DeliveredLastSequence = window.DeliveredLastSequence, DeclaredCharacterLimit = window.DeclaredCharacterLimit,
            DeliveredCharacterCount = window.DeliveredCharacterCount, Truncated = window.Truncated, OverLimit = window.OverLimit,
            TruncatedMessageCount = window.TruncatedMessageCount, ContextFormatVersion = window.ContextFormatVersion,
            State = InitialContextState.Prepared, CreatedAtUtc = createdAtUtc
        };
    }

    public bool Confirm(DateTimeOffset confirmedAtUtc)
    {
        if (State == InitialContextState.Confirmed) return false;
        if (confirmedAtUtc < CreatedAtUtc) throw new ArgumentOutOfRangeException(nameof(confirmedAtUtc));
        State = InitialContextState.Confirmed;
        ConfirmedAtUtc = confirmedAtUtc;
        return true;
    }

    public AgentContextWindow GetWindow() => new(SnapshotSequence, CandidateFirstSequence, CandidateLastSequence,
        DeliveredFirstSequence, DeliveredLastSequence, DeclaredCharacterLimit, DeliveredCharacterCount,
        Truncated, OverLimit, TruncatedMessageCount, ContextFormatVersion);
}
