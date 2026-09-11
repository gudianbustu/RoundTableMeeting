using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Abstraction.Services;
using RoundTableMeeting.Application.AgentCollaboration;

namespace RoundTableMeeting.Application.Tests;

public sealed class AgentRecoveryFoundationTests
{
    [Fact]
    public void ConfigurationAllowsIndependentConfirmationDeadlineAndSaturatingBackoff()
    {
        Assert.True(Valid(new()));
        Assert.True(Valid(new() { ContextConfirmationTimeoutSeconds = 1000, MaximumAutomaticRetries = 0 }));
        Assert.True(Valid(new() { MaximumAutomaticRetries = 10, RetryBackoffSeconds = [2] }));
        Assert.False(Valid(new() { ReliableWaitTimeoutSeconds = 301 }));
        Assert.False(Valid(new() { ConnectionLeaseSeconds = 60 }));
        Assert.False(Valid(new() { LeaseRenewIntervalSeconds = 90 }));
        Assert.False(Valid(new() { LeaseRenewIntervalSeconds = int.MaxValue, LeaseSafetyMarginSeconds = int.MaxValue }));
        Assert.False(Valid(new() { RetryBackoffSeconds = [] }));
        Assert.False(Valid(new() { RetryBackoffSeconds = [2, 0] }));
        Assert.False(Valid(new() { RetryBackoffSeconds = null! }));
        Assert.False(Valid(new() { RecoveryScanIntervalSeconds = 0 }));
        Assert.False(Valid(new() { ResumeFailureLimitPerConnection = -1 }));
        Assert.False(Valid(new() { RecoveryRateLimitMaximumBuckets = 0 }));
    }

    [Fact]
    public void RecoveryKeyUsesDecodedBytesAndRejectsMalformedOrWrongSecrets()
    {
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var key = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var digest = AgentRecoveryCredentials.HashKey(key);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), digest);
        Assert.True(AgentRecoveryCredentials.VerifyKey(key, digest));
        Assert.True(AgentRecoveryCredentials.VerifyKey(key, digest.ToLowerInvariant()));
        Assert.False(AgentRecoveryCredentials.VerifyKey(key, null));
        Assert.False(AgentRecoveryCredentials.VerifyKey(key, new string('z', 64)));
        Assert.False(AgentRecoveryCredentials.VerifyKey(key, new string('0', 64)));
        Assert.False(AgentRecoveryCredentials.VerifyKey(key + "=", digest));
        Assert.False(AgentRecoveryCredentials.VerifyKey(null, digest));
    }

    [Fact]
    public void JoinFingerprintNormalizesDefaultModeButSeparatesTakeoverBudgetAndSecret()
    {
        var request = new AgentJoinRequest("meeting", "Worker", 12000)
        { ReliabilityMode = AgentReliabilityProtocol.ExplicitAck, OperationId = Guid.NewGuid(), RecoveryKey = new string('A', 43) };
        var fingerprint = AgentRecoveryCredentials.JoinFingerprint(request);
        Assert.Equal(fingerprint, AgentRecoveryCredentials.JoinFingerprint(request with { JoinMode = "create", SourceAddress = "different-source" }));
        Assert.NotEqual(fingerprint, AgentRecoveryCredentials.JoinFingerprint(request with { JoinMode = "takeover" }));
        Assert.NotEqual(fingerprint, AgentRecoveryCredentials.JoinFingerprint(request with { ContextCharacterLimit = 24000 }));
        Assert.NotEqual(fingerprint, AgentRecoveryCredentials.JoinFingerprint(request with { RecoveryKey = new string('B', 42) + "A" }));
        Assert.DoesNotContain(request.RecoveryKey, request.ToString());
    }

    [Fact]
    public void AttemptsAreLimitedBySourceAndBusinessConnectionAcrossNewOperations()
    {
        var options = new AgentCollaborationOptions { ResumeRateLimitPerSource = 2, ResumeRateLimitPerConnection = 2 };
        var clock = new Clock();
        var limiter = new AgentResumeRateLimiter(options, clock);
        var id = Guid.NewGuid();
        Assert.True(limiter.TryAcquire("source1", id, out _, out _));
        Assert.True(limiter.TryAcquire("source2", id, out _, out _));
        Assert.False(limiter.TryAcquire("source3", id, out _, out var retryAfter));
        Assert.Equal(300, retryAfter);
        Assert.True(limiter.TryAcquire("source1", Guid.NewGuid(), out _, out _));
        Assert.False(limiter.TryAcquire(" SOURCE1 ", Guid.NewGuid(), out _, out _));
        clock.Now = clock.Now.AddSeconds(300);
        Assert.True(limiter.TryAcquire("source1", id, out _, out _));
    }

    [Fact]
    public void FailedCredentialsHaveIndependentLimitsAndSuccessDoesNotClearFailures()
    {
        var options = new AgentCollaborationOptions { ResumeFailureLimitPerSource = 2, ResumeFailureLimitPerConnection = 1 };
        var limiter = new AgentResumeRateLimiter(options, new Clock());
        var id = Guid.NewGuid();
        Assert.True(limiter.TryAcquire("source", id, out var first, out _));
        first!.RecordFailure();
        first.RecordFailure();
        Assert.False(limiter.TryAcquire("another-source", id, out _, out _));
        Assert.True(limiter.TryAcquire("source", Guid.NewGuid(), out _, out _));
        Assert.True(limiter.TryAcquire("source", Guid.NewGuid(), out var second, out _));
        second!.RecordFailure();
        Assert.False(limiter.TryAcquire("source", Guid.NewGuid(), out _, out _));
    }

    [Fact]
    public void CapacityRejectionAllocatesNothingAndExpiredBucketsAreReclaimed()
    {
        var clock = new Clock();
        var limiter = new AgentResumeRateLimiter(new() { RecoveryRateLimitMaximumBuckets = 2 }, clock);
        var id = Guid.NewGuid();
        Assert.True(limiter.TryAcquire("source", id, out _, out _));
        Assert.False(limiter.TryAcquire("new-source", Guid.NewGuid(), out _, out var retryAfter));
        Assert.Equal(300, retryAfter);
        Assert.True(limiter.TryAcquire("source", id, out _, out _));
        clock.Now = clock.Now.AddSeconds(300);
        Assert.True(limiter.TryAcquire("new-source", Guid.NewGuid(), out _, out _));
    }

    [Fact]
    public void LateFailureFromOldWindowCannotPoisonFreshWindow()
    {
        var clock = new Clock();
        var limiter = new AgentResumeRateLimiter(new() { ResumeFailureLimitPerSource = 1 }, clock);
        var id = Guid.NewGuid();
        Assert.True(limiter.TryAcquire("source", id, out var old, out _));
        clock.Now = clock.Now.AddSeconds(300);
        Assert.True(limiter.TryAcquire("source", id, out _, out _));
        old!.RecordFailure();
        Assert.True(limiter.TryAcquire("source", id, out _, out _));
    }

    [Fact]
    public async Task ConcurrentAdmissionsCannotExceedQuota()
    {
        var limiter = new AgentResumeRateLimiter(new() { ResumeRateLimitPerSource = 3 }, new Clock());
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            limiter.TryAcquire("source", Guid.NewGuid(), out _, out _))));
        Assert.Equal(3, results.Count(value => value));
    }

    private static bool Valid(AgentCollaborationOptions options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
