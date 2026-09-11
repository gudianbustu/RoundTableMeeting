using RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;
using RoundTableMeeting.Application.AgentCollaboration;

namespace RoundTableMeeting.Application.Tests;

public sealed class AgentRecoveryRequestValidatorTests
{
    private static string Key => Convert.ToBase64String(new byte[32]).TrimEnd('=');

    [Fact]
    public void LegacyDefaultsRemainValidAndDoNotRequireRecoveryCredentials()
        => Assert.Null(AgentRecoveryRequestValidator.ValidateJoin(new("code", "Worker", 12000)));

    [Theory]
    [InlineData("legacy_v1", null)]
    [InlineData("unknown", "create")]
    [InlineData("explicit_ack_v1", "invalid")]
    [InlineData("", "create")]
    public void InvalidModeOrCredentialShapeDoesNotBecomeModeRequired(string mode, string? joinMode)
    {
        var request = new AgentJoinRequest("code", "Worker", 12000)
        { ReliabilityMode = mode, JoinMode = joinMode, OperationId = Guid.Empty };
        var result = Assert.IsType<AgentOperationResult>(AgentRecoveryRequestValidator.ValidateJoin(request));
        Assert.Equal("agent_request_invalid", result.ErrorCode);
        if (mode == AgentReliabilityProtocol.ExplicitAck)
            Assert.Equal(new AgentRecoveryErrorData("stop"), result.Data);
        else Assert.Null(result.Data);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("takeover")]
    public void ReliableJoinRequiresValidPrivateCredentials(string joinMode)
    {
        var request = new AgentJoinRequest("code", "Worker", 12000)
        { ReliabilityMode = AgentReliabilityProtocol.ExplicitAck, JoinMode = joinMode };
        Assert.Equal("agent_request_invalid", AgentRecoveryRequestValidator.ValidateJoin(request)?.ErrorCode);
        request = request with { OperationId = Guid.NewGuid(), RecoveryKey = Key };
        Assert.Null(AgentRecoveryRequestValidator.ValidateJoin(request));
        Assert.DoesNotContain(Key, request.ToString());
        Assert.DoesNotContain(Key, new AgentResumeRequest(Guid.NewGuid(), Key, Guid.NewGuid()).ToString());
    }

    [Fact]
    public void WellFormedReliableParametersOnLegacyReturnModeRequiredWithNullData()
    {
        var request = new AgentJoinRequest("code", "Worker", 12000)
        { JoinMode = "takeover", OperationId = Guid.NewGuid(), RecoveryKey = Key };
        var result = Assert.IsType<AgentOperationResult>(AgentRecoveryRequestValidator.ValidateJoin(request));
        Assert.Equal("reliability_mode_required", result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public void RecoveryKeyMustHaveCanonicalEncodingAndExactly32Bytes()
    {
        Assert.True(AgentRecoveryRequestValidator.IsRecoveryKey(Key));
        Assert.False(AgentRecoveryRequestValidator.IsRecoveryKey(Key + "="));
        Assert.False(AgentRecoveryRequestValidator.IsRecoveryKey(Key[..^1] + "B"));
        Assert.False(AgentRecoveryRequestValidator.IsRecoveryKey(new string('A', 42)));
        Assert.False(AgentRecoveryRequestValidator.IsRecoveryKey(new string('A', 44)));
        Assert.False(AgentRecoveryRequestValidator.IsRecoveryKey(new string('+', 43)));
        Assert.False(AgentRecoveryRequestValidator.IsRecoveryKey(null));
    }

    [Fact]
    public void ConfirmationTargetsAreExclusiveAndCannotUseEmptyIdentifiers()
    {
        var initial = new AgentConfirmContextRequest(Guid.NewGuid(), "initial", Guid.NewGuid());
        Assert.Null(AgentRecoveryRequestValidator.ValidateConfirmation(initial));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateConfirmation(initial with { TaskId = Guid.NewGuid() }));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateConfirmation(initial with { InitialDeliveryId = Guid.Empty }));
        var task = new AgentConfirmContextRequest(Guid.NewGuid(), "task", TaskId: Guid.NewGuid(), DeliveryAttemptId: Guid.NewGuid());
        Assert.Null(AgentRecoveryRequestValidator.ValidateConfirmation(task));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateConfirmation(task with { InitialDeliveryId = Guid.NewGuid() }));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateConfirmation(task with { DeliveryAttemptId = null }));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateConfirmation(task with { Kind = "Task" }));
    }

    [Fact]
    public void ResumeAndRetryRequireEveryOperationIdentifier()
    {
        var resume = new AgentResumeRequest(Guid.NewGuid(), Key, Guid.NewGuid());
        Assert.Null(AgentRecoveryRequestValidator.ValidateResume(resume));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateResume(resume with { ConnectionSessionId = Guid.Empty }));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateResume(resume with { RecoveryKey = "bad" }));
        var retry = new AgentRetryRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(AgentRecoveryRequestValidator.ValidateRetry(retry));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateRetry(retry with { OperationId = null }));
        Assert.NotNull(AgentRecoveryRequestValidator.ValidateRetry(retry with { TaskId = Guid.Empty }));
    }
}
