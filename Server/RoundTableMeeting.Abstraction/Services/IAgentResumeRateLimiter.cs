namespace RoundTableMeeting.Abstraction.Services;

public interface IAgentResumeRateLimiter
{
    bool TryAcquire(string? sourceAddress, Guid connectionSessionId,
        out IAgentResumeAttempt? attempt, out int retryAfterSeconds);
}

/// <summary>An admitted attempt can record at most one failed credential check.</summary>
public interface IAgentResumeAttempt
{
    void RecordFailure();
}
