using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.AgentCollaboration;

/// <summary>The single failure classification used by task transitions, status and immutable failure receipts.</summary>
public sealed class AgentTaskFailurePolicy
{
    private readonly int maximumAutomaticRetries;
    private readonly int[] backoffSeconds;

    public AgentTaskFailurePolicy(int maximumAutomaticRetries, IReadOnlyList<int> backoffSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumAutomaticRetries);
        ArgumentNullException.ThrowIfNull(backoffSeconds);
        if (backoffSeconds.Count == 0 || backoffSeconds.Any(seconds => seconds <= 0))
            throw new ArgumentException("Backoff must contain positive durations.", nameof(backoffSeconds));
        this.maximumAutomaticRetries = maximumAutomaticRetries;
        this.backoffSeconds = backoffSeconds.ToArray();
    }

    public AgentTaskFailureDecision Decide(AgentTaskFailureCode code, int automaticRetryCount, DateTimeOffset failedAtUtc)
    {
        if (!Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        ArgumentOutOfRangeException.ThrowIfNegative(automaticRetryCount);
        if (code is AgentTaskFailureCode.AgentPermanentFailure or AgentTaskFailureCode.ContextUnavailable or AgentTaskFailureCode.InternalFailure)
            return new(true, AgentTaskStopReason.PermanentFailure, null);
        if (automaticRetryCount >= maximumAutomaticRetries)
            return new(true, AgentTaskStopReason.AttemptsExhausted, null);
        return new(false, null, failedAtUtc.AddSeconds(backoffSeconds[Math.Min(automaticRetryCount, backoffSeconds.Length - 1)]));
    }

    public static bool TryParseReportedCode(string? value, out AgentTaskFailureCode code)
    {
        code = value switch
        {
            "agent_transient_failure" => AgentTaskFailureCode.AgentTransientFailure,
            "agent_permanent_failure" => AgentTaskFailureCode.AgentPermanentFailure,
            _ => AgentTaskFailureCode.InternalFailure
        };
        return value is "agent_transient_failure" or "agent_permanent_failure";
    }

    public static AgentTaskFailureCode FromStoredCode(string? value) => value switch
    {
        "confirmation_timeout" => AgentTaskFailureCode.ConfirmationTimeout,
        "transport_closed" => AgentTaskFailureCode.TransportClosed,
        "connection_lease_expired" => AgentTaskFailureCode.ConnectionLeaseExpired,
        "connection_resumed" => AgentTaskFailureCode.ConnectionResumed,
        "connection_taken_over" => AgentTaskFailureCode.ConnectionTakenOver,
        "server_restart" => AgentTaskFailureCode.ServerRestart,
        "agent_transient_failure" => AgentTaskFailureCode.AgentTransientFailure,
        "agent_permanent_failure" => AgentTaskFailureCode.AgentPermanentFailure,
        "context_unavailable" => AgentTaskFailureCode.ContextUnavailable,
        _ => AgentTaskFailureCode.InternalFailure
    };

    public static string ToStoredCode(AgentTaskFailureCode code) => code switch
    {
        AgentTaskFailureCode.ConfirmationTimeout => "confirmation_timeout",
        AgentTaskFailureCode.TransportClosed => "transport_closed",
        AgentTaskFailureCode.ConnectionLeaseExpired => "connection_lease_expired",
        AgentTaskFailureCode.ConnectionResumed => "connection_resumed",
        AgentTaskFailureCode.ConnectionTakenOver => "connection_taken_over",
        AgentTaskFailureCode.ServerRestart => "server_restart",
        AgentTaskFailureCode.AgentTransientFailure => "agent_transient_failure",
        AgentTaskFailureCode.AgentPermanentFailure => "agent_permanent_failure",
        AgentTaskFailureCode.ContextUnavailable => "context_unavailable",
        AgentTaskFailureCode.InternalFailure => "internal_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    public static string? ToWire(AgentTaskStopReason? reason) => reason switch
    {
        null => null,
        AgentTaskStopReason.AttemptsExhausted => "attempts_exhausted",
        AgentTaskStopReason.PermanentFailure => "permanent_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}

public sealed record AgentTaskFailureDecision(bool RetryExhausted, AgentTaskStopReason? StopReason, DateTimeOffset? NextRetryAtUtc);
