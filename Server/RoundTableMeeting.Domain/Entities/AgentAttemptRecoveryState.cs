using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentTaskDeliveryAttempt
{
    public long ConnectionGeneration { get; private set; } = 1;
    public long BindingVersion { get; private set; } = 1;
    public int RetryCycle { get; private set; }
    public string ContextFormatVersion { get; private set; } = "agent-context-v1";
    public DateTimeOffset? ConfirmedAtUtc { get; private set; }
    public DateTimeOffset? ConfirmationDeadlineUtc { get; private set; }
    public DateTimeOffset? InvalidatedAtUtc { get; private set; }
    public string? InvalidationReason { get; private set; }
    public Guid? ReplacementAttemptId { get; private set; }
    public string? ReportedFailureCode { get; private set; }
    public AgentReportedFailureDecision? FailureDecision { get; private set; }

    public static AgentTaskDeliveryAttempt CreateReliable(AgentReplyTask task, AgentConnectionSession connection,
        AgentContextWindow window, DateTimeOffset now, int confirmationSeconds)
    {
        if (task.MeetingId != connection.MeetingId || task.AgentParticipantId != connection.ParticipantId
            || connection.ReliabilityMode != "explicit_ack_v1" || connection.State != AgentConnectionState.Connected
            || connection.LeaseExpiresAtUtc is not DateTimeOffset lease || now >= lease)
            throw new InvalidOperationException("An active matching reliable connection is required.");
        if (window.CandidateFirstSequence != task.CandidateFromSequenceExclusive + 1 || window.CandidateLastSequence != task.TriggerSequence
            || window.DeclaredCharacterLimit != connection.DeclaredContextCharacterLimit)
            throw new ArgumentException("Context does not match the fixed task range and connection budget.");
        ArgumentOutOfRangeException.ThrowIfLessThan(confirmationSeconds, 1);
        var deadline = now.AddSeconds(confirmationSeconds);
        var attempt = Create(task.ID, connection.ID, task.MeetingId, window.CandidateFirstSequence!.Value,
            window.CandidateLastSequence!.Value, window.DeclaredCharacterLimit, window.DeliveredCharacterCount,
            window.Truncated, window.OverLimit, window.TruncatedMessageCount, now);
        attempt.MarkDelivered(window.DeliveredFirstSequence!.Value, window.DeliveredLastSequence!.Value, now);
        attempt.ConnectionGeneration = connection.ConnectionGeneration;
        attempt.BindingVersion = connection.BindingVersion;
        attempt.RetryCycle = task.RetryCycle;
        attempt.ContextFormatVersion = window.ContextFormatVersion;
        attempt.ConfirmationDeadlineUtc = deadline;
        return attempt;
    }

    public bool Confirm(DateTimeOffset now)
    {
        if (InvalidatedAtUtc is not null || State == AgentTaskDeliveryAttemptState.Abandoned)
            throw new InvalidOperationException("The delivery attempt is stale.");
        if (State == AgentTaskDeliveryAttemptState.Confirmed) return false;
        if (State != AgentTaskDeliveryAttemptState.Delivered || ConfirmationDeadlineUtc is not DateTimeOffset deadline)
            throw new InvalidOperationException("The delivery attempt cannot be confirmed.");
        if (now >= deadline) throw new InvalidOperationException("The confirmation deadline has passed.");
        if (now < DeliveredAtUtc) throw new ArgumentOutOfRangeException(nameof(now));
        ConfirmedAtUtc = now;
        State = AgentTaskDeliveryAttemptState.Confirmed;
        return true;
    }

    public bool Invalidate(AgentTaskFailureCode reason, DateTimeOffset now)
    {
        if (InvalidatedAtUtc is not null) return false;
        if (now < (ConfirmedAtUtc ?? DeliveredAtUtc ?? CreatedAtUtc)) throw new ArgumentOutOfRangeException(nameof(now));
        InvalidationReason = AgentTaskFailurePolicy.ToStoredCode(reason);
        InvalidatedAtUtc = now;
        return true;
    }

    public void ReplaceWith(AgentTaskDeliveryAttempt replacement, DateTimeOffset now)
    {
        if (replacement.ID == ID || replacement.TaskId != TaskId || replacement.MeetingId != MeetingId
            || InvalidatedAtUtc is null || ReplacementAttemptId is not null
            || (State == AgentTaskDeliveryAttemptState.Abandoned && ConfirmationDeadlineUtc is not null)
            || replacement.CreatedAtUtc < InvalidatedAtUtc || now < replacement.CreatedAtUtc)
            throw new InvalidOperationException("The replacement does not match this invalidated attempt.");
        // A migrated legacy attempt can already be Abandoned without a successor.
        // Link it once after invalidation; reliable attempts cannot be replaced twice.
        ReplacementAttemptId = replacement.ID;
        State = AgentTaskDeliveryAttemptState.Abandoned;
    }

    public void RecordReportedFailure(AgentTaskFailureCode code, AgentTaskFailureDecision decision)
    {
        if (State != AgentTaskDeliveryAttemptState.Confirmed || InvalidatedAtUtc is not null || ReportedFailureCode is not null
            || code is not (AgentTaskFailureCode.AgentTransientFailure or AgentTaskFailureCode.AgentPermanentFailure))
            throw new InvalidOperationException("The attempt cannot accept this failure report.");
        ReportedFailureCode = AgentTaskFailurePolicy.ToStoredCode(code);
        FailureDecision = new(RetryCycle, decision.RetryExhausted, decision.StopReason, decision.NextRetryAtUtc);
    }
}

public sealed record AgentReportedFailureDecision(int RetryCycle, bool RetryExhausted, AgentTaskStopReason? StopReason, DateTimeOffset? NextRetryAtUtc);
