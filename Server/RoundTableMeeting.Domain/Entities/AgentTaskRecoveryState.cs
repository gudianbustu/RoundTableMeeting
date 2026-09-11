using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Entities;

public sealed partial class AgentReplyTask
{
    public bool InitializeLegacyFailureDecision()
    {
        // Legacy Failed has no retry decision. Preserve its cause and timestamps and
        // require an explicit retry rather than creating an unbounded recovery loop.
        if (State != AgentReplyTaskState.Failed || RetryExhausted || StopReason is not null || NextRetryAtUtc is not null)
            return false;
        RetryExhausted = true;
        StopReason = AgentTaskStopReason.PermanentFailure;
        return true;
    }

    public void StopForContextFailure(DateTimeOffset now)
    {
        if (State is not (AgentReplyTaskState.Queued or AgentReplyTaskState.Failed))
            throw new InvalidOperationException("Only a task waiting for delivery can fail context construction.");
        EnsureNotBeforeLatestState(now);
        State = AgentReplyTaskState.Failed;
        FailedAtUtc = now;
        FailureCode = "context_unavailable";
        RetryExhausted = true;
        StopReason = AgentTaskStopReason.PermanentFailure;
        NextRetryAtUtc = null;
    }

    public Guid? CurrentAttemptId { get; private set; }
    public int RetryCycle { get; private set; }
    public int AutomaticRetryCount { get; private set; }
    public DateTimeOffset? NextRetryAtUtc { get; private set; }
    public bool RetryExhausted { get; private set; }
    public AgentTaskStopReason? StopReason { get; private set; }

    public void AttachLegacyAttempt(AgentTaskDeliveryAttempt attempt)
    {
        if (State != AgentReplyTaskState.Delivered || CurrentAttemptId is not null || attempt.TaskId != ID
            || attempt.MeetingId != MeetingId || attempt.State != AgentTaskDeliveryAttemptState.Prepared)
            throw new InvalidOperationException("The legacy attempt does not match the delivered task.");
        CurrentAttemptId = attempt.ID;
    }

    public void DeliverReliable(AgentTaskDeliveryAttempt attempt, AgentTaskDeliveryAttempt? previous, DateTimeOffset now)
    {
        if (State is not (AgentReplyTaskState.Queued or AgentReplyTaskState.Failed)
            || attempt.TaskId != ID || attempt.MeetingId != MeetingId || attempt.RetryCycle != RetryCycle
            || attempt.State != AgentTaskDeliveryAttemptState.Delivered || attempt.InvalidatedAtUtc is not null
            || attempt.ConfirmationDeadlineUtc is not DateTimeOffset deadline || now >= deadline
            || attempt.CreatedAtUtc > now || CurrentAttemptId != previous?.ID)
            throw new InvalidOperationException("The task cannot accept this delivery attempt.");
        EnsureNotBeforeLatestState(now);
        if (State == AgentReplyTaskState.Failed && (RetryExhausted || NextRetryAtUtc is not DateTimeOffset due || now < due))
            throw new InvalidOperationException("The failed task is not ready for retry.");
        var count = previous?.RetryCycle == RetryCycle ? checked(AutomaticRetryCount + 1) : AutomaticRetryCount;
        if (previous is not null) previous.ReplaceWith(attempt, now);
        AutomaticRetryCount = count;
        CurrentAttemptId = attempt.ID;
        State = AgentReplyTaskState.Delivered;
        DeliveredAtUtc = now;
        ProcessingAtUtc = null;
        FailedAtUtc = null;
        FailureCode = null;
        RetryExhausted = false;
        StopReason = null;
        NextRetryAtUtc = null;
    }

    public bool FailReliable(AgentTaskFailureCode code, AgentTaskFailurePolicy policy, DateTimeOffset now)
    {
        if (State == AgentReplyTaskState.Failed) return false;
        var decision = policy.Decide(code, AutomaticRetryCount, now);
        MarkFailed(AgentTaskFailurePolicy.ToStoredCode(code), now);
        RetryExhausted = decision.RetryExhausted;
        StopReason = decision.StopReason;
        NextRetryAtUtc = decision.NextRetryAtUtc;
        return true;
    }

    public void BeginExplicitRetry(DateTimeOffset now)
    {
        if (State != AgentReplyTaskState.Failed || !RetryExhausted) throw new InvalidOperationException("Only an exhausted failed task can begin a new cycle.");
        EnsureNotBeforeLatestState(now);
        var cycle = checked(RetryCycle + 1);
        RetryCycle = cycle;
        AutomaticRetryCount = 0;
        RetryExhausted = false;
        StopReason = null;
        NextRetryAtUtc = now;
    }
}
