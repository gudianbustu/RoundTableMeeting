using System.Text.Json.Serialization;

namespace RoundTableMeeting.Abstraction.Contracts.AgentCollaboration;

/// <summary>Frozen wire identifiers; their presence in code does not advertise server capabilities.</summary>
public static class AgentReliabilityProtocol
{
    public const string Legacy = "legacy_v1";
    public const string ExplicitAck = "explicit_ack_v1";
    public const string Create = "create";
    public const string Takeover = "takeover";
    public const string ConfirmTool = "confirm_context";
    public const string ResumeTool = "resume_agent";
    public const string RenewTool = "renew_agent_lease";
    public const string FailureTool = "report_task_failure";
    public const string RetryTool = "retry_task";

    public static IReadOnlyList<string> Capabilities { get; } = Array.AsReadOnly(new[]
    {
        "explicit_context_ack", "connection_resume", "explicit_takeover",
        "connection_lease", "task_retry", "client_wait_timeout", "multi_agent_collaboration_v1"
    });
}

// Optional extensions preserve the legacy JSON shape and positional constructors.

public sealed partial record AgentJoinRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReliabilityMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JoinMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OperationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryKey { get; init; }

    // Generated record diagnostics must not expose the client-owned recovery secret.
    public override string ToString() => "AgentJoinRequest { redacted }";
}

public sealed partial record AgentWaitRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WaitTimeoutSeconds { get; init; }

}

public sealed partial record AgentStatusRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TaskId { get; init; }

}

public sealed partial record AgentConnectionDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReliabilityMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConnectionGeneration { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BindingVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisconnectReason { get; init; }

}

public sealed partial record AgentJoinResultDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LifecycleProof { get; init; }
    public override string ToString() => "AgentJoinResultDto { redacted }";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JoinDisposition { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Replayed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? InitialDeliveryId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextPurpose { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentLeasePolicyDto? LeasePolicy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Capabilities { get; init; }

}

public sealed partial record AgentMentionResultDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ConfirmationDeadline { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Replayed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReplayReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContextPurpose { get; init; }

}

public sealed partial record AgentReplyResultDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

}

public sealed partial record AgentTaskSummaryDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? CurrentAttemptId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ConfirmationDeadline { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryCycle { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AutomaticRetryCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RetryExhausted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextRetryAt { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ReplyMessageId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }

}

public sealed partial record AgentStatusResultDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? InitialDeliveryId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentTaskSummaryDto? RequestedTask { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAction { get; init; }

}

public sealed partial record AgentOperationResult
{
    public static AgentOperationResult Failure(string code, string message, AgentRecoveryErrorData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new(AgentCollaborationContract.ProtocolVersion, false, code, message, data);
    }
}

public sealed record AgentResumeRequest(Guid? ConnectionSessionId, string? RecoveryKey, Guid? OperationId)
{
    public override string ToString() => "AgentResumeRequest { redacted }";
}

public sealed record AgentRetryRequest(Guid? ConnectionSessionId, Guid? TaskId, Guid? OperationId);

public sealed record AgentConfirmContextRequest(
    Guid? ConnectionSessionId, string? Kind, Guid? InitialDeliveryId = null,
    Guid? TaskId = null, Guid? DeliveryAttemptId = null);

public sealed record AgentLeaseRequest(Guid? ConnectionSessionId);

public sealed record AgentFailureRequest(
    Guid? ConnectionSessionId, Guid? TaskId, Guid? DeliveryAttemptId, string? FailureCode);

public sealed record AgentJoinRecoveryRequiredDto(
    Guid ConnectionSessionId, bool Replayed, string NextAction = AgentReliabilityProtocol.ResumeTool)
{
    public string Kind => "recovery_required";
}

public sealed record AgentResumeResultDto(
    AgentConnectionDto Connection, bool Replayed, Guid? InitialDeliveryId,
    AgentContextDto? InitialContext, AgentTaskSummaryDto? QueueHead,
    AgentTaskSummaryDto? LastRepliedTask, AgentLeasePolicyDto LeasePolicy,
    IReadOnlyList<string> Capabilities, string NextAction)
{
    public string Kind => "resumed";
    public string? ContextPurpose => InitialContext is null ? null : "initial";
}

public sealed record AgentConfirmContextResultDto(
    bool Replayed, long DeliveredThroughSequence, string NextAction,
    Guid? InitialDeliveryId = null, Guid? TaskId = null, Guid? DeliveryAttemptId = null)
{
    public string Kind => "context_confirmed";
    public bool Confirmed => true;
}

public sealed record AgentLeaseResultDto(
    DateTimeOffset ServerTime, DateTimeOffset LeaseExpiresAt, int LeaseRenewIntervalSeconds)
{
    public string Kind => "lease_renewed";
}

public sealed record AgentFailureResultDto(
    Guid TaskId, int RetryCycle, int CurrentRetryCycle, bool Replayed,
    bool RetryExhausted, string? StopReason, DateTimeOffset? NextRetryAt, string NextAction)
{
    public string Kind => "task_failure_recorded";
}

public sealed record AgentRetryResultDto(
    Guid TaskId, int RetryCycle, int CurrentRetryCycle, bool Replayed,
    bool RetryExhausted, string? StopReason, DateTimeOffset? NextRetryAt, string NextAction)
{
    public string Kind => "task_retry_scheduled";
}

public sealed record AgentLeasePolicyDto(
    int ConnectionLeaseSeconds, int LeaseRenewIntervalSeconds, int LeaseSafetyMarginSeconds,
    int ReliableWaitTimeoutSeconds, int MaximumWaitTimeoutSeconds, int ContextConfirmationTimeoutSeconds);

public sealed record AgentRecoveryErrorData(string NextAction)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ServerTime { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentTaskSummaryDto? TaskSummary { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? NextRetryAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RetryExhausted { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CurrentBindingVersion { get; init; }
}
