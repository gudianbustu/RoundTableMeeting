using System.ComponentModel.DataAnnotations;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

namespace RoundTableMeeting.Application.AgentCollaboration;

public sealed partial class AgentCollaborationOptions : IValidatableObject
{
    [Range(1, int.MaxValue)] public int ReliableWaitTimeoutSeconds { get; set; } = 30;
    [Range(1, int.MaxValue)] public int ConnectionLeaseSeconds { get; set; } = 120;
    [Range(1, int.MaxValue)] public int LeaseRenewIntervalSeconds { get; set; } = 30;
    [Range(1, int.MaxValue)] public int LeaseSafetyMarginSeconds { get; set; } = 30;
    [Range(1, int.MaxValue)] public int ContextConfirmationTimeoutSeconds { get; set; } = 60;
    [Range(1, int.MaxValue)] public int RecoveryScanIntervalSeconds { get; set; } = 5;
    [Range(0, int.MaxValue)] public int MaximumAutomaticRetries { get; set; } = 3;
    public int[] RetryBackoffSeconds { get; set; } = [2, 5, 15];
    [Range(1, int.MaxValue)] public int ResumeRateLimitWindowSeconds { get; set; } = 300;
    [Range(1, int.MaxValue)] public int ResumeRateLimitPerSource { get; set; } = 100;
    [Range(1, int.MaxValue)] public int ResumeRateLimitPerConnection { get; set; } = 30;
    [Range(1, int.MaxValue)] public int ResumeFailureLimitPerSource { get; set; } = 20;
    [Range(1, int.MaxValue)] public int ResumeFailureLimitPerConnection { get; set; } = 10;
    [Range(1, int.MaxValue)] public int RecoveryRateLimitMaximumBuckets { get; set; } = 10000;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinimumContextCharacterLimit > DefaultContextCharacterLimit || DefaultContextCharacterLimit > MaximumContextCharacterLimit)
            yield return new("Context character limits must be ordered minimum <= default <= maximum.");
        if (ReliableWaitTimeoutSeconds > WaitTimeoutSeconds)
            yield return new("Reliable wait timeout cannot exceed the maximum wait timeout.");
        // Widen before addition so invalid large values cannot wrap into a valid configuration.
        if ((long)ReliableWaitTimeoutSeconds + LeaseSafetyMarginSeconds >= ConnectionLeaseSeconds)
            yield return new("Reliable wait plus safety margin must be shorter than the connection lease.");
        if ((long)LeaseRenewIntervalSeconds + LeaseSafetyMarginSeconds >= ConnectionLeaseSeconds)
            yield return new("Renew interval plus safety margin must be shorter than the connection lease.");
        if (RetryBackoffSeconds is null || RetryBackoffSeconds.Length == 0 || RetryBackoffSeconds.Any(value => value <= 0))
            yield return new("Retry backoff must contain positive durations.");
    }

    public AgentLeasePolicyDto ToLeasePolicy() => new(ConnectionLeaseSeconds, LeaseRenewIntervalSeconds,
        LeaseSafetyMarginSeconds, ReliableWaitTimeoutSeconds, WaitTimeoutSeconds, ContextConfirmationTimeoutSeconds);
}
