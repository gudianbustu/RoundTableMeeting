using RoundTableMeeting.Domain.AgentCollaboration;
using RoundTableMeeting.Domain.Enums;

namespace RoundTableMeeting.Domain.Tests;

public sealed class AgentTaskFailurePolicyTests
{
    [Theory]
    [InlineData(AgentTaskFailureCode.ConfirmationTimeout)]
    [InlineData(AgentTaskFailureCode.TransportClosed)]
    [InlineData(AgentTaskFailureCode.ConnectionLeaseExpired)]
    [InlineData(AgentTaskFailureCode.ConnectionResumed)]
    [InlineData(AgentTaskFailureCode.ConnectionTakenOver)]
    [InlineData(AgentTaskFailureCode.ServerRestart)]
    [InlineData(AgentTaskFailureCode.AgentTransientFailure)]
    public void RecoverableFailuresUseRemainingQuotaAndSaturatingBackoff(AgentTaskFailureCode code)
    {
        var policy = new AgentTaskFailurePolicy(3, [2, 5]);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(new(false, null, now.AddSeconds(2)), policy.Decide(code, 0, now));
        Assert.Equal(new(false, null, now.AddSeconds(5)), policy.Decide(code, 1, now));
        Assert.Equal(new(false, null, now.AddSeconds(5)), policy.Decide(code, 2, now));
        Assert.Equal(new(true, AgentTaskStopReason.AttemptsExhausted, null), policy.Decide(code, 3, now));
        Assert.Equal(code, AgentTaskFailurePolicy.FromStoredCode(AgentTaskFailurePolicy.ToStoredCode(code)));
    }

    [Theory]
    [InlineData(AgentTaskFailureCode.AgentPermanentFailure)]
    [InlineData(AgentTaskFailureCode.ContextUnavailable)]
    [InlineData(AgentTaskFailureCode.InternalFailure)]
    public void PermanentReasonHasPriorityRegardlessOfConsumedQuota(AgentTaskFailureCode code)
    {
        var policy = new AgentTaskFailurePolicy(3, [2, 5, 15]);
        foreach (var count in new[] { 0, 1, 3, 4 })
            Assert.Equal(new(true, AgentTaskStopReason.PermanentFailure, null), policy.Decide(code, count, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ZeroQuotaStopsAfterFirstFailureAndExternalCodesAreWhitelisted()
    {
        var policy = new AgentTaskFailurePolicy(0, [2]);
        Assert.Equal(AgentTaskStopReason.AttemptsExhausted,
            policy.Decide(AgentTaskFailureCode.AgentTransientFailure, 0, DateTimeOffset.UtcNow).StopReason);
        Assert.True(AgentTaskFailurePolicy.TryParseReportedCode("agent_transient_failure", out var transient));
        Assert.Equal(AgentTaskFailureCode.AgentTransientFailure, transient);
        Assert.True(AgentTaskFailurePolicy.TryParseReportedCode("agent_permanent_failure", out _));
        Assert.False(AgentTaskFailurePolicy.TryParseReportedCode("server_restart", out _));
        Assert.False(AgentTaskFailurePolicy.TryParseReportedCode("AgentTransientFailure", out _));
        Assert.False(AgentTaskFailurePolicy.TryParseReportedCode(null, out _));
        Assert.Equal(AgentTaskFailureCode.InternalFailure, AgentTaskFailurePolicy.FromStoredCode("unknown_legacy_reason"));
    }

    [Fact]
    public void InvalidPolicyOrTimeOverflowCannotScheduleAnUnboundedRetry()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentTaskFailurePolicy(-1, [2]));
        Assert.Throws<ArgumentException>(() => new AgentTaskFailurePolicy(3, []));
        Assert.Throws<ArgumentException>(() => new AgentTaskFailurePolicy(3, [0]));
        var policy = new AgentTaskFailurePolicy(3, [2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Decide((AgentTaskFailureCode)100, 0, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Decide(AgentTaskFailureCode.ServerRestart, -1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Decide(AgentTaskFailureCode.ServerRestart, 0, DateTimeOffset.MaxValue));
    }
}
